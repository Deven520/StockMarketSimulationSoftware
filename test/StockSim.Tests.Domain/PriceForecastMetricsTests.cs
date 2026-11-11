using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Tests.Domain;

public class PriceForecastMetricsTests
{
    [Fact]
    public void CanFinalizeForecast_Rejects_Today_Before_Close()
    {
        var today = new DateTime(2026, 11, 9, 0, 40, 0);
        Assert.False(PriceForecastMetrics.CanFinalizeForecast(today.Date, today));
        Assert.False(PriceForecastMetrics.CanFinalizeForecast(
            today.Date, new DateTime(2026, 11, 9, 14, 59, 0)));
        Assert.True(PriceForecastMetrics.CanFinalizeForecast(
            today.Date, new DateTime(2026, 11, 9, 15, 0, 0)));
    }

    [Fact]
    public void CanFinalizeForecast_Allows_Past_Trading_Day()
    {
        var now = new DateTime(2026, 11, 9, 0, 40, 0);
        Assert.True(PriceForecastMetrics.CanFinalizeForecast(new DateTime(2026, 11, 6), now));
        Assert.False(PriceForecastMetrics.CanFinalizeForecast(new DateTime(2026, 11, 10), now));
    }

    [Fact]
    public void ResolveActualHighLow_Uses_Matching_DailyBar_Only()
    {
        var bars = new[]
        {
            new DailyBar
            {
                Date = new DateTime(2026, 11, 6),
                Open = 529m,
                High = 574.55m,
                Low = 528.88m,
                Close = 560m,
                Volume = 1,
                Amount = 1
            }
        };

        var hit = PriceForecastMetrics.ResolveActualHighLowFromDailyBars(bars, new DateTime(2026, 11, 6));
        Assert.NotNull(hit);
        Assert.Equal(574.55m, hit!.Value.High);
        Assert.Equal(528.88m, hit.Value.Low);

        Assert.Null(PriceForecastMetrics.ResolveActualHighLowFromDailyBars(bars, new DateTime(2026, 11, 9)));
        Assert.Null(PriceForecastMetrics.ResolveActualHighLowFromDailyBars(null, new DateTime(2026, 11, 6)));
    }

    [Fact]
    public void Evaluate_Deviation_Matches_Formula()
    {
        // 预测 160 vs 实际 574.55 → 约 -72%
        var (highDev, _, _, _, combined, _, meanAbs) = PriceForecastMetrics.Evaluate(
            160.85m, 155.15m, 574.55m, 528.88m);
        Assert.True(highDev < -70m);
        Assert.True(meanAbs > 5m);
        Assert.Equal(0m, combined);
        Assert.Equal("较差", PriceForecastMetrics.AccuracyGrade(combined));
    }

    [Fact]
    public void Evaluate_Uses_MeanAbs_With_5Pct_Tolerance()
    {
        // 通富例：预测 46.28/40.14 vs 实际 46.98/44.34 → 相对误差约 5.48% → 得分 0
        var (highDev, lowDev, _, _, combined, rangeHit, meanAbs) = PriceForecastMetrics.Evaluate(
            46.28m, 40.14m, 46.98m, 44.34m);
        Assert.Equal(-1.4900m, highDev);
        Assert.Equal(-9.4723m, lowDev);
        Assert.True(meanAbs > 5m && meanAbs < 6m);
        Assert.False(rangeHit);
        Assert.Equal(0m, combined);
        Assert.Equal(0.70m, PriceForecastMetrics.AbsoluteError(46.28m, 46.98m));
        Assert.Equal(4.20m, PriceForecastMetrics.AbsoluteError(40.14m, 44.34m));
    }

    [Fact]
    public void Evaluate_Near_Perfect_Scores_Excellent()
    {
        var (_, _, _, _, combined, rangeHit, meanAbs) = PriceForecastMetrics.Evaluate(
            100.5m, 99.5m, 100m, 100m);
        Assert.True(meanAbs < 1m);
        Assert.True(combined >= 80m);
        Assert.True(rangeHit);
        Assert.Equal("优秀", PriceForecastMetrics.AccuracyGrade(combined));
    }

