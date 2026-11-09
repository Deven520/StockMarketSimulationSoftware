using StockSim.Domain.MarketData;
using StockSim.Domain.Trading;

namespace StockSim.Domain.Strategies;

/// <summary>高低价预测的目标日、偏差与准确率计算。</summary>
public static class PriceForecastMetrics
{
    /// <summary>相对偏差容差（%）：|偏差|≤此值时准确率为 100。</summary>
    public const decimal DefaultTolerancePct = 1.5m;

    /// <summary>连续竞价开盘时刻；此前视为开市前。</summary>
    public static readonly TimeSpan MarketOpenTime = new(9, 30, 0);

    /// <summary>连续竞价收盘时刻；当日日K未齐前不对账。</summary>
    public static readonly TimeSpan MarketCloseTime = new(15, 0, 0);

    /// <summary>对账回看自然日上限（含已 Evaluated 的纠错重算）。</summary>
    public const int EvaluationLookbackDays = 14;

    /// <summary>
    /// 是否允许用目标日日K落库对账：不可对未来日；当日须已收盘（≥15:00）。
    /// </summary>
    public static bool CanFinalizeForecast(DateTime forecastForDate, DateTime localNow)
    {
        var target = forecastForDate.Date;
        var today = localNow.Date;
        if (target > today) return false;
        if (target == today && localNow.TimeOfDay < MarketCloseTime) return false;
        return true;
    }

    /// <summary>仅从日K取目标日高低；不使用 Board（昨收盘高低会污染未开盘的「今日」）。</summary>
    public static (decimal High, decimal Low)? ResolveActualHighLowFromDailyBars(
        IEnumerable<DailyBar>? dailyBars,
        DateTime forecastForDate)
    {
        if (dailyBars is null) return null;
        var bar = dailyBars.FirstOrDefault(d => d.Date.Date == forecastForDate.Date);
        if (bar is null || bar.High <= 0 || bar.Low <= 0) return null;
        return (bar.High, bar.Low);
    }

    public static DateTime NextTradingDay(DateTime from)
    {
        var d = from.Date.AddDays(1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            d = d.AddDays(1);
        return d;
    }

    /// <summary>
    /// 交易日且本地时间 &lt; 15:00 → 预测/展示当日高低；
    /// 否则（已收盘后/非交易日）→ 预测下一交易日高低。
    /// </summary>
    public static DateTime ResolveForecastTargetDate(DateTime localNow)
    {
        if (AShareMarketSession.IsTradingDay(localNow) && localNow.TimeOfDay < MarketCloseTime)
            return localNow.Date;
        return NextTradingDay(localNow.Date);
    }

    /// <summary>当日收盘前（含盘中）仍以当日为预测目标日。</summary>
    public static bool IsSameDayForecastWindow(DateTime localNow)
        => AShareMarketSession.IsTradingDay(localNow) && localNow.TimeOfDay < MarketCloseTime;

    /// <summary>兼容旧名：等同 <see cref="IsSameDayForecastWindow"/>。</summary>
    public static bool IsPreOpenForecast(DateTime localNow) => IsSameDayForecastWindow(localNow);

    public static decimal DeviationPct(decimal predicted, decimal actual)
    {
        if (actual == 0) return 0;
        return Math.Round((predicted - actual) / actual * 100m, 4);
    }

    public static decimal AccuracyPct(decimal deviationPct, decimal tolerancePct = DefaultTolerancePct)
    {
        if (tolerancePct <= 0) return 0;
        var score = 100m * (1m - Math.Abs(deviationPct) / tolerancePct);
        return Math.Clamp(Math.Round(score, 2), 0m, 100m);
    }

    public static (
        decimal HighDeviationPct,
        decimal LowDeviationPct,
        decimal HighAccuracyPct,
        decimal LowAccuracyPct,
        decimal CombinedAccuracyPct,
        bool RangeHit) Evaluate(
        decimal predictedHigh,
        decimal predictedLow,
        decimal actualHigh,
        decimal actualLow,
        decimal tolerancePct = DefaultTolerancePct)
    {
        var highDev = DeviationPct(predictedHigh, actualHigh);
        var lowDev = DeviationPct(predictedLow, actualLow);
        var highAcc = AccuracyPct(highDev, tolerancePct);
        var lowAcc = AccuracyPct(lowDev, tolerancePct);
        var combined = Math.Round((highAcc + lowAcc) / 2m, 2);
        var lo = Math.Min(predictedLow, predictedHigh);
        var hi = Math.Max(predictedLow, predictedHigh);
        var rangeHit = actualLow >= lo && actualHigh <= hi;
        return (highDev, lowDev, highAcc, lowAcc, combined, rangeHit);
    }
}
