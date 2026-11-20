using StockSim.Domain.MarketData;

namespace StockSim.Domain.Strategies;

/// <summary>
/// 次日预涨选股硬约束与阈值：预期钳制、追高/弱势/拥挤/大盘门控、confidence。
/// 阈值可由近周复盘标签校准上调。
/// </summary>
public static class NextDayPickGuard
{
    public const decimal DefaultExpectedReturnPct = 1.5m;
    public const decimal MinExpectedReturnPct = 0.5m;
    /// <summary>硬上限；创建目标时优先钳到 SoftMaxExpectedReturnPct。</summary>
    public const decimal MaxExpectedReturnPct = 3.0m;
    /// <summary>入库推荐上限，避免全员卡 3% 导致目标命中虚零。</summary>
    public const decimal SoftMaxExpectedReturnPct = 2.0m;

    public const decimal ConfidenceFloor = 0.55m;
    public const int MaxSameThemePerBatch = 2;
    /// <summary>默认一批最多入选只数；弱市/高 MarketDrag 时下调。</summary>
    public const int MaxPicksPerBatch = 5;
    public const int WeakMarketMaxPicksPerBatch = 3;

    /// <summary>距 20 日高点不足该百分比视为追高区。</summary>
    public const decimal ChaseHighNearHighPct = 3.0m;

    /// <summary>近 1～2 日涨幅超过该值且靠近高点 → 追高。</summary>
    public const decimal ChaseHighRecentGainPct = 5.0m;

    /// <summary>近 5 日相对指数超额低于该值 → 弱势。</summary>
    public const decimal WeakRelative5dThresholdPct = -2.0m;

    /// <summary>指数当日涨跌低于该值 → 弱市，提高门槛。</summary>
    public const decimal IndexWeakDayThresholdPct = -1.5m;

    /// <summary>弱市/绝对方向门控下要求的最低相对强弱（近 5 日）。</summary>
    public const decimal WeakMarketMinRs5dPct = 1.0m;

    /// <summary>题材含「放量」时，近 1 日成交额相对前 5 日均量的最低倍数。</summary>
    public const decimal MinVolumeSurgeRatio = 1.2m;

    /// <summary>题材历史方向命中率低于该值 → 降权/需更强催化剂。</summary>
    public const decimal ThemeHitRateFloorPct = 40.0m;

    /// <summary>日成交额下限（元），过低视为流动性陷阱。</summary>
    public const decimal MinAmountYuan = 80_000_000m;

    /// <summary>题材结构化分级：纯叙事拦截 / 弱约束抬门槛 / 正常。</summary>
    public static NextDayThemeClass ClassifyTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return NextDayThemeClass.Ok;
        var t = theme.Trim();
        var hasSupport = ContainsAny(t, "放量", "量能", "成交额", "超跌", "反弹", "底部", "低位", "企稳", "低估");
        var heavyNarrative = ContainsAny(t, "重大利好", "重磅利好", "重大消息", "突发利好", "重磅消息", "有重大利好");
        if (heavyNarrative && !hasSupport)
            return NextDayThemeClass.NarrativeBlocked;

        // 空心口号（无板块）：抬门槛，迫使写具体板块+催化
        if (IsVagueThemeSlogan(t))
            return NextDayThemeClass.SoftPenalty;

        // 纯「有利好/利好」类、无量能或超跌约束 → 软降权（抬 confidence）
        if (!hasSupport && ContainsAny(t, "利好", "政策催化", "消息催化"))
            return NextDayThemeClass.SoftPenalty;

        // 「利好+放量」但无板块实体 → 软降权；「超跌反弹」等风格词可无板块
        if (ContainsAny(t, "利好") && hasSupport && !HasSectorCue(t))
            return NextDayThemeClass.SoftPenalty;

