using StockSim.Domain.MarketData;

namespace StockSim.Domain.Strategies;

/// <summary>
/// 次日预涨选股硬约束与阈值：预期钳制、追高/弱势/拥挤/大盘门控、confidence。
/// 阈值可由近周复盘标签校准上调。
/// </summary>
public static class NextDayPickGuard
{
    public const decimal DefaultExpectedReturnPct = 2.0m;
    public const decimal MinExpectedReturnPct = 0.5m;
    public const decimal MaxExpectedReturnPct = 3.0m;

    public const decimal ConfidenceFloor = 0.55m;
    public const int MaxSameThemePerBatch = 2;

    /// <summary>距 20 日高点不足该百分比视为追高区。</summary>
    public const decimal ChaseHighNearHighPct = 3.0m;

    /// <summary>近 1～2 日涨幅超过该值且靠近高点 → 追高。</summary>
    public const decimal ChaseHighRecentGainPct = 5.0m;

    /// <summary>近 5 日相对指数超额低于该值 → 弱势。</summary>
    public const decimal WeakRelative5dThresholdPct = -2.0m;

    /// <summary>指数当日涨跌低于该值 → 弱市，提高门槛。</summary>
    public const decimal IndexWeakDayThresholdPct = -1.5m;

    /// <summary>弱市下要求的最低相对强弱（近 5 日）。</summary>
    public const decimal WeakMarketMinRs5dPct = 0.5m;

    /// <summary>题材历史方向命中率低于该值 → 降权/需更强催化剂。</summary>
    public const decimal ThemeHitRateFloorPct = 40.0m;

    /// <summary>日成交额下限（元），过低视为流动性陷阱。</summary>
    public const decimal MinAmountYuan = 80_000_000m;

    public static decimal ClampExpectedReturnPct(decimal expectedReturnPct)
    {
        if (expectedReturnPct < MinExpectedReturnPct) return MinExpectedReturnPct;
        if (expectedReturnPct > MaxExpectedReturnPct) return MaxExpectedReturnPct;
        return Math.Round(expectedReturnPct, 2);
    }

    public static bool TryParseConfidence(string? payloadJson, out decimal confidence)
    {
        confidence = 0;
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("confidence", out var c)) return false;

            if (c.ValueKind == System.Text.Json.JsonValueKind.Number
                && c.TryGetDecimal(out var d))
            {
                confidence = NormalizeConfidenceNumber(d);
                return true;
            }

