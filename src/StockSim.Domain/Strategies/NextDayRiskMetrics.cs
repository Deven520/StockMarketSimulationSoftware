using StockSim.Domain.Enums;

namespace StockSim.Domain.Strategies;

/// <summary>次日下跌风险：等级判定与对账命中。</summary>
public static class NextDayRiskMetrics
{
    /// <summary>相对现价，预测低点回撤达到该阈值 → Medium。</summary>
    public const decimal MediumDropPct = 4m;

    /// <summary>相对现价，预测低点回撤达到该阈值 → High。</summary>
    public const decimal HighDropPct = 6m;

    /// <summary>对账「强风险命中」：次日收益 ≤ 该阈值。</summary>
    public const decimal StrongHitDropPct = -3m;

    public static decimal ReturnPct(decimal baselinePrice, decimal actualClose)
    {
        if (baselinePrice <= 0 || actualClose <= 0) return 0;
        return Math.Round((actualClose - baselinePrice) / baselinePrice * 100m, 4);
    }

    /// <summary>方向命中：次日确实下跌。</summary>
    public static bool DirectionHit(decimal returnPct) => returnPct < 0m;

    /// <summary>强风险命中：跌幅达到预警强度。</summary>
    public static bool StrongHit(decimal returnPct, decimal thresholdPct = StrongHitDropPct)
        => returnPct <= thresholdPct;

    public static (decimal ReturnPct, bool DirectionHit, bool StrongHit) Evaluate(
        decimal baselinePrice,
        decimal actualClose,
        decimal strongThresholdPct = StrongHitDropPct)
    {
        var r = ReturnPct(baselinePrice, actualClose);
        return (r, DirectionHit(r), StrongHit(r, strongThresholdPct));
    }

    /// <summary>
    /// 由预测低点相对参考价的回撤%判定规则等级。
    /// dropPct 为负或零；内部用绝对值比较阈值。
    /// </summary>
    public static DownsideRiskLevel FromPredictedDropPct(decimal dropPct)
    {
        var abs = Math.Abs(dropPct);
        if (abs >= HighDropPct) return DownsideRiskLevel.High;
        if (abs >= MediumDropPct) return DownsideRiskLevel.Medium;
        return DownsideRiskLevel.Low;
    }

    public static DownsideRiskLevel Max(DownsideRiskLevel a, DownsideRiskLevel b)
        => (DownsideRiskLevel)Math.Max((int)a, (int)b);

    public static string ToLabel(DownsideRiskLevel level) => level switch
    {
        DownsideRiskLevel.High => "高风险",
        DownsideRiskLevel.Medium => "中风险",
        _ => "低风险"
    };

    public static string DefaultSuggestAction(DownsideRiskLevel level, bool hasSellable)
        => level switch
        {
            DownsideRiskLevel.High => hasSellable ? "SellToday" : "Hold",
            DownsideRiskLevel.Medium => hasSellable ? "Reduce" : "Hold",
            _ => "Hold"
        };
}