        return NextDayThemeClass.Ok;
    }

    /// <summary>空心题材口号（无行业/板块实体）。</summary>
    public static bool IsVagueThemeSlogan(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return false;
        var norm = theme.Trim()
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("，", ",", StringComparison.Ordinal)
            .Replace("、", ",", StringComparison.Ordinal);
        return norm is "有利好,持续放量" or "有利好持续放量" or "有利好" or "持续放量"
            or "有重大利好" or "重大利好" or "政策催化" or "消息催化";
    }

    /// <summary>题材是否含板块/行业线索。</summary>
    public static bool HasSectorCue(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return false;
        if (SectorAliases.Keys.Any(k => theme.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return true;
        return SectorAliases.Values.Any(aliases =>
            aliases.Any(a => theme.Contains(a, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>题材是否要求放量硬校验。</summary>
    public static bool RequiresVolumeSurge(string? theme) =>
        !string.IsNullOrWhiteSpace(theme)
        && ContainsAny(theme, "放量", "量能", "持续放量");

    public static string? ThemeClassHint(NextDayThemeClass cls) => cls switch
    {
        NextDayThemeClass.NarrativeBlocked =>
            "题材偏纯叙事（如「重大利好」且无放量/超跌约束），入库将被拦截；建议改为「板块+超跌反弹」或「板块+有利好+持续放量」。",
        NextDayThemeClass.SoftPenalty =>
            "题材偏空心口号或缺少板块实体，将抬高置信度门槛；请写明具体板块/催化并带量能或超跌约束。",
        _ => null
    };

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    public static decimal ClampExpectedReturnPct(decimal expectedReturnPct)
    {
        if (expectedReturnPct < MinExpectedReturnPct) return MinExpectedReturnPct;
        if (expectedReturnPct > MaxExpectedReturnPct) return MaxExpectedReturnPct;
        return Math.Round(expectedReturnPct, 2);
    }

    /// <summary>次日预涨入库用：硬钳制后再压到 SoftMax（默认推荐 ≤2%）。</summary>
    public static decimal ClampExpectedReturnForNextDay(decimal expectedReturnPct)
    {
        var v = ClampExpectedReturnPct(expectedReturnPct);
        if (v > SoftMaxExpectedReturnPct) return SoftMaxExpectedReturnPct;
        return v;
    }

    /// <summary>近 1 日成交额 / 前 5 日均额；数据不足返回 null。</summary>
    public static decimal? TryVolumeSurgeRatio(StockDetailBundle? detail, DateTime asOf)
    {
        if (detail?.DailyBars is null) return null;
        var bars = detail.DailyBars
            .Where(b => b.Date.Date <= asOf.Date && b.Amount > 0)
            .OrderBy(b => b.Date)
            .ToList();
        if (bars.Count < 6) return null;
        var last = bars[^1].Amount;
        var avg5 = bars.Skip(bars.Count - 6).Take(5).Average(b => b.Amount);
        if (avg5 <= 0) return null;
        return Math.Round(last / avg5, 3);
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

    /// <summary>
    /// 根据近周错因标签与方向命中率上调阈值。
    /// CorrectThesisWrongTiming 不参与追高/弱势等占比；MarketDrag 按未中样本占比。
    /// </summary>
    public static NextDayPickThresholds CalibrateThresholds(
        IReadOnlyDictionary<string, int> tagCounts,
        int sampleSize,
        decimal? directionHitRatePct = null,
        int missSampleSize = 0)
    {
        var t = NextDayPickThresholds.Default;
        if (sampleSize < 5) return t;

        var chase = CountTag(tagCounts, NextDayPickMetrics.TagChaseHigh);
        var weak = CountTag(tagCounts, NextDayPickMetrics.TagWeakRelative);
        var gap = CountTag(tagCounts, NextDayPickMetrics.TagGapDownOpen);
        var fade = CountTag(tagCounts, NextDayPickMetrics.TagThemeFade);
        var drag = CountTag(tagCounts, NextDayPickMetrics.TagMarketDrag);

        if (chase * 100m / sampleSize >= 20m)
            t = t with { ChaseHighNearHighPct = 4.0m, ChaseHighRecentGainPct = 4.0m };
        if (weak * 100m / sampleSize >= 25m)
            t = t with { WeakRelative5dThresholdPct = -1.0m };
        if (gap * 100m / sampleSize >= 15m)
            t = t with { BlockHighOpenChase = true };
        if (fade * 100m / sampleSize >= 20m)
            t = t with { ThemeHitRateFloorPct = 45.0m, ConfidenceFloor = 0.60m };

        if (directionHitRatePct is < 45m && sampleSize >= 8)
        {
            t = t with
            {
                ConfidenceFloor = Math.Max(t.ConfidenceFloor, 0.60m),
                IndexWeakDayThresholdPct = -1.0m
            };
        }

        var missBase = missSampleSize > 0 ? missSampleSize : sampleSize;
        var dragPctOfMiss = missBase > 0 ? drag * 100m / missBase : 0m;
        if (missBase >= 5 && dragPctOfMiss >= 40m)
        {
            t = t with
            {
                WeakMarketMinRs5dPct = 1.5m,
                IndexWeakDayThresholdPct = Math.Max(t.IndexWeakDayThresholdPct, -1.0m),
                PreferAbsoluteDirection = true
            };
        }

        // MarketDrag 占未中 ≥50%：同题材上限 1、置信抬到 0.65、弱市缩批次（不依赖总命中率）
        if (missBase >= 5 && dragPctOfMiss >= 50m)
        {
            t = t with
            {
                MaxSameThemePerBatch = 1,
                ConfidenceFloor = Math.Max(t.ConfidenceFloor, 0.65m),
                MaxPicksPerBatch = Math.Min(t.MaxPicksPerBatch, WeakMarketMaxPicksPerBatch),
                PreferAbsoluteDirection = true,
                IndexWeakDayThresholdPct = Math.Max(t.IndexWeakDayThresholdPct, -1.0m),
                WeakMarketMinRs5dPct = Math.Max(t.WeakMarketMinRs5dPct, 1.5m)
            };
        }

        return t;
    }

    /// <summary>指数近 1 日涨跌%（无数据返回 null）。</summary>
    public static decimal? TryIndexReturn1d(StockDetailBundle? indexDetail, DateTime asOf)
    {
        if (indexDetail is null || indexDetail.IsSimulated || indexDetail.DailyBars is null)
            return null;
        var bars = indexDetail.DailyBars
            .Where(b => b.Date.Date <= asOf.Date && b.Close > 0)
            .OrderBy(b => b.Date)
            .ToList();
        return ReturnN(bars, 1);
    }

    /// <summary>是否弱市日（指数近 1 日 ≤ 阈值）。</summary>
    public static bool IsWeakMarketDay(decimal? index1dPct, NextDayPickThresholds thresholds) =>
        index1dPct is not null && index1dPct.Value <= thresholds.IndexWeakDayThresholdPct;

    /// <summary>avoidPatterns 是否含可执行关键词（过长叙事不进硬匹配）。</summary>
    public static bool IsExecutableAvoidPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var p = pattern.Trim();
        if (p.Length < 4 || p.Length > 48) return false;
        return ContainsAny(p,
            "追高", "高开", "低走", "无放量", "放量滞涨", "缺口", "跌停", "弱市", "阴跌",
            "接盘", "一字", "无量", "破位", "杀跌", "冲高回落", "高位放量");
    }

    /// <summary>板块/行业别名：复盘降权主题与候选行业模糊对齐。</summary>
    public static bool SectorAliasMatch(string? text, string? themeOrPattern)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(themeOrPattern))
            return false;
        if (text.Contains(themeOrPattern, StringComparison.OrdinalIgnoreCase)
            || themeOrPattern.Contains(text, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var (key, aliases) in SectorAliases)
        {
            var themeHit = themeOrPattern.Contains(key, StringComparison.OrdinalIgnoreCase)
                           || aliases.Any(a => themeOrPattern.Contains(a, StringComparison.OrdinalIgnoreCase));
            if (!themeHit) continue;
            if (text.Contains(key, StringComparison.OrdinalIgnoreCase)) return true;
            if (aliases.Any(a => text.Contains(a, StringComparison.OrdinalIgnoreCase))) return true;
        }

        return false;
    }

    private static readonly IReadOnlyDictionary<string, string[]> SectorAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["医药"] = new[] { "医疗", "生物", "中药", "化学制药", "医美", "CXO", "创新药", "医保" },
            ["物流"] = new[] { "快递", "航运", "交运", "仓储", "顺丰" },
            ["地产"] = new[] { "房地产", "物业", "建筑装饰", "水泥", "建材" },
            ["新能源"] = new[] { "光伏", "锂电", "储能", "风电", "电池", "逆变器" },
            ["军工"] = new[] { "国防", "航空", "航天", "船舶" },
            ["金融"] = new[] { "银行", "证券", "保险", "券商" },
            ["消费"] = new[] { "白酒", "食品", "零售", "家电", "餐饮" },
            ["半导体"] = new[] { "芯片", "集成电路", "光刻", "晶圆" },
            ["机器人"] = new[] { "工业自动化", "智能制造", "机床" }
        };

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

            // 高 MarketDrag 校准后：即使指数非弱市，也要求 5 日超额达标
            if (thresholds.PreferAbsoluteDirection
                && rs5 < thresholds.WeakMarketMinRs5dPct)
                reasons.Add(
                    $"绝对方向门控：近5日超额 {rs5:N2}% 未达 {thresholds.WeakMarketMinRs5dPct:N1}%");
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
    bool BlockHighOpenChase,
    int MaxPicksPerBatch = NextDayPickGuard.MaxPicksPerBatch,
    bool PreferAbsoluteDirection = false)
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
        BlockHighOpenChase: false,
        MaxPicksPerBatch: NextDayPickGuard.MaxPicksPerBatch,
        PreferAbsoluteDirection: false);
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

public enum NextDayThemeClass
{
    Ok = 0,
    SoftPenalty = 1,
    NarrativeBlocked = 2
}