            if (c.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = c.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(s)) return false;
                if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var ds))
                {
                    confidence = NormalizeConfidenceNumber(ds);
                    return true;
                }

                // AI 常返回 medium / high 等定性值
                confidence = s.ToLowerInvariant() switch
                {
                    "very high" or "veryhigh" or "极高" => 0.9m,
                    "high" or "高" => 0.8m,
                    "medium-high" or "mediumhigh" or "中高" or "中等偏高" => 0.7m,
                    "medium" or "mid" or "中" or "中等" => 0.6m,
                    "medium-low" or "mediumlow" or "中低" => 0.5m,
                    "low" or "低" => 0.4m,
                    "very low" or "verylow" or "极低" => 0.3m,
                    _ => 0m
                };
                return confidence > 0;
            }
        }
        catch
        {
            /* ignore */
        }

        return false;
    }

    /// <summary>0~1 或 0~100 分制统一到 0~1。</summary>
    private static decimal NormalizeConfidenceNumber(decimal d)
    {
        if (d > 1m && d <= 100m) return Math.Round(d / 100m, 4);
        if (d < 0) return 0;
        if (d > 1) return 1;
        return d;
    }

    public static string? TryGetCatalystType(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("catalystType", out var p)
                && p.ValueKind == System.Text.Json.JsonValueKind.String)
                return p.GetString()?.Trim().ToLowerInvariant();
        }
        catch { /* ignore */ }

        return null;
    }

    public static bool HasInvalidation(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("invalidation", out var p)) return false;
            return p.ValueKind == System.Text.Json.JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(p.GetString());
        }
        catch { return false; }
    }

    /// <summary>根据近周错因标签上调阈值（回调）。</summary>
    public static NextDayPickThresholds CalibrateThresholds(
        IReadOnlyDictionary<string, int> tagCounts,
        int sampleSize)
    {
        var t = NextDayPickThresholds.Default;
        if (sampleSize < 5) return t;

        var chase = CountTag(tagCounts, NextDayPickMetrics.TagChaseHigh);
        var weak = CountTag(tagCounts, NextDayPickMetrics.TagWeakRelative);
        var gap = CountTag(tagCounts, NextDayPickMetrics.TagGapDownOpen);
        var fade = CountTag(tagCounts, NextDayPickMetrics.TagThemeFade);

        if (chase * 100m / sampleSize >= 20m)
            t = t with { ChaseHighNearHighPct = 4.0m, ChaseHighRecentGainPct = 4.0m };
        if (weak * 100m / sampleSize >= 25m)
            t = t with { WeakRelative5dThresholdPct = -1.0m };
        if (gap * 100m / sampleSize >= 15m)
            t = t with { BlockHighOpenChase = true };
        if (fade * 100m / sampleSize >= 20m)
            t = t with { ThemeHitRateFloorPct = 45.0m, ConfidenceFloor = 0.60m };

        return t;
    }

    private static int CountTag(IReadOnlyDictionary<string, int> tags, string name) =>
        tags.TryGetValue(name, out var n) ? n : 0;

    /// <summary>基于日K与指数做单股硬过滤。</summary>
    public static NextDayPickFilterVerdict EvaluateLiquidityAndPath(
        StockDetailBundle? detail,
        StockDetailBundle? indexDetail,
        NextDayPickThresholds thresholds,
        DateTime asOf)
    {
        if (detail is null || detail.IsSimulated)
            return NextDayPickFilterVerdict.Reject("无真实行情，跳过硬过滤样本", soft: true);

        var bars = detail.DailyBars?
            .Where(b => b.Date.Date <= asOf.Date && b.Close > 0)
            .OrderBy(b => b.Date)
            .ToList() ?? new List<DailyBar>();
        if (bars.Count < 6)
            return NextDayPickFilterVerdict.Reject("日K不足，无法校验强弱/追高", soft: true);

        var last = bars[^1];
        if (last.Amount > 0 && last.Amount < thresholds.MinAmountYuan)
            return NextDayPickFilterVerdict.Reject(
                $"流动性不足：日成交额 {last.Amount:N0} &lt; {thresholds.MinAmountYuan:N0}");

        var reasons = new List<string>();

        // 追高：靠近 20 日高 + 近 1～2 日大涨
        var lookback = bars.TakeLast(21).ToList();
        var prior = lookback.Count > 1 ? lookback.Take(lookback.Count - 1).ToList() : lookback;
        var high20 = prior.Count > 0 ? prior.Max(b => b.High) : last.High;
        if (high20 > 0)
        {
            var nearPct = (high20 - last.Close) / high20 * 100m;
            if (nearPct <= thresholds.ChaseHighNearHighPct)
            {
                decimal recentGain = 0;
                if (bars.Count >= 2 && bars[^2].Close > 0)
                    recentGain = (last.Close - bars[^2].Close) / bars[^2].Close * 100m;
                if (bars.Count >= 3 && bars[^3].Close > 0)
                {
                    var g2 = (last.Close - bars[^3].Close) / bars[^3].Close * 100m;
                    if (g2 > recentGain) recentGain = g2;
                }

                if (recentGain >= thresholds.ChaseHighRecentGainPct)
                    reasons.Add(
                        $"追高：距20日高仅 {nearPct:N1}% 且近1-2日涨 {recentGain:N1}%");
                else if (thresholds.BlockHighOpenChase
                         && last.Open > 0
                         && TryPrevClose(bars, last) is { } pre
                         && pre > 0
                         && (last.Open - pre) / pre * 100m >= 2m
                         && last.Close <= last.Open)
                    reasons.Add("高开低走形态且处于高位区，禁止追隔夜缺口");
            }
        }

        // 相对强弱 5 日
        var stock5 = ReturnN(bars, 5);
        decimal? index5 = null;
        decimal? index1 = null;
        if (indexDetail is { IsSimulated: false, DailyBars: not null })
        {
            var ib = indexDetail.DailyBars
                .Where(b => b.Date.Date <= asOf.Date && b.Close > 0)
                .OrderBy(b => b.Date)
                .ToList();
            index5 = ReturnN(ib, 5);
            index1 = ReturnN(ib, 1);
        }

        if (stock5 is not null && index5 is not null)
        {
            var rs5 = stock5.Value - index5.Value;
            if (rs5 < thresholds.WeakRelative5dThresholdPct)
                reasons.Add($"相对弱势：近5日超额 {rs5:N2}% &lt; {thresholds.WeakRelative5dThresholdPct:N1}%");

            if (index1.HasValue
                && index1.Value <= thresholds.IndexWeakDayThresholdPct
                && rs5 < thresholds.WeakMarketMinRs5dPct)
                reasons.Add(
                    $"大盘弱市门控：指数近1日 {index1:N2}%，个股5日超额仅 {rs5:N2}%，未达 {thresholds.WeakMarketMinRs5dPct:N1}%");
        }

        if (reasons.Count > 0)
            return NextDayPickFilterVerdict.Reject(string.Join("；", reasons));

        return NextDayPickFilterVerdict.Pass(
            stock5d: stock5,
            index5d: index5,
            rs5d: stock5 is not null && index5 is not null ? stock5 - index5 : null);
    }

    private static decimal? ReturnN(IReadOnlyList<DailyBar> bars, int n)
    {
        if (bars.Count <= n) return null;
        var a = bars[^1].Close;
        var b = bars[^(n + 1)].Close;
        if (b <= 0 || a <= 0) return null;
        return Math.Round((a - b) / b * 100m, 4);
    }

    private static decimal? TryPrevClose(IReadOnlyList<DailyBar> bars, DailyBar last)
    {
        var idx = -1;
        for (var i = 0; i < bars.Count; i++)
            if (bars[i].Date.Date == last.Date.Date) { idx = i; break; }
        if (idx <= 0) return null;
        return bars[idx - 1].Close;
    }
}

