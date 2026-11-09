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
        var (highDev, _, _, _, combined, _) = PriceForecastMetrics.Evaluate(
            160.85m, 155.15m, 574.55m, 528.88m);
        Assert.True(highDev < -70m);
        Assert.Equal(0m, combined);
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
}
