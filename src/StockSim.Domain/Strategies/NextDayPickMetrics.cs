namespace StockSim.Domain.Strategies;

/// <summary>次日上涨预选：单股对账、命中判定与错因标签。</summary>
public static class NextDayPickMetrics
{
    public const string TagThemeFade = "ThemeFade";
    public const string TagChaseHigh = "ChaseHigh";
    public const string TagWeakRelative = "WeakRelative";
    public const string TagNewsNoise = "NewsNoise";
    public const string TagGapDownOpen = "GapDownOpen";
    public const string TagLiquidityTrap = "LiquidityTrap";
    public const string TagCorrectThesisWrongTiming = "CorrectThesisWrongTiming";
    public const string TagMarketDrag = "MarketDrag";

    /// <summary>
    /// 相对基准价的次日收益%：(收盘−基准)/基准×100。
    /// </summary>
    public static decimal ReturnPct(decimal baselinePrice, decimal actualClose)
    {
        if (baselinePrice <= 0 || actualClose <= 0) return 0;
        return Math.Round((actualClose - baselinePrice) / baselinePrice * 100m, 4);
    }

    /// <summary>方向命中：次日收益 &gt; 0。</summary>
    public static bool DirectionHit(decimal returnPct) => returnPct > 0m;

    /// <summary>目标命中：次日收益 ≥ 预期收益%。</summary>
    public static bool TargetHit(decimal returnPct, decimal expectedReturnPct) =>
        returnPct >= expectedReturnPct;

    public static (decimal ReturnPct, bool DirectionHit, bool TargetHit) Evaluate(
        decimal baselinePrice,
        decimal actualClose,
        decimal expectedReturnPct)
    {
        var r = ReturnPct(baselinePrice, actualClose);
        return (r, DirectionHit(r), TargetHit(r, expectedReturnPct));
    }

    /// <summary>个股收益 − 指数同日收益。</summary>
    public static decimal? RelativeStrengthPct(decimal stockReturnPct, decimal? indexReturnPct)
    {
        if (indexReturnPct is null) return null;
        return Math.Round(stockReturnPct - indexReturnPct.Value, 4);
    }

    /// <summary>根据对账结果给出启发式错因标签（AI 可再修订）。</summary>
    public static IReadOnlyList<string> InferFailureTags(
        bool directionHit,
        decimal returnPct,
        decimal? relativeStrengthPct,
        decimal? openGapPct = null,
        bool? nearHighChase = null)
    {
        if (directionHit) return Array.Empty<string>();

        var tags = new List<string>();
        if (relativeStrengthPct is < -1.5m)
            tags.Add(TagWeakRelative);
        if (indexDragged(returnPct, relativeStrengthPct))
            tags.Add(TagMarketDrag);
        if (openGapPct is < -2m)
            tags.Add(TagGapDownOpen);
        if (nearHighChase == true)
            tags.Add(TagChaseHigh);
        if (returnPct <= -3m && relativeStrengthPct is >= -0.5m and <= 0.5m)
            tags.Add(TagThemeFade);
        if (tags.Count == 0)
            tags.Add(TagCorrectThesisWrongTiming);
        return tags;
    }

    /// <summary>
    /// 合并启发式与 AI 标签：命中清空；未中时保留 OHLC/强弱类标签，AI 可补充，
    /// 若已有具体标签则去掉单独的 CorrectThesisWrongTiming。
    /// </summary>
    public static IReadOnlyList<string> MergeFailureTags(
        bool directionHit,
        IEnumerable<string>? inferred,
        IEnumerable<string>? fromAi = null)
    {
        if (directionHit) return Array.Empty<string>();

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in inferred ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());
        foreach (var t in fromAi ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());

        if (set.Count == 0) return Array.Empty<string>();

        var concrete = set
            .Where(t => !t.Equals(TagCorrectThesisWrongTiming, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (concrete.Count > 0)
            return concrete;

        return new[] { TagCorrectThesisWrongTiming };
    }

    private static bool indexDragged(decimal stockRet, decimal? rs)
        => stockRet < 0 && rs is > 0m; // 个股跌但强于指数 → 大盘拖累为主
}