public sealed record NextDayPickThresholds(
    decimal ConfidenceFloor,
    decimal ChaseHighNearHighPct,
    decimal ChaseHighRecentGainPct,
    decimal WeakRelative5dThresholdPct,
    decimal IndexWeakDayThresholdPct,
    decimal WeakMarketMinRs5dPct,
    decimal ThemeHitRateFloorPct,
    decimal MinAmountYuan,
    int MaxSameThemePerBatch,
    bool BlockHighOpenChase)
{
    public static NextDayPickThresholds Default { get; } = new(
        NextDayPickGuard.ConfidenceFloor,
        NextDayPickGuard.ChaseHighNearHighPct,
        NextDayPickGuard.ChaseHighRecentGainPct,
        NextDayPickGuard.WeakRelative5dThresholdPct,
        NextDayPickGuard.IndexWeakDayThresholdPct,
        NextDayPickGuard.WeakMarketMinRs5dPct,
        NextDayPickGuard.ThemeHitRateFloorPct,
        NextDayPickGuard.MinAmountYuan,
        NextDayPickGuard.MaxSameThemePerBatch,
        BlockHighOpenChase: false);
}

public sealed record NextDayPickFilterVerdict(
    bool Accepted,
    string? Reason,
    bool SoftSkip,
    decimal? Stock5dPct,
    decimal? Index5dPct,
    decimal? Rs5dPct)
{
    public static NextDayPickFilterVerdict Pass(
        decimal? stock5d = null,
        decimal? index5d = null,
        decimal? rs5d = null) =>
        new(true, null, false, stock5d, index5d, rs5d);

    public static NextDayPickFilterVerdict Reject(string reason, bool soft = false) =>
        new(false, reason, soft, null, null, null);
}
