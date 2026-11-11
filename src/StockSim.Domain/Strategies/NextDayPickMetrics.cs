namespace StockSim.Domain.Strategies;

/// <summary>次日上涨预选：单股对账与命中判定。</summary>
public static class NextDayPickMetrics
{
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
}
