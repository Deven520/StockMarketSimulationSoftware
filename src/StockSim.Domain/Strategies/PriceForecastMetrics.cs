using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Trading;

namespace StockSim.Domain.Strategies;

/// <summary>高低价预测的目标日、偏差、准确率、ATR 基线与校准。</summary>
public static class PriceForecastMetrics
{
    /// <summary>相对偏差容差（%）：平均绝对偏差≤此值时综合准确率为 100。</summary>
    public const decimal DefaultTolerancePct = 5.0m;

    /// <summary>连续竞价开盘时刻；此前视为开市前。</summary>
    public static readonly TimeSpan MarketOpenTime = new(9, 30, 0);

    /// <summary>连续竞价收盘时刻；当日日K未齐前不对账。</summary>
    public static readonly TimeSpan MarketCloseTime = new(15, 0, 0);

    /// <summary>对账回看自然日上限（含已 Evaluated 的纠错重算）。</summary>
    public const int EvaluationLookbackDays = 14;

    /// <summary>ATR 基线默认周期。</summary>
    public const int DefaultAtrPeriod = 14;

    /// <summary>ATR 倍数：预测高/低 = 参考价 ± ATR×倍数。</summary>
    public const decimal DefaultAtrMultiplier = 1.0m;

    /// <summary>做 T 软边界：卖出须进入预测区间上沿比例，买入须进入下沿比例。</summary>
    public const decimal ForecastSoftBandRatio = 0.35m;

    /// <summary>近史综合准确率低于此值时，夜更对策略做降权（更保守）。</summary>
    public const decimal LowAccuracyDeweightThreshold = 40m;

    /// <summary>对账偏差绝对值超过此值视为异常样本，不计入准确率。</summary>
    public const decimal OutlierAbsDeviationPct = 30m;

    /// <summary>偏差校准最少有效样本数。</summary>
    public const int MinCalibrationSamples = 5;

    /// <summary>单次校准允许的最大均值偏差幅度（%）。</summary>
    public const decimal MaxCalibrationAdjustPct = 5m;

    /// <summary>预测区间相对 ATR 的最小/最大带宽倍数。</summary>
    public const decimal MinAtrBandMultiple = 0.6m;
    public const decimal MaxAtrBandMultiple = 1.8m;

    /// <summary>默认相对昨收的涨跌幅硬钳制（%）；创业板/科创可更高。</summary>
    public const decimal DefaultLimitCapPct = 12m;

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

    /// <summary>绝对误差（元）：|预测−实际|。</summary>
    public static decimal AbsoluteError(decimal predicted, decimal actual)
        => Math.Round(Math.Abs(predicted - actual), 4);

    /// <summary>相对误差%：高/低绝对偏差%的平均。</summary>
    public static decimal MeanAbsDeviationPct(decimal highDeviationPct, decimal lowDeviationPct)
        => Math.Round((Math.Abs(highDeviationPct) + Math.Abs(lowDeviationPct)) / 2m, 4);

    public static decimal AccuracyPct(decimal deviationPct, decimal tolerancePct = DefaultTolerancePct)
    {
        if (tolerancePct <= 0) return 0;
        var score = 100m * (1m - Math.Abs(deviationPct) / tolerancePct);
        return Math.Clamp(Math.Round(score, 2), 0m, 100m);
    }

    /// <summary>由平均绝对偏差%打综合分（0–100）。</summary>
    public static decimal CombinedAccuracyFromMeanAbs(
        decimal meanAbsDeviationPct,
        decimal tolerancePct = DefaultTolerancePct)
    {
        if (tolerancePct <= 0) return 0;
        var score = 100m * (1m - meanAbsDeviationPct / tolerancePct);
        return Math.Clamp(Math.Round(score, 2), 0m, 100m);
    }

    /// <summary>综合得分等级：优秀 / 良好 / 一般 / 较差。</summary>
    public static string AccuracyGrade(decimal? combinedAccuracyPct) => combinedAccuracyPct switch
    {
        null => "-",
        >= 80m => "优秀",
        >= 60m => "良好",
        >= 40m => "一般",
        _ => "较差"
    };

    /// <summary>
    /// 用已存偏差重算综合分（兼容旧容差落库数据的展示层刷新）。
    /// </summary>
    public static decimal? RescoreCombinedAccuracy(
        decimal? highDeviationPct,
        decimal? lowDeviationPct,
        decimal tolerancePct = DefaultTolerancePct)
    {
        if (highDeviationPct is null || lowDeviationPct is null) return null;
        var meanAbs = MeanAbsDeviationPct(highDeviationPct.Value, lowDeviationPct.Value);
        return CombinedAccuracyFromMeanAbs(meanAbs, tolerancePct);
    }

