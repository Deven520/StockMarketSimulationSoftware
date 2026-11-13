using StockSim.Domain.Strategies;

namespace StockSim.Tests.Domain;

public class NextDayPickMetricsTests
{
    [Fact]
    public void Evaluate_Direction_And_Target_Hit()
    {
        var (ret, dir, tgt) = NextDayPickMetrics.Evaluate(
            baselinePrice: 10m, actualClose: 10.3m, expectedReturnPct: 2m);
        Assert.Equal(3m, ret);
        Assert.True(dir);
        Assert.True(tgt);
    }

    [Fact]
    public void Evaluate_Up_But_Miss_Target()
    {
        var (ret, dir, tgt) = NextDayPickMetrics.Evaluate(10m, 10.1m, expectedReturnPct: 3m);
        Assert.Equal(1m, ret);
        Assert.True(dir);
        Assert.False(tgt);
    }

    [Fact]
    public void Evaluate_Down_Misses_Both()
    {
        var (ret, dir, tgt) = NextDayPickMetrics.Evaluate(10m, 9.5m, expectedReturnPct: 1m);
        Assert.Equal(-5m, ret);
        Assert.False(dir);
        Assert.False(tgt);
    }

    [Fact]
    public void RelativeStrength_And_FailureTags()
    {
        Assert.Equal(-2.5m, NextDayPickMetrics.RelativeStrengthPct(-1m, 1.5m));
        Assert.Empty(NextDayPickMetrics.InferFailureTags(true, 2m, 1m));

        var tags = NextDayPickMetrics.InferFailureTags(
            directionHit: false,
            returnPct: -3m,
            relativeStrengthPct: -2m,
            openGapPct: -3m,
            nearHighChase: true);
        Assert.Contains(NextDayPickMetrics.TagWeakRelative, tags);
        Assert.Contains(NextDayPickMetrics.TagGapDownOpen, tags);
        Assert.Contains(NextDayPickMetrics.TagChaseHigh, tags);
    }

    [Fact]
    public void InferFailureTags_MarketDrag_When_StockDown_But_Beats_Index()
    {
        var tags = NextDayPickMetrics.InferFailureTags(false, -1m, relativeStrengthPct: 0.5m);
        Assert.Contains(NextDayPickMetrics.TagMarketDrag, tags);
    }
}
