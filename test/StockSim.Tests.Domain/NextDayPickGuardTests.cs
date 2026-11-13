using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Tests.Domain;

public class NextDayPickGuardTests
{
    [Fact]
    public void ClampExpectedReturn_Caps_At_3()
    {
        Assert.Equal(3m, NextDayPickGuard.ClampExpectedReturnPct(7m));
        Assert.Equal(0.5m, NextDayPickGuard.ClampExpectedReturnPct(0.1m));
        Assert.Equal(2m, NextDayPickGuard.ClampExpectedReturnPct(2m));
    }

    [Fact]
    public void Calibrate_Raises_Chase_When_Frequent()
    {
        var tags = new Dictionary<string, int>
        {
            [NextDayPickMetrics.TagChaseHigh] = 4,
            [NextDayPickMetrics.TagWeakRelative] = 3
        };
        var t = NextDayPickGuard.CalibrateThresholds(tags, sampleSize: 10);
        Assert.True(t.ChaseHighNearHighPct >= 4m);
        Assert.True(t.WeakRelative5dThresholdPct >= -1m);
    }

    [Fact]
    public void Evaluate_Rejects_ChaseHigh()
    {
        var bars = new List<DailyBar>();
        var d0 = new DateTime(2026, 11, 1);
        for (var i = 0; i < 20; i++)
        {
            bars.Add(new DailyBar
            {
                Date = d0.AddDays(i),
                Open = 10, High = 10.5m, Low = 9.8m, Close = 10.2m, Amount = 200_000_000m
            });
        }

        // 最后两日大涨并贴近前高
        bars.Add(new DailyBar
        {
            Date = d0.AddDays(20),
            Open = 10.2m, High = 11.2m, Low = 10.1m, Close = 11.1m, Amount = 300_000_000m
        });
        bars.Add(new DailyBar
        {
            Date = d0.AddDays(21),
            Open = 11.1m, High = 11.3m, Low = 10.9m, Close = 11.2m, Amount = 280_000_000m
        });

        var detail = new StockDetailBundle
        {
            Board = new StockQuoteBoard
            {
                Symbol = "000001", Name = "测试", Last = 11.2m, Open = 11.1m, High = 11.3m,
                Low = 10.9m, PreClose = 11.1m, Change = 0.1m, ChangePercent = 0.9m,
                Volume = 1, Amount = 280_000_000m, TurnoverRate = 1, AsOf = DateTime.Now
            },
            Fundamentals = new StockFundamentals(),
            FundFlow = new FundFlowSnapshot(),
            DailyBars = bars,
            MinuteBars = Array.Empty<MinuteBar>(),
            IsSimulated = false
        };

        var v = NextDayPickGuard.EvaluateLiquidityAndPath(
            detail, null, NextDayPickThresholds.Default, bars[^1].Date);
        Assert.False(v.Accepted);
        Assert.Contains("追高", v.Reason!);
    }

    [Fact]
    public void TryParseConfidence_And_Invalidation()
    {
        var json = """{"confidence":0.7,"invalidation":"低开超2%失效","catalystType":"theme"}""";
        Assert.True(NextDayPickGuard.TryParseConfidence(json, out var c));
        Assert.Equal(0.7m, c);
        Assert.True(NextDayPickGuard.HasInvalidation(json));
        Assert.Equal("theme", NextDayPickGuard.TryGetCatalystType(json));
    }

    [Fact]
    public void TryParseConfidence_Accepts_Qualitative_Strings()
    {
        Assert.True(NextDayPickGuard.TryParseConfidence("""{"confidence":"medium"}""", out var m));
        Assert.Equal(0.6m, m);
        Assert.True(NextDayPickGuard.TryParseConfidence("""{"confidence":"medium-high"}""", out var mh));
        Assert.Equal(0.7m, mh);
        Assert.True(NextDayPickGuard.TryParseConfidence("""{"confidence":"80"}""", out var p));
        Assert.Equal(0.8m, p);
    }
}