    public static (
        decimal HighDeviationPct,
        decimal LowDeviationPct,
        decimal HighAccuracyPct,
        decimal LowAccuracyPct,
        decimal CombinedAccuracyPct,
        bool RangeHit,
        decimal MeanAbsDeviationPct) Evaluate(
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
        var meanAbs = MeanAbsDeviationPct(highDev, lowDev);
        var combined = CombinedAccuracyFromMeanAbs(meanAbs, tolerancePct);
        var lo = Math.Min(predictedLow, predictedHigh);
        var hi = Math.Max(predictedLow, predictedHigh);
        var rangeHit = actualLow >= lo && actualHigh <= hi;
        return (highDev, lowDev, highAcc, lowAcc, combined, rangeHit, meanAbs);
    }

    /// <summary>占位与 ATR 对照不计入准确率榜（ATR 仅作基线对照）。</summary>
    public static bool ShouldCountTowardAccuracy(ForecastSource source)
        => source is not ForecastSource.Placeholder and not ForecastSource.AtrBaseline;

    /// <summary>高/低偏差绝对值过大 → 异常样本。</summary>
    public static bool IsOutlierEvaluation(decimal highDeviationPct, decimal lowDeviationPct)
        => Math.Abs(highDeviationPct) >= OutlierAbsDeviationPct
           || Math.Abs(lowDeviationPct) >= OutlierAbsDeviationPct;

    /// <summary>由近史 AI 综合分决定与 ATR 融合时的 AI 权重。</summary>
    public static decimal ResolveAiBlendWeight(decimal? avgCombinedAccuracyPct)
    {
        if (avgCombinedAccuracyPct is null) return 0.7m;
        if (avgCombinedAccuracyPct.Value < LowAccuracyDeweightThreshold) return 0.35m;
        if (avgCombinedAccuracyPct.Value < 60m) return 0.5m;
        return 0.75m;
    }

    public static ForecastSource ParseSource(string? source, bool? countsTowardAccuracy = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            if (countsTowardAccuracy == false) return ForecastSource.Placeholder;
            return ForecastSource.Ai;
        }