    [Fact]
    public void ResolveForecastTargetDate_Same_Trading_Day_Until_Close()
    {
        var morning = new DateTime(2026, 11, 9, 9, 34, 0);
        Assert.Equal(new DateTime(2026, 11, 9), PriceForecastMetrics.ResolveForecastTargetDate(morning));
        Assert.True(PriceForecastMetrics.IsSameDayForecastWindow(morning));

        var beforeClose = new DateTime(2026, 11, 9, 14, 59, 0);
        Assert.Equal(new DateTime(2026, 11, 9), PriceForecastMetrics.ResolveForecastTargetDate(beforeClose));

        var afterClose = new DateTime(2026, 11, 9, 15, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 10), PriceForecastMetrics.ResolveForecastTargetDate(afterClose));
        Assert.False(PriceForecastMetrics.IsSameDayForecastWindow(afterClose));
    }

    [Fact]
    public void IsPlaceholderForecast_Detects_Source_And_Rationale()
    {
        Assert.True(PriceForecastMetrics.IsPlaceholderForecast(new ForecastPolicy
        {
            PredictedHigh = 10,
            PredictedLow = 9,
            Source = "placeholder",
            CountsTowardAccuracy = false
        }));
        Assert.True(PriceForecastMetrics.IsPlaceholderForecast(new ForecastPolicy
        {
            PredictedHigh = 10,
            PredictedLow = 9,
            Rationale = "占位：按参考价±1.5%"
        }));
        Assert.False(PriceForecastMetrics.IsPlaceholderForecast(new ForecastPolicy
        {
            PredictedHigh = 10,
            PredictedLow = 9,
            Source = "ai",
            CountsTowardAccuracy = true
        }));
    }

    [Fact]
    public void TryBuildAtrBaseline_Uses_Recent_Bars()
    {
        var bars = new List<DailyBar>();
        var d = new DateTime(2026, 10, 1);
        var close = 100m;
        for (var i = 0; i < 20; i++)
        {
            bars.Add(new DailyBar
            {
                Date = d.AddDays(i),
                Open = close,
                High = close + 2m,
                Low = close - 2m,
                Close = close + 0.5m,
                Volume = 1000,
                Amount = 100000
            });
            close += 0.5m;
        }

        var target = new DateTime(2026, 10, 25);
        var fc = PriceForecastMetrics.TryBuildAtrBaseline(bars, target);
        Assert.NotNull(fc);
        Assert.True(fc!.PredictedHigh > fc.PredictedLow);
        Assert.Equal("atr_baseline", fc.Source);
        Assert.True(fc.CountsTowardAccuracy);
    }

    [Fact]
    public void ApplyBiasCalibration_Corrects_Systematic_Overestimate()
    {
        var raw = new ForecastPolicy
        {
            ForDate = "2026-11-10",
            PredictedHigh = 110m,
            PredictedLow = 100m,
            Source = "ai"
        };
        // 历史系统性偏高 10%
        var calibrated = PriceForecastMetrics.ApplyBiasCalibration(raw, 10m, 10m);
        Assert.Equal("calibrated_ai", calibrated.Source);
        Assert.True(calibrated.PredictedHigh < 110m);
        Assert.True(calibrated.PredictedLow < 100m);
    }

    [Fact]
    public void PassesForecastSoftBand_Blocks_Sell_Too_Far_From_High()
    {
        var fc = new ForecastPolicy
        {
            ForDate = "2026-11-09",
            PredictedHigh = 110m,
            PredictedLow = 100m,
            Source = "ai",
            CountsTowardAccuracy = true
        };
        var now = new DateTime(2026, 11, 9, 10, 0, 0);
        Assert.False(PriceForecastMetrics.PassesForecastSoftBand(
            fc, lastPrice: 101m, now, forSell: true, 0.35m, out var reason));
        Assert.Contains("高抛区", reason);
        Assert.True(PriceForecastMetrics.PassesForecastSoftBand(
            fc, lastPrice: 108m, now, forSell: true, 0.35m, out _));
        Assert.True(PriceForecastMetrics.PassesForecastSoftBand(
            fc, lastPrice: 101m, now, forSell: false, 0.35m, out _));
    }

    [Fact]
    public void ResolveAggressionScale_Deweights_When_Accuracy_Low()
    {
        Assert.Equal(1m, PriceForecastMetrics.ResolveAggressionScale(80m));
        Assert.True(PriceForecastMetrics.ResolveAggressionScale(0m) < 1m);
        Assert.Equal(1m, PriceForecastMetrics.ResolveAggressionScale(null));
    }
}
