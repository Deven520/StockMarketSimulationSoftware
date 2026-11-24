using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockSim.Application.Dtos;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Application.Services;

/// <summary>
/// 次日预涨硬过滤：置信度、追高/弱势/拥挤/大盘、题材命中率、pickHints 硬消化。
/// </summary>
public sealed class NextDayPickFilterAppService
{
    private readonly IStockSimDataStore _db;
    private readonly IStockDetailProvider _details;
    private readonly ILogger<NextDayPickFilterAppService> _logger;

    public NextDayPickFilterAppService(
        IStockSimDataStore db,
        IStockDetailProvider details,
        ILogger<NextDayPickFilterAppService> logger)
    {
        _db = db;
        _details = details;
        _logger = logger;
    }

    public async Task<NextDayPickThresholds> GetCalibratedThresholdsAsync(CancellationToken ct = default)
    {
        var since = DateTime.Today.AddDays(-14);
        var rows = await _db.NextDayPickReviews.AsNoTracking()
            .Where(r => r.Status == NextDayPickReviewStatus.Succeeded
                        && r.TargetTradeDate >= since)
            .Select(r => new { r.FailureTagsJson, r.DirectionHit })
            .ToListAsync(ct);

        var tags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.FailureTagsJson)) continue;
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(row.FailureTagsJson);
                if (list is null) continue;
                foreach (var t in list.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    // 校准忽略纯 WrongTiming 噪声（单独出现时不计 chase/weak 等）
                    if (t.Equals(NextDayPickMetrics.TagCorrectThesisWrongTiming, StringComparison.OrdinalIgnoreCase)
                        && list.Count == 1)
                        continue;
                    tags[t] = tags.GetValueOrDefault(t) + 1;
                }
            }
            catch { /* ignore */ }
        }

        var hitRate = rows.Count == 0
            ? (decimal?)null
            : Math.Round(100m * rows.Count(x => x.DirectionHit == true) / rows.Count, 1);
        var missCount = rows.Count(x => x.DirectionHit == false);
        return NextDayPickGuard.CalibrateThresholds(tags, rows.Count, hitRate, missCount);
    }

    /// <summary>近两周题材方向命中率（供提示词与降权）。</summary>
    public async Task<IReadOnlyList<ThemeHitStat>> GetThemeHitStatsAsync(
        int lookbackDays = 28,
        CancellationToken ct = default)
    {
        var since = DateTime.Today.AddDays(-Math.Clamp(lookbackDays, 7, 90));
        var rows = await _db.NextDayPickReviews.AsNoTracking()
            .Where(r => r.Status == NextDayPickReviewStatus.Succeeded
                        && r.TargetTradeDate >= since
                        && r.DirectionHit != null
                        && r.ThemePreference != null
                        && r.ThemePreference != "")
            .Select(r => new { r.ThemePreference, r.DirectionHit })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.ThemePreference!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ThemeHitStat(
                g.Key,
                g.Count(),
                Math.Round(100m * g.Count(x => x.DirectionHit == true) / g.Count(), 1)))
            .OrderBy(x => x.DirectionHitRatePct)
            .ToList();
    }

    public async Task<NextDayCalibrationSummaryDto> GetCalibrationSummaryAsync(CancellationToken ct = default)
    {
        var thresholds = await GetCalibratedThresholdsAsync(ct);
        var themes = await GetThemeHitStatsAsync(28, ct);
        var since = DateTime.Today.AddDays(-14);
        var tagRows = await _db.NextDayPickReviews.AsNoTracking()
            .Where(r => r.Status == NextDayPickReviewStatus.Succeeded && r.TargetTradeDate >= since)
            .Select(r => new { r.FailureTagsJson, r.DirectionHit, r.RelativeStrengthPct })
            .ToListAsync(ct);

        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in tagRows)
        {
            if (string.IsNullOrWhiteSpace(r.FailureTagsJson)) continue;
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(r.FailureTagsJson);
                if (list is null) continue;
                foreach (var t in list.Where(x => !string.IsNullOrWhiteSpace(x)))
                    tagCounts[t] = tagCounts.GetValueOrDefault(t) + 1;
            }
            catch { /* ignore */ }
        }

        var weakThemes = themes
            .Where(t => t.SampleSize >= 2 && t.DirectionHitRatePct < thresholds.ThemeHitRateFloorPct)
            .Select(t => t.Theme)
            .ToList();

        var missRows = tagRows.Where(x => x.DirectionHit == false).ToList();
        var drag = CountTag(tagCounts, NextDayPickMetrics.TagMarketDrag);
        decimal? dragMissPct = missRows.Count == 0
            ? null
            : Math.Round(100m * drag / missRows.Count, 1);
        var missButBeat = missRows.Count(x => x.RelativeStrengthPct is > 0);

        return new NextDayCalibrationSummaryDto(
            14,
            tagRows.Count,
            tagRows.Count == 0
                ? null
                : Math.Round(100m * tagRows.Count(x => x.DirectionHit == true) / tagRows.Count, 1),
            tagCounts.OrderByDescending(kv => kv.Value).Take(8)
                .Select(kv => new NextDayTagCountDto(kv.Key, kv.Value)).ToList(),
            themes.Take(12)
                .Select(t => new NextDayThemeHitDto(t.Theme, t.SampleSize, t.DirectionHitRatePct))
                .ToList(),
            weakThemes,
            thresholds.ConfidenceFloor,
            thresholds.IndexWeakDayThresholdPct,
            thresholds.WeakMarketMinRs5dPct,
            thresholds.BlockHighOpenChase,
            thresholds.MaxSameThemePerBatch,
            thresholds.MaxPicksPerBatch,
            thresholds.PreferAbsoluteDirection,
            dragMissPct,
            missRows.Count,
            missButBeat);
    }

    /// <summary>供 GoalToPrompt 注入的校准摘要（含周标签统计与动态阈值）。</summary>
    public async Task<object> LoadCalibrationForPromptAsync(CancellationToken ct = default)
    {
        var thresholds = await GetCalibratedThresholdsAsync(ct);
        var themes = await GetThemeHitStatsAsync(28, ct);
        var since = DateTime.Today.AddDays(-14);
        var tagRows = await _db.NextDayPickReviews.AsNoTracking()
            .Where(r => r.Status == NextDayPickReviewStatus.Succeeded && r.TargetTradeDate >= since)
            .Select(r => new { r.FailureTagsJson, r.PickHintsJson, r.DirectionHit })
            .ToListAsync(ct);

        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hintNotes = new List<string>();
        foreach (var r in tagRows)
        {
            if (!string.IsNullOrWhiteSpace(r.FailureTagsJson))
            {
                try
                {
                    var list = JsonSerializer.Deserialize<List<string>>(r.FailureTagsJson);
                    if (list is not null)
                        foreach (var t in list)
                            tagCounts[t] = tagCounts.GetValueOrDefault(t) + 1;
                }
                catch { /* ignore */ }
            }

            if (!string.IsNullOrWhiteSpace(r.PickHintsJson) && hintNotes.Count < 8)
                AppendHintNotes(r.PickHintsJson, hintNotes);
        }

        var weakThemes = themes
            .Where(t => t.SampleSize >= 2 && t.DirectionHitRatePct < thresholds.ThemeHitRateFloorPct)
            .Select(t => t.Theme)
            .ToList();

        var hitRate = tagRows.Count == 0
            ? (decimal?)null
            : Math.Round(100m * tagRows.Count(x => x.DirectionHit == true) / tagRows.Count, 1);
        var missCount = tagRows.Count(x => x.DirectionHit == false);
        var drag = CountTag(tagCounts, NextDayPickMetrics.TagMarketDrag);
        var dragMissPct = missCount == 0 ? (decimal?)null : Math.Round(100m * drag / missCount, 1);
        var marketDragHeavy = dragMissPct is >= 50m || thresholds.PreferAbsoluteDirection;

        return new
        {
            windowDays = 14,
            sampleSize = tagRows.Count,
            directionHitRatePct = hitRate,
            marketDragMissPct = dragMissPct,
            preferAbsoluteDirection = thresholds.PreferAbsoluteDirection,
            topFailureTags = tagCounts.OrderByDescending(kv => kv.Value).Take(8)
                .Select(kv => new { tag = kv.Key, count = kv.Value }).ToList(),
            themeHitRates = themes.Take(12),
            weakThemesMustDownweight = weakThemes,
            pickHintDigest = hintNotes.Distinct().Take(12).ToList(),
            narrativeThemeRule =
                "禁止以「重大利好/重磅利好」等纯叙事、无放量或超跌约束的题材作为唯一催化；" +
                "优先超跌反弹、有利好+持续放量等可验证约束。",
            marketDragRule = marketDragHeavy || hitRate is < 45m || drag >= 3
                ? "近周 MarketDrag 偏多：指数近1日≤弱市阈时宁缺毋滥（缩批次、抬置信）；" +
                  "须同时追求绝对收涨与相对指数不弱；弱市日勿硬凑满仓。"
                : null,
            weakMarketGate =
                $"指数近1日≤{thresholds.IndexWeakDayThresholdPct:0.#}% 视为弱市：" +
                $"confidence≥{Math.Max(thresholds.ConfidenceFloor, 0.65m):0.##}，" +
                $"RS5d≥{thresholds.WeakMarketMinRs5dPct:0.#}%，" +
                $"同行业≤{thresholds.MaxSameThemePerBatch}，批次≤{Math.Min(thresholds.MaxPicksPerBatch, NextDayPickGuard.WeakMarketMaxPicksPerBatch)}。",
            thresholds = new
            {
                confidenceFloor = thresholds.ConfidenceFloor,
                maxExpectedReturnPct = NextDayPickGuard.MaxExpectedReturnPct,
                chaseHighNearHighPct = thresholds.ChaseHighNearHighPct,
                weakRelative5dThresholdPct = thresholds.WeakRelative5dThresholdPct,
                indexWeakDayThresholdPct = thresholds.IndexWeakDayThresholdPct,
                weakMarketMinRs5dPct = thresholds.WeakMarketMinRs5dPct,
                themeHitRateFloorPct = thresholds.ThemeHitRateFloorPct,
                maxSameThemePerBatch = thresholds.MaxSameThemePerBatch,
                maxPicksPerBatch = thresholds.MaxPicksPerBatch,
                preferAbsoluteDirection = thresholds.PreferAbsoluteDirection,
                blockHighOpenChase = thresholds.BlockHighOpenChase
            },
            hardRules =
                "入库硬过滤：confidence≥阈值；须有 invalidation；追高/5日相对弱势/弱市门控/流动性；" +
                "同行业一批最多 MaxSameThemePerBatch；弱命中/叙事题材须更强催化剂否则剔除；" +
                "须硬消化 pickHints.downweightThemes（含板块别名）/ 可执行 avoidPatterns；" +
                "弱市日优先绝对方向+相对强弱双达标，禁止硬凑。" +
                "超跌/企稳题材：不套用历史 PreferAbsolute 的 RS≥1.5% 永久门控（非弱市日），请按 FormatHardGates 数值输出 8～10 候选。"
        };
    }

    /// <summary>当前校准 + 题材/弱市调整后的硬门槛文案（供选股提示词）。</summary>
    public async Task<(NextDayPickThresholds Thresholds, bool WeakMarketDay, string HardGatesClause)>
        GetHardGatesClauseAsync(string? themePreference, CancellationToken ct = default)
    {
        var thresholds = await GetCalibratedThresholdsAsync(ct);
        StockDetailBundle? index = null;
        try
        {
            index = await _details.GetDetailAsync(
                NextDayPickReviewAppService.IndexSymbol, "创业板指", ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Index unavailable for hard gates");
        }

        var index1d = NextDayPickGuard.TryIndexReturn1d(index, DateTime.Today);
        var weak = NextDayPickGuard.IsWeakMarketDay(index1d, thresholds);
        thresholds = NextDayPickGuard.AdjustForThemeAndSession(
            thresholds, themePreference, weak, preview: true);
        var clause = NextDayPickGuard.FormatHardGatesForPrompt(thresholds, themePreference, weak);
        if (index1d is { } i)
            clause += $"（指数近1日 {i:N2}%{(weak ? "，弱市" : "")}；非弱市不启用 RS≥1.5% 粘滞门控）";
        return (thresholds, weak, clause);
    }

    /// <summary>
    /// 用板块种子 + 附加代码做真源日K预筛，返回已过硬门槛的池（供 AI 池内重选）。
    /// </summary>
    public async Task<IReadOnlyList<NextDaySeedPassDto>> BuildPassingSeedPoolAsync(
        string? themePreference,
        IEnumerable<(string Symbol, string Name)>? extraCandidates = null,
        int maxTake = 24,
        CancellationToken ct = default)
    {
        var thresholds = await GetCalibratedThresholdsAsync(ct);
        StockDetailBundle? index = null;
        try
        {
            index = await _details.GetDetailAsync(
                NextDayPickReviewAppService.IndexSymbol, "创业板指", ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Index unavailable for seed pool");
        }

        var asOf = DateTime.Today;
        var index1d = NextDayPickGuard.TryIndexReturn1d(index, asOf);
        var weak = NextDayPickGuard.IsWeakMarketDay(index1d, thresholds);
        thresholds = NextDayPickGuard.AdjustForThemeAndSession(
            thresholds, themePreference, weak, preview: true);

        var candidates = new List<(string Symbol, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in NextDayPickGuard.ResolveSectorSeedSymbols(themePreference))
        {
            if (seen.Add(s.Symbol))
                candidates.Add(s);
        }

        if (extraCandidates is not null)
        {
            foreach (var e in extraCandidates)
            {
                if (string.IsNullOrWhiteSpace(e.Symbol)) continue;
                if (seen.Add(e.Symbol.Trim()))
                    candidates.Add((e.Symbol.Trim(), e.Name ?? ""));
            }
        }

        var passed = new List<NextDaySeedPassDto>();
        foreach (var (symbol, name) in candidates)
        {
            if (passed.Count >= maxTake) break;
            StockDetailBundle? detail = null;
            try
            {
                detail = await _details.GetDetailAsync(symbol, name, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Seed detail failed {Symbol}", symbol);
            }

            var verdict = NextDayPickGuard.EvaluateLiquidityAndPath(
                detail, index, thresholds, asOf);
            // 严格失败跳过；SoftSkip（多为流动性偏弱但强弱过关）可进池
            if (!verdict.Accepted && !verdict.SoftSkip)
                continue;
            if (!verdict.Accepted && verdict.SoftSkip
                && !(verdict.Reason?.Contains("流动性", StringComparison.Ordinal) ?? false))
                continue;

            var displayName = !string.IsNullOrWhiteSpace(detail?.Board?.Name)
                ? detail!.Board!.Name
                : name;
            passed.Add(new NextDaySeedPassDto(
                symbol,
                displayName ?? symbol,
                verdict.Rs5dPct,
                verdict.Stock5dPct,
                !verdict.Accepted ? verdict.Reason : null));
        }

        return passed
            .OrderByDescending(p => p.Rs5dPct ?? -999m)
            .Take(maxTake)
            .ToList();
    }

    public sealed record NextDaySeedPassDto(
        string Symbol,
        string Name,
        decimal? Rs5dPct,
        decimal? Stock5dPct,
        string? SoftNote);

    /// <summary>Goals 页三块选择器 + 命中率芯片目录。</summary>
    public async Task<NextDayThemeComposerCatalogDto> GetThemeComposerCatalogAsync(CancellationToken ct = default)
    {
        var thresholds = await GetCalibratedThresholdsAsync(ct);
        var themes = await GetThemeHitStatsAsync(28, ct);
        var hints = await LoadPickHintRulesAsync(ct);
        var weakOrDrag = thresholds.PreferAbsoluteDirection;

        StockDetailBundle? index = null;
        try
        {
            index = await _details.GetDetailAsync(
                NextDayPickReviewAppService.IndexSymbol, "创业板指", ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Index detail unavailable for theme catalog");
        }

        var index1d = NextDayPickGuard.TryIndexReturn1d(index, DateTime.Today);
        if (NextDayPickGuard.IsWeakMarketDay(index1d, thresholds))
            weakOrDrag = true;

        var styleAllowed = NextDayThemeComposer.StylesForMarket(weakOrDrag);
        var styleChips = NextDayThemeComposer.StyleOptions.Select(s =>
        {
            var disabled = weakOrDrag && s.HighRisk;
            if (weakOrDrag && styleAllowed.All(a => a.Id != s.Id))
                disabled = true;
            return new NextDayThemeStyleChipDto(
                s.Id, s.Label, s.Phrase, s.Recommended, s.HighRisk, s.RequiresSector, disabled);
        }).ToList();

        var hitMap = themes.ToDictionary(
            t => t.Theme, t => t, StringComparer.OrdinalIgnoreCase);

        NextDayThemeChipDto Chip(string theme, string kind)
        {
            hitMap.TryGetValue(theme, out var st);
            return new NextDayThemeChipDto(
                theme,
                st?.SampleSize ?? 0,
                st?.DirectionHitRatePct,
                kind);
        }

        var recommended = new List<NextDayThemeChipDto>();
        // 风格实证优先
        foreach (var s in new[] { "超跌反弹", "持续放量", "低位企稳" })
        {
            if (hitMap.TryGetValue(s, out var st) && st.DirectionHitRatePct >= 45m)
                recommended.Add(Chip(s, "style"));
            else if (s == "超跌反弹")
                recommended.Add(Chip(s, "style"));
        }

        // 历史高命中题材（非弱、非空心）
        foreach (var t in themes
                     .Where(x => x.SampleSize >= 2
                                 && x.DirectionHitRatePct >= 55m
                                 && !NextDayPickGuard.IsVagueThemeSlogan(x.Theme)
                                 && NextDayPickGuard.ClassifyTheme(x.Theme) != NextDayThemeClass.NarrativeBlocked)
                     .OrderByDescending(x => x.DirectionHitRatePct)
                     .ThenByDescending(x => x.SampleSize)
                     .Take(6))
        {
            if (recommended.Any(r => r.Theme.Equals(t.Theme, StringComparison.OrdinalIgnoreCase)))
                continue;
            recommended.Add(Chip(t.Theme, "history"));
        }

        // 结构化示例芯片
        foreach (var sample in new[] { "半导体超跌反弹", "电网设备持续放量", "医药低位企稳" })
        {
            if (recommended.Count >= 8) break;
            if (recommended.Any(r => r.Theme.Equals(sample, StringComparison.OrdinalIgnoreCase)))
                continue;
            recommended.Add(Chip(sample, "template"));
        }

        var caution = themes
            .Where(t => t.SampleSize >= 2
                        && (t.DirectionHitRatePct < thresholds.ThemeHitRateFloorPct
                            || NextDayPickGuard.IsVagueThemeSlogan(t.Theme)
                            || NextDayPickGuard.ClassifyTheme(t.Theme) == NextDayThemeClass.NarrativeBlocked))
            .OrderBy(t => t.DirectionHitRatePct)
            .Take(6)
            .Select(t => Chip(t.Theme, "caution"))
            .ToList();

        // 保证空心/叙事出现在慎用区
        foreach (var bad in new[] { "有重大利好", "有利好，持续放量", "有利好" })
        {
            if (caution.Any(c => c.Theme.Equals(bad, StringComparison.OrdinalIgnoreCase))) continue;
            caution.Add(Chip(bad, "caution"));
        }

        var note = weakOrDrag
            ? $"弱市/高 MarketDrag 模式：禁用重大利好；推荐超跌反弹或板块+放量"
                + (index1d is { } i ? $"（指数近1日 {i:N2}%）" : "")
            : index1d is { } i2
                ? $"指数近1日 {i2:N2}%；点选推荐芯片或三块组装题材"
                : "点选推荐芯片或三块组装题材；避免空心「有利好」口号";

        return new NextDayThemeComposerCatalogDto(
            NextDayThemeComposer.SectorOptions,
            styleChips,
            NextDayThemeComposer.CatalystOptions,
            recommended,
            caution,
            hints.DownweightThemes.Take(8).ToList(),
            weakOrDrag,
            note);
    }

    /// <summary>
    /// 对 AI 建议列表做过滤。
    /// strict=false（选股落库）：路径硬拦 + 叙事/hints；并写回 IsHardRejected 供 UI 灰显。
    /// strict=true（用户勾选入库）：补齐字段门槛。
    /// </summary>
    public async Task<(IReadOnlyList<AiSuggestion> Accepted, IReadOnlyList<string> RejectNotes)> FilterSuggestionsAsync(
        InvestmentGoal goal,
        IReadOnlyList<AiSuggestion> suggestions,
        bool strict = false,
        CancellationToken ct = default)
    {
        if (goal.TradeStyle != StockTradeStyle.NextDayRise)
            return (suggestions, Array.Empty<string>());

        foreach (var s in suggestions)
        {
            s.IsHardRejected = false;
            s.FilterRejectReason = null;
        }

        var thresholds = await GetCalibratedThresholdsAsync(ct);
        var themeStats = await GetThemeHitStatsAsync(28, ct);
        var weakThemes = themeStats
            .Where(t => t.SampleSize >= 2 && t.DirectionHitRatePct < thresholds.ThemeHitRateFloorPct)
            .Select(t => t.Theme)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hintRules = await LoadPickHintRulesAsync(ct);

        StockDetailBundle? index = null;
        try
        {
            index = await _details.GetDetailAsync(
                NextDayPickReviewAppService.IndexSymbol, "创业板指", ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Index detail unavailable for NextDay filter");
        }

        var rejected = new List<string>();
        var accepted = new List<AiSuggestion>();
        var industryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var asOf = DateTime.Today;

        var index1d = NextDayPickGuard.TryIndexReturn1d(index, asOf);
        var weakMarketDay = NextDayPickGuard.IsWeakMarketDay(index1d, thresholds);
        // 非弱市关掉 PreferAbsolute 粘滞；预检放宽流动性
        thresholds = NextDayPickGuard.AdjustForThemeAndSession(
            thresholds, goal.ThemePreference, weakMarketDay, preview: !strict);
        var meanReversion = NextDayPickGuard.IsMeanReversionTheme(goal.ThemePreference);
        var weakOrDrag = weakMarketDay || thresholds.PreferAbsoluteDirection;

        var themeClass = NextDayPickGuard.ClassifyTheme(goal.ThemePreference);
        // 弱市/高拖累：空心口号升级为整批硬拦
        if (themeClass == NextDayThemeClass.NarrativeBlocked
            || (weakOrDrag && NextDayPickGuard.IsVagueThemeSlogan(goal.ThemePreference)))
        {
            var note = themeClass == NextDayThemeClass.NarrativeBlocked
                ? $"题材「{goal.ThemePreference}」为纯叙事（无放量/超跌约束），整批不可入库"
                : $"弱市下空心题材「{goal.ThemePreference}」整批不可入库，请改用板块+超跌反弹/持续放量";
            foreach (var s in suggestions.Where(x => !string.IsNullOrWhiteSpace(x.Symbol)))
                MarkReject(s, note, rejected);
            return (Array.Empty<AiSuggestion>(), rejected);
        }

        var themePenalty = themeClass == NextDayThemeClass.SoftPenalty
                           || IsWeakTheme(goal.ThemePreference, weakThemes);
        var confidenceFloor = themePenalty
            ? Math.Max(thresholds.ConfidenceFloor, 0.65m)
            : thresholds.ConfidenceFloor;

        // 复盘 downweightThemes 命中当前题材 → 抬门槛（与历史弱题材并列）
        if (hintRules.DownweightThemes.Any(dw =>
                ThemeFuzzyMatch(goal.ThemePreference, dw)
                || NextDayPickGuard.SectorAliasMatch(goal.ThemePreference, dw)))
        {
            themePenalty = true;
            confidenceFloor = Math.Max(confidenceFloor, 0.65m);
        }

        var maxPicks = thresholds.MaxPicksPerBatch;
        if (weakOrDrag)
        {
            confidenceFloor = Math.Max(confidenceFloor, 0.65m);
            maxPicks = Math.Min(maxPicks, NextDayPickGuard.WeakMarketMaxPicksPerBatch);
        }

        foreach (var s in suggestions)
        {
            if (accepted.Count >= maxPicks)
            {
                MarkReject(s, $"{s.Symbol} 超过本批上限 {maxPicks} 只（弱市/校准缩批次）", rejected);
                continue;
            }

            if (string.IsNullOrWhiteSpace(s.Symbol))
            {
                MarkReject(s, "(空代码) 已丢弃", rejected);
                continue;
            }

            var haystack = BuildHaystack(s);

            var avoidHit = hintRules.AvoidPatterns
                .FirstOrDefault(p => NextDayPickGuard.IsExecutableAvoidPattern(p)
                                     && (haystack.Contains(p, StringComparison.OrdinalIgnoreCase)
                                         || ExecutableAvoidKeywordHit(haystack, p)));
            if (avoidHit is not null)
            {
                var msg = $"{s.Symbol} 命中复盘 avoidPatterns「{Truncate(avoidHit, 40)}」";
                if (strict || themePenalty || weakMarketDay)
                {
                    MarkReject(s, msg, rejected);
                    continue;
                }

                s.FilterRejectReason = msg + "（预检提示）";
            }

            var dwHit = hintRules.DownweightThemes
                .FirstOrDefault(dw => ThemeFuzzyMatch(haystack, dw)
                                      || ThemeFuzzyMatch(goal.ThemePreference, dw)
                                      || NextDayPickGuard.SectorAliasMatch(haystack, dw)
                                      || NextDayPickGuard.SectorAliasMatch(goal.ThemePreference, dw));
            if (dwHit is not null && strict && (themePenalty || weakMarketDay))
            {
                MarkReject(s, $"{s.Symbol} 命中复盘降权题材「{Truncate(dwHit, 40)}」", rejected);
                continue;
            }

            var hasConf = NextDayPickGuard.TryParseConfidence(s.PayloadJson, out var conf);
            if (strict)
            {
                if (!hasConf || conf < confidenceFloor)
                {
                    MarkReject(s, $"{s.Symbol} 置信度不足（需 ≥{confidenceFloor:0.##}）", rejected);
                    continue;
                }

                if (!NextDayPickGuard.HasInvalidation(s.PayloadJson))
                {
                    MarkReject(s, $"{s.Symbol} 缺少 invalidation 失效情景", rejected);
                    continue;
                }
            }
            else if (hasConf && conf < confidenceFloor * 0.7m)
            {
                MarkReject(s, $"{s.Symbol} 置信度过低（{conf:0.##}）", rejected);
                continue;
            }

            var catalyst = NextDayPickGuard.TryGetCatalystType(s.PayloadJson);
            if (strict && themePenalty && catalyst is not null
                && (catalyst is "news" or "theme"
                    || catalyst.Contains("news", StringComparison.Ordinal)
                    || catalyst.Contains("theme", StringComparison.Ordinal)
                    || catalyst.Contains("政策", StringComparison.Ordinal)))
            {
                MarkReject(s, $"{s.Symbol} 弱命中题材下催化偏叙事（{catalyst}），入库需更强依据", rejected);
                continue;
            }

            StockDetailBundle? detail = null;
            try
            {
                detail = await _details.GetDetailAsync(s.Symbol, s.Name, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Detail failed for filter {Symbol}", s.Symbol);
            }

            var verdict = NextDayPickGuard.EvaluateLiquidityAndPath(
                detail, index, thresholds, asOf);
            if (!verdict.Accepted)
            {
                if (verdict.SoftSkip)
                {
                    _logger.LogInformation("Soft skip filter {Symbol}: {Reason}", s.Symbol, verdict.Reason);
                    if (strict)
                    {
                        MarkReject(s, $"{s.Symbol} {verdict.Reason}", rejected);
                        continue;
                    }
                }
                else
                {
                    MarkReject(s, $"{s.Symbol} {verdict.Reason}", rejected);
                    continue;
                }
            }

            // 弱市/绝对方向：非均值回归题材要求近5日绝对不显著为负；超跌/企稳仅拦深跌
            if ((weakMarketDay || thresholds.PreferAbsoluteDirection)
                && verdict.Stock5dPct is { } s5
                && (strict || weakMarketDay || thresholds.PreferAbsoluteDirection))
            {
                var floor5d = meanReversion ? -6.0m : 0m;
                if (s5 < floor5d)
                {
                    MarkReject(s,
                        meanReversion
                            ? $"{s.Symbol} 企稳题材闸门：近5日绝对过弱（{s5:0.##}% &lt; {floor5d:0.#}%）"
                            : $"{s.Symbol} 弱市闸门：近5日绝对偏弱（{s5:0.##}%），优先绝对收涨候选",
                        rejected);
                    continue;
                }
            }

            if ((weakMarketDay || thresholds.PreferAbsoluteDirection)
                && verdict.Rs5dPct is { } rs5
                && rs5 < thresholds.WeakMarketMinRs5dPct
                && strict)
            {
                MarkReject(s,
                    $"{s.Symbol} 绝对方向门控：近5日超额 {rs5:0.##}% &lt; {thresholds.WeakMarketMinRs5dPct:0.#}%",
                    rejected);
                continue;
            }

            // 题材含「放量」：近 1 日成交额须明显高于前 5 日均量
            if (strict && NextDayPickGuard.RequiresVolumeSurge(goal.ThemePreference))
            {
                var surge = NextDayPickGuard.TryVolumeSurgeRatio(detail, asOf);
                if (surge is null)
                {
                    MarkReject(s, $"{s.Symbol} 放量主题缺少可校验成交额序列", rejected);
                    continue;
                }

                if (surge.Value < NextDayPickGuard.MinVolumeSurgeRatio)
                {
                    MarkReject(s,
                        $"{s.Symbol} 放量未达标：近1日/前5日均额={surge.Value:0.##}（需≥{NextDayPickGuard.MinVolumeSurgeRatio:0.##}）",
                        rejected);
                    continue;
                }
            }

            var industry = detail?.Fundamentals?.Industry?.Trim();
            if (string.IsNullOrWhiteSpace(industry))
                industry = "未知行业";

            if (hintRules.DownweightThemes.Any(dw =>
                    NextDayPickGuard.SectorAliasMatch(industry, dw)
                    || ThemeFuzzyMatch(industry, dw)))
            {
                if (strict && (themePenalty || weakMarketDay || thresholds.PreferAbsoluteDirection))
                {
                    MarkReject(s, $"{s.Symbol} 行业「{industry}」命中复盘降权板块", rejected);
                    continue;
                }
            }

            industryCounts.TryGetValue(industry, out var cnt);
            if (cnt >= thresholds.MaxSameThemePerBatch)
            {
                MarkReject(s, $"{s.Symbol} 同行业「{industry}」超过 {thresholds.MaxSameThemePerBatch} 只", rejected);
                continue;
            }

            industryCounts[industry] = cnt + 1;
            accepted.Add(s);
        }

        // 选股阶段：全灭时不再假「通过」；保留拒因供上层拒因重试 / 强制入库
        if (!strict && accepted.Count == 0 && suggestions.Count > 0)
        {
            rejected.Insert(0,
                $"硬过滤后通过 0/{suggestions.Count} 只（非假通过）；将触发拒因重试或需强制入库");
        }

        return (accepted, rejected);
    }

    private async Task<PickHintRules> LoadPickHintRulesAsync(CancellationToken ct)
    {
        var since = DateTime.Today.AddDays(-14);
        var jsons = await _db.NextDayPickReviews.AsNoTracking()
            .Where(r => r.Status == NextDayPickReviewStatus.Succeeded
                        && r.TargetTradeDate >= since
                        && r.PickHintsJson != null
                        && r.PickHintsJson != "")
            .OrderByDescending(r => r.TargetTradeDate)
            .Select(r => r.PickHintsJson!)
            .Take(40)
            .ToListAsync(ct);

        var down = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var avoid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in jsons)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("downweightThemes", out var dw)
                    && dw.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in dw.EnumerateArray())
                    {
                        var s = x.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(s) && s.Length >= 2)
                            down.Add(s);
                    }
                }

                if (doc.RootElement.TryGetProperty("avoidPatterns", out var ap)
                    && ap.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in ap.EnumerateArray())
                    {
                        var s = x.GetString()?.Trim();
                        if (NextDayPickGuard.IsExecutableAvoidPattern(s))
                            avoid.Add(s!);
                    }
                }
            }
            catch { /* ignore */ }
        }

        return new PickHintRules(down.Take(24).ToList(), avoid.Take(24).ToList());
    }

    private static void AppendHintNotes(string pickHintsJson, List<string> hintNotes)
    {
        try
        {
            using var doc = JsonDocument.Parse(pickHintsJson);
            if (doc.RootElement.TryGetProperty("notes", out var n)
                && n.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(n.GetString()))
                hintNotes.Add(n.GetString()!.Trim());
            if (doc.RootElement.TryGetProperty("avoidPatterns", out var ap)
                && ap.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in ap.EnumerateArray())
                {
                    var s = x.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && hintNotes.Count < 12)
                        hintNotes.Add("avoid:" + s.Trim());
                }
            }

            if (doc.RootElement.TryGetProperty("downweightThemes", out var dw)
                && dw.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in dw.EnumerateArray())
                {
                    var s = x.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && hintNotes.Count < 12)
                        hintNotes.Add("downweightTheme:" + s.Trim());
                }
            }
        }
        catch { /* ignore */ }
    }

    private static void MarkReject(AiSuggestion s, string reason, List<string> rejected)
    {
        s.IsHardRejected = true;
        s.FilterRejectReason = reason;
        rejected.Add(reason);
    }

    private static string BuildHaystack(AiSuggestion s) =>
        string.Join('\n', new[] { s.Symbol, s.Name, s.Reason, s.SelectionLogic, s.Risk, s.PayloadJson }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

    private static bool ThemeFuzzyMatch(string? text, string pattern)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(pattern)) return false;
        if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase)
            || pattern.Contains(text, StringComparison.OrdinalIgnoreCase))
            return true;
        return NextDayPickGuard.SectorAliasMatch(text, pattern);
    }

    /// <summary>长 avoid 句中抽可执行关键词与 haystack 匹配。</summary>
    private static bool ExecutableAvoidKeywordHit(string haystack, string pattern)
    {
        string[] keys =
        [
            "追高", "高开低走", "高开", "无放量", "放量滞涨", "弱市", "阴跌", "破位", "冲高回落", "高位放量"
        ];
        foreach (var k in keys)
        {
            if (pattern.Contains(k, StringComparison.OrdinalIgnoreCase)
                && haystack.Contains(k, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsWeakTheme(string theme, HashSet<string> weakThemes)
    {
        if (weakThemes.Contains(theme)) return true;
        foreach (var w in weakThemes)
        {
            if (theme.Contains(w, StringComparison.OrdinalIgnoreCase)
                || w.Contains(theme, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int CountTag(IReadOnlyDictionary<string, int> tags, string name) =>
        tags.TryGetValue(name, out var n) ? n : 0;

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s[..n] + "…";

    private sealed record PickHintRules(
        IReadOnlyList<string> DownweightThemes,
        IReadOnlyList<string> AvoidPatterns);

    public sealed record ThemeHitStat(string Theme, int SampleSize, decimal DirectionHitRatePct);
}