        return source.Trim().ToLowerInvariant() switch
        {
            "atr" or "atr_baseline" or "baseline" => ForecastSource.AtrBaseline,
            "placeholder" or "fallback" or "stub" => ForecastSource.Placeholder,
            "calibrated" or "calibrated_ai" => ForecastSource.CalibratedAi,
            _ => ForecastSource.Ai
        };
    }

    public static string ToSourceKey(ForecastSource source) => source switch
    {
        ForecastSource.AtrBaseline => "atr_baseline",
        ForecastSource.Placeholder => "placeholder",
        ForecastSource.CalibratedAi => "calibrated_ai",
        _ => "ai"
    };

    /// <summary>是否像占位预测（禁止污染准确率）。ATR 基线不算占位。</summary>
    public static bool IsPlaceholderForecast(ForecastPolicy? forecast)
    {
        if (forecast is null) return false;
        var src = ParseSource(forecast.Source, forecast.CountsTowardAccuracy);
        if (src == ForecastSource.AtrBaseline) return false;
        if (src == ForecastSource.Placeholder) return true;
        if (forecast.CountsTowardAccuracy == false) return true;
        var rationale = forecast.Rationale ?? string.Empty;
        return rationale.Contains("占位", StringComparison.Ordinal)
               || rationale.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 近 N 日 ATR 高低带：参考价 ± ATR×倍数。
    /// 参考价优先昨收/最近收盘。
    /// </summary>
    public static ForecastPolicy? TryBuildAtrBaseline(
        IEnumerable<DailyBar>? dailyBars,
        DateTime forecastTargetDate,
        int period = DefaultAtrPeriod,
        decimal multiplier = DefaultAtrMultiplier)
    {
        if (dailyBars is null) return null;
        var bars = dailyBars
            .Where(b => b.Date.Date < forecastTargetDate.Date || b.Close > 0)
            .OrderBy(b => b.Date)
            .ToList();
        if (bars.Count < 2) return null;

        // 用目标日前已收盘的日K（不含未来）
        var hist = bars.Where(b => b.Date.Date < forecastTargetDate.Date).ToList();
        if (hist.Count < 2) hist = bars;

        var atr = ComputeAverageTrueRange(hist, period);
        if (atr is null or <= 0) return null;

        var refPrice = hist[^1].Close;
        if (refPrice <= 0) return null;

        var band = Math.Round(atr.Value * multiplier, 4);
        var high = Math.Round(refPrice + band, 2);
        var low = Math.Round(Math.Max(0.01m, refPrice - band), 2);

        return new ForecastPolicy
        {
            ForDate = forecastTargetDate.Date.ToString("yyyy-MM-dd"),
            PredictedHigh = high,
            PredictedLow = low,
            AsOf = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Rationale = $"ATR{period}×{multiplier:0.##} 基线：参考收盘 {refPrice:F2}，ATR={atr:F4}",
            Source = ToSourceKey(ForecastSource.AtrBaseline),
            CountsTowardAccuracy = false
        };
    }

    /// <summary>
    /// 硬钳制：相对参考价涨跌停近似上沿/下沿，并将区间宽度压到 ATR×[0.6,1.8]。
    /// </summary>
    public static ForecastPolicy ClampForecastToTradingBand(
        ForecastPolicy forecast,
        decimal referencePrice,
        decimal? atr,
        decimal limitCapPct = DefaultLimitCapPct)
    {
        if (forecast.PredictedHigh <= 0 || forecast.PredictedLow <= 0 || referencePrice <= 0)
            return forecast;

        var high = Math.Max(forecast.PredictedHigh, forecast.PredictedLow);
        var low = Math.Min(forecast.PredictedHigh, forecast.PredictedLow);
        var capPct = limitCapPct > 0 ? limitCapPct : DefaultLimitCapPct;
        var maxHigh = Math.Round(referencePrice * (1m + capPct / 100m), 2);
        var minLow = Math.Round(Math.Max(0.01m, referencePrice * (1m - capPct / 100m)), 2);

        high = Math.Min(high, maxHigh);
        low = Math.Max(low, minLow);
        if (high < low) (high, low) = (low, high);

        if (atr is > 0)
        {
            var width = high - low;
            var minW = Math.Round(atr.Value * MinAtrBandMultiple, 2);
            var maxW = Math.Round(atr.Value * MaxAtrBandMultiple, 2);
            if (width < minW)
            {
                var mid = (high + low) / 2m;
                high = Math.Round(Math.Min(maxHigh, mid + minW / 2m), 2);
                low = Math.Round(Math.Max(minLow, mid - minW / 2m), 2);
            }
            else if (width > maxW)
            {
                var mid = (high + low) / 2m;
                high = Math.Round(Math.Min(maxHigh, mid + maxW / 2m), 2);
                low = Math.Round(Math.Max(minLow, mid - maxW / 2m), 2);
            }
        }

        if (high <= low)
        {
            high = maxHigh;
            low = minLow;
        }

        forecast.PredictedHigh = high;
        forecast.PredictedLow = low;
        forecast.Rationale = (forecast.Rationale ?? string.Empty)
            + $" | 硬钳制 ref={referencePrice:F2} cap±{capPct:0.#}%";
        forecast.CapHighByLimitPct = capPct;
        return forecast;
    }

    /// <summary>AI 与 ATR 基线按权重融合（落盘为 calibrated_ai 或 ai）。</summary>
    public static ForecastPolicy BlendAiWithAtr(
        ForecastPolicy ai,
        ForecastPolicy atrBaseline,
        decimal aiWeight)
    {
        var w = Math.Clamp(aiWeight, 0.15m, 0.9m);
        var high = Math.Round(w * ai.PredictedHigh + (1m - w) * atrBaseline.PredictedHigh, 2);
        var low = Math.Round(w * ai.PredictedLow + (1m - w) * atrBaseline.PredictedLow, 2);
        if (high < low) (high, low) = (low, high);

        return new ForecastPolicy
        {
            ForDate = ai.ForDate ?? atrBaseline.ForDate,
            PredictedHigh = high,
            PredictedLow = low,
            AsOf = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Rationale = (ai.Rationale ?? string.Empty)
                        + $" | 融合ATR wAi={w:F2}",
            Source = string.Equals(ai.Source, ToSourceKey(ForecastSource.CalibratedAi), StringComparison.OrdinalIgnoreCase)
                ? ToSourceKey(ForecastSource.CalibratedAi)
                : ToSourceKey(ForecastSource.Ai),
            CountsTowardAccuracy = true,
            SoftBandMode = string.IsNullOrWhiteSpace(ai.SoftBandMode) ? "softPrefer" : ai.SoftBandMode,
            CapHighByLimitPct = ai.CapHighByLimitPct > 0 ? ai.CapHighByLimitPct : DefaultLimitCapPct
        };
    }

    public static decimal Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return 0m;
        var sorted = values.OrderBy(x => x).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : Math.Round((sorted[mid - 1] + sorted[mid]) / 2m, 4);
    }

    public static decimal? ComputeAverageTrueRange(IReadOnlyList<DailyBar> barsAscending, int period)
    {
        if (barsAscending.Count < 2 || period < 1) return null;
        var trs = new List<decimal>();
        for (var i = 1; i < barsAscending.Count; i++)
        {
            var cur = barsAscending[i];
            var prevClose = barsAscending[i - 1].Close;
            var tr = Math.Max(
                cur.High - cur.Low,
                Math.Max(Math.Abs(cur.High - prevClose), Math.Abs(cur.Low - prevClose)));
            if (tr > 0) trs.Add(tr);
        }

        if (trs.Count == 0) return null;
        var take = Math.Min(period, trs.Count);
        return trs.Skip(trs.Count - take).Average();
    }

    /// <summary>
    /// 用历史高/低偏差校准：predicted' = predicted / (1 + meanDev%/100)。
    /// 偏差会先钳制在 ±MaxCalibrationAdjustPct，避免越校越歪。
    /// </summary>
    public static ForecastPolicy ApplyBiasCalibration(
        ForecastPolicy forecast,
        decimal meanHighDeviationPct,
        decimal meanLowDeviationPct)
    {
        static decimal Correct(decimal predicted, decimal meanDevPct)
        {
            if (predicted <= 0) return predicted;
            var clamped = Math.Clamp(meanDevPct, -MaxCalibrationAdjustPct, MaxCalibrationAdjustPct);
            var factor = 1m + clamped / 100m;
            if (Math.Abs(factor) < 0.01m) return predicted;
            return Math.Round(predicted / factor, 2);
        }

        var highDev = Math.Clamp(meanHighDeviationPct, -MaxCalibrationAdjustPct, MaxCalibrationAdjustPct);
        var lowDev = Math.Clamp(meanLowDeviationPct, -MaxCalibrationAdjustPct, MaxCalibrationAdjustPct);
        var high = Correct(forecast.PredictedHigh, highDev);
        var low = Correct(forecast.PredictedLow, lowDev);
        if (high < low) (high, low) = (low, high);

        return new ForecastPolicy
        {
            ForDate = forecast.ForDate,
            PredictedHigh = high,
            PredictedLow = low,
            AsOf = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Rationale = (forecast.Rationale ?? string.Empty)
                        + $" | 偏差校准 highDev̄={highDev:F2}% lowDev̄={lowDev:F2}% (≤±{MaxCalibrationAdjustPct:0.#}%)",
            Source = ToSourceKey(ForecastSource.CalibratedAi),
            CountsTowardAccuracy = true,
            SoftBandMode = string.IsNullOrWhiteSpace(forecast.SoftBandMode) ? "softPrefer" : forecast.SoftBandMode,
            CapHighByLimitPct = forecast.CapHighByLimitPct > 0 ? forecast.CapHighByLimitPct : DefaultLimitCapPct
        };
    }

    /// <summary>样本充足且偏差非零才建议校准。</summary>
    public static bool ShouldApplyCalibration(int sampleCount, decimal meanHighDev, decimal meanLowDev)
        => sampleCount >= MinCalibrationSamples
           && (Math.Abs(meanHighDev) >= 0.05m || Math.Abs(meanLowDev) >= 0.05m);

    /// <summary>softPrefer 未入带时的默认缩仓系数。</summary>
    public const decimal SoftPreferShrinkScale = 0.6m;

    /// <summary>现价相对预测低跌破超过此比例（%）→ 视为预测低失效。</summary>
    public const decimal ForecastLowBreakPct = 3.0m;

    /// <summary>预测低失效日抬高的 softPrefer 缩仓系数（更接近放行）。</summary>
    public const decimal SoftPreferShrinkScaleWhenForecastBroken = 0.85m;

    /// <summary>现价是否已显著跌破预测低（预测失效日）。</summary>
    public static bool IsForecastLowBroken(ForecastPolicy? forecast, decimal lastPrice)
    {
        if (forecast is null || lastPrice <= 0) return false;
        if (forecast.PredictedHigh <= 0 || forecast.PredictedLow <= 0) return false;
        if (IsPlaceholderForecast(forecast)) return false;
        var low = Math.Min(forecast.PredictedHigh, forecast.PredictedLow);
        if (low <= 0) return false;
        var breakPct = (low - lastPrice) / low * 100m;
        return breakPct >= ForecastLowBreakPct;
    }

    /// <summary>
    /// 预测软边界（兼容旧调用）：默认按 forecast.softBandMode。
    /// hard 模式未入带返回 false；softPrefer 未入带仍返回 true（由调用方缩仓）。
    /// </summary>
    public static bool PassesForecastSoftBand(
        ForecastPolicy? forecast,
        decimal lastPrice,
        DateTime now,
        bool forSell,
        decimal bandRatio,
        out string? blockReason)
    {
        var verdict = EvaluateForecastSoftBand(
            forecast, lastPrice, now, forSell, bandRatio, out blockReason);
        return verdict != SoftBandVerdict.Block;
    }

    /// <summary>
    /// 预测软边界：高抛须接近预测上沿，低吸须接近预测下沿。
    /// 支持 softPrefer（默认）与 hard；预测高可按参考价钳制。
    /// </summary>
    public static SoftBandVerdict EvaluateForecastSoftBand(
        ForecastPolicy? forecast,
        decimal lastPrice,
        DateTime now,
        bool forSell,
        decimal bandRatio,
        out string? message,
        decimal? referencePrice = null,
        decimal? avgCombinedAccuracyPct = null)
    {
        message = null;
        if (forecast is null || lastPrice <= 0) return SoftBandVerdict.Pass;
        if (forecast.PredictedHigh <= 0 || forecast.PredictedLow <= 0) return SoftBandVerdict.Pass;
        if (IsPlaceholderForecast(forecast)) return SoftBandVerdict.Pass;

        if (!string.IsNullOrWhiteSpace(forecast.ForDate)
            && DateTime.TryParse(forecast.ForDate, out var forDate)
            && forDate.Date != now.Date)
        {
            return SoftBandVerdict.Pass;
        }

        var mode = string.IsNullOrWhiteSpace(forecast.SoftBandMode)
            ? "softPrefer"
            : forecast.SoftBandMode.Trim();
        var hard = string.Equals(mode, "hard", StringComparison.OrdinalIgnoreCase);

        var high = Math.Max(forecast.PredictedHigh, forecast.PredictedLow);
        var low = Math.Min(forecast.PredictedHigh, forecast.PredictedLow);

        var capPct = forecast.CapHighByLimitPct > 0 ? forecast.CapHighByLimitPct : 12m;
        if (referencePrice is > 0)
        {
            var capped = referencePrice.Value * (1m + capPct / 100m);
            if (capped > low)
                high = Math.Min(high, capped);
        }

        var range = high - low;
        if (range <= 0) return SoftBandVerdict.Pass;

        var ratio = Math.Clamp(bandRatio, 0.05m, 0.5m);
        if (avgCombinedAccuracyPct is < LowAccuracyDeweightThreshold)
            ratio = Math.Min(0.5m, ratio + 0.15m);

        // 已显著跌破预测低：低吸软边界视为失效放行（避免锚在错误预测低上）
        if (!forSell && IsForecastLowBroken(forecast, lastPrice))
        {
            message = $"预测低已失效(现价{lastPrice:F2}≪预测低{low:F2})，低吸软边界放行";
            return SoftBandVerdict.Pass;
        }

        if (forSell)
        {
            var floor = high - range * ratio;
            if (lastPrice < floor)
            {
                message = $"预测软边界：现价 {lastPrice:F2} 未进入高抛区(≥{floor:F2}，预测高 {high:F2})";
                return hard ? SoftBandVerdict.Block : SoftBandVerdict.PreferShrink;
            }
        }
        else
        {
            var ceil = low + range * ratio;
            if (lastPrice > ceil)
            {
                message = $"预测软边界：现价 {lastPrice:F2} 未进入低吸区(≤{ceil:F2}，预测低 {low:F2})";
                return hard ? SoftBandVerdict.Block : SoftBandVerdict.PreferShrink;
            }
        }

        return SoftBandVerdict.Pass;
    }

    /// <summary>近史准确率过低时，返回更保守的 aggression 缩放系数（&lt;1）。</summary>
    public static decimal ResolveAggressionScale(decimal? avgCombinedAccuracyPct)
    {
        if (avgCombinedAccuracyPct is null) return 1m;
        if (avgCombinedAccuracyPct.Value >= LowAccuracyDeweightThreshold) return 1m;
        // 0 分 → 0.7；40 分 → 1.0
        var t = Math.Clamp(avgCombinedAccuracyPct.Value / LowAccuracyDeweightThreshold, 0m, 1m);
        return Math.Round(0.70m + 0.30m * t, 3);
    }
}
