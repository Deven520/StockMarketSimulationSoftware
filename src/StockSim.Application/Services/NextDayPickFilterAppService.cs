using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Application.Services;

/// <summary>
/// 次日预涨硬过滤：置信度、追高/弱势/拥挤/大盘、题材命中率、pickHints 消化。
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
            .Select(r => r.FailureTagsJson)
            .ToListAsync(ct);

        var tags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in rows)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list is null) continue;
                foreach (var t in list.Where(x => !string.IsNullOrWhiteSpace(x)))
                    tags[t] = tags.GetValueOrDefault(t) + 1;
            }
            catch { /* ignore */ }
        }

        return NextDayPickGuard.CalibrateThresholds(tags, rows.Count);
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
            {
                try
                {
                    using var doc = JsonDocument.Parse(r.PickHintsJson);
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
        }

        var weakThemes = themes
            .Where(t => t.SampleSize >= 2 && t.DirectionHitRatePct < thresholds.ThemeHitRateFloorPct)
            .Select(t => t.Theme)
            .ToList();

        return new
        {
            windowDays = 14,
            sampleSize = tagRows.Count,
            directionHitRatePct = tagRows.Count == 0
                ? (decimal?)null
                : Math.Round(100m * tagRows.Count(x => x.DirectionHit == true) / tagRows.Count, 1),
            topFailureTags = tagCounts.OrderByDescending(kv => kv.Value).Take(8)
                .Select(kv => new { tag = kv.Key, count = kv.Value }).ToList(),
            themeHitRates = themes.Take(12),
            weakThemesMustDownweight = weakThemes,
            pickHintDigest = hintNotes.Distinct().Take(12).ToList(),
            thresholds = new
            {
                confidenceFloor = thresholds.ConfidenceFloor,
                maxExpectedReturnPct = NextDayPickGuard.MaxExpectedReturnPct,
                chaseHighNearHighPct = thresholds.ChaseHighNearHighPct,
                weakRelative5dThresholdPct = thresholds.WeakRelative5dThresholdPct,
                themeHitRateFloorPct = thresholds.ThemeHitRateFloorPct,
                maxSameThemePerBatch = thresholds.MaxSameThemePerBatch,
                blockHighOpenChase = thresholds.BlockHighOpenChase
            },
            hardRules =
                "入库硬过滤：confidence≥阈值；须有 invalidation；追高/5日相对弱势/弱市门控/流动性；" +
                "同行业一批最多 MaxSameThemePerBatch；弱命中题材须更强催化剂否则剔除。"
        };
    }

    /// <summary>
    /// 对 AI 建议列表做过滤。
    /// strict=false（选股落库）：只拦追高/弱势/流动性/拥挤；缺 confidence/invalidation 仅降权标注，避免整批空窗。
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

        var thresholds = await GetCalibratedThresholdsAsync(ct);
        var themeStats = await GetThemeHitStatsAsync(28, ct);
        var weakThemes = themeStats
            .Where(t => t.SampleSize >= 2 && t.DirectionHitRatePct < thresholds.ThemeHitRateFloorPct)
            .Select(t => t.Theme)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

        var themePenalty = IsWeakTheme(goal.ThemePreference, weakThemes);
        var confidenceFloor = themePenalty
            ? Math.Max(thresholds.ConfidenceFloor, 0.65m)
            : thresholds.ConfidenceFloor;

        foreach (var s in suggestions)
        {
            if (string.IsNullOrWhiteSpace(s.Symbol))
            {
                rejected.Add("(空代码) 已丢弃");
                continue;
            }

            var hasConf = NextDayPickGuard.TryParseConfidence(s.PayloadJson, out var conf);
            if (strict)
            {
                if (!hasConf || conf < confidenceFloor)
                {
                    rejected.Add($"{s.Symbol} 置信度不足（需 ≥{confidenceFloor:0.##}）");
                    continue;
                }

                if (!NextDayPickGuard.HasInvalidation(s.PayloadJson))
                {
                    rejected.Add($"{s.Symbol} 缺少 invalidation 失效情景");
                    continue;
                }
            }
            else if (hasConf && conf < confidenceFloor * 0.7m)
            {
                // 选股阶段仅剔除明确低置信
                rejected.Add($"{s.Symbol} 置信度过低（{conf:0.##}）");
                continue;
            }

            var catalyst = NextDayPickGuard.TryGetCatalystType(s.PayloadJson);
            if (strict && themePenalty && catalyst is not null
                && (catalyst is "news" or "theme"
                    || catalyst.Contains("news", StringComparison.Ordinal)
                    || catalyst.Contains("theme", StringComparison.Ordinal)
                    || catalyst.Contains("政策", StringComparison.Ordinal)))
            {
                rejected.Add($"{s.Symbol} 弱命中题材下催化偏叙事（{catalyst}），入库需更强依据");
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
                    // 无行情：选股阶段放行，入库阶段仍拒绝
                    if (strict)
                    {
                        rejected.Add($"{s.Symbol} {verdict.Reason}");
                        continue;
                    }
                }
                else
                {
                    rejected.Add($"{s.Symbol} {verdict.Reason}");
                    continue;
                }
            }

            var industry = detail?.Fundamentals?.Industry?.Trim();
            if (string.IsNullOrWhiteSpace(industry))
                industry = "未知行业";
            industryCounts.TryGetValue(industry, out var cnt);
            if (cnt >= thresholds.MaxSameThemePerBatch)
            {
                rejected.Add($"{s.Symbol} 同行业「{industry}」超过 {thresholds.MaxSameThemePerBatch} 只");
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
                rejected.Add(
                    "硬过滤后无剩余，已回退保留前 " + fallback.Count +
                    " 只供人工甄别（入库时仍会严格校验）");
                return (fallback, rejected);
            }
        }

        return (accepted, rejected);
    }

    private static bool IsWeakTheme(string theme, HashSet<string> weakThemes)
    {
        if (weakThemes.Contains(theme)) return true;
        // 模糊：历史「有重大利好」与新题材「有利好…」同源
        foreach (var w in weakThemes)
        {
            if (theme.Contains(w, StringComparison.OrdinalIgnoreCase)
                || w.Contains(theme, StringComparison.OrdinalIgnoreCase))
                return true;
            if ((theme.Contains("利好", StringComparison.Ordinal)
                 || theme.Contains("重大", StringComparison.Ordinal))
                && (w.Contains("利好", StringComparison.Ordinal) || w.Contains("重大", StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    public sealed record ThemeHitStat(string Theme, int SampleSize, decimal DirectionHitRatePct);
}
