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
                "弱市日优先绝对方向+相对强弱双达标，禁止硬凑。"
        };
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

        var themeClass = NextDayPickGuard.ClassifyTheme(goal.ThemePreference);
        if (themeClass == NextDayThemeClass.NarrativeBlocked)
        {
            var note = $"题材「{goal.ThemePreference}」为纯叙事（无放量/超跌约束），整批不可入库";
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

        var index1d = NextDayPickGuard.TryIndexReturn1d(index, asOf);
        var weakMarketDay = NextDayPickGuard.IsWeakMarketDay(index1d, thresholds);
        var maxPicks = thresholds.MaxPicksPerBatch;
        if (weakMarketDay || thresholds.PreferAbsoluteDirection)
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

            // 弱市/高 MarketDrag：要求绝对偏强 + 相对不弱（stock5d 不可显著为负）
            if ((weakMarketDay || thresholds.PreferAbsoluteDirection)
                && verdict.Stock5dPct is < 0m
                && (strict || weakMarketDay || thresholds.PreferAbsoluteDirection))
            {
                MarkReject(s,
                    $"{s.Symbol} 弱市闸门：近5日绝对偏弱（{verdict.Stock5dPct:0.##}%），优先绝对收涨候选",
                    rejected);
                continue;
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

        // 选股阶段：若行情硬过滤杀光，回退保留前 3 只（避免队列失败），并注明
        if (!strict && accepted.Count == 0 && suggestions.Count > 0)
        {
            var fallback = suggestions
                .Where(s => !string.IsNullOrWhiteSpace(s.Symbol))
                .Take(3)
                .ToList();
            if (fallback.Count > 0)
            {
                foreach (var s in fallback)
                {
                    s.IsHardRejected = true;
                    s.FilterRejectReason = (s.FilterRejectReason ?? "硬过滤未通过") + "（空窗回退保留，入库仍严格校验）";
                }

                rejected.Add(
                    "硬过滤后无剩余，已回退保留前 " + fallback.Count +
                    " 只供人工甄别（默认不可勾选入库，需强制确认）");
                return (fallback, rejected);
            }
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
