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
        Assert.Equal(2m, NextDayPickGuard.ClampExpectedReturnForNextDay(7m));
        Assert.Equal(1.5m, NextDayPickGuard.ClampExpectedReturnForNextDay(1.5m));
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
    public void Calibrate_Raises_On_Low_HitRate_And_MarketDrag()
    {
        var tags = new Dictionary<string, int>
        {
            [NextDayPickMetrics.TagMarketDrag] = 5
        };
        var t = NextDayPickGuard.CalibrateThresholds(
            tags, sampleSize: 10, directionHitRatePct: 40m, missSampleSize: 6);
        Assert.True(t.ConfidenceFloor >= 0.60m);
        Assert.Equal(-1.0m, t.IndexWeakDayThresholdPct);
        Assert.Equal(1.5m, t.WeakMarketMinRs5dPct);
        Assert.True(t.PreferAbsoluteDirection);
    }

    [Fact]
    public void Calibrate_MarketDrag_Half_Of_Misses_Tightens_Batch()
    {
        // 6/12 miss = 50% → MaxTheme=1, Confidence≥0.65, MaxPicks≤3（不依赖总命中率）
        var tags = new Dictionary<string, int>
        {
            [NextDayPickMetrics.TagMarketDrag] = 6
        };
        var t = NextDayPickGuard.CalibrateThresholds(
            tags, sampleSize: 20, directionHitRatePct: 55m, missSampleSize: 12);
        Assert.Equal(1, t.MaxSameThemePerBatch);
        Assert.True(t.ConfidenceFloor >= 0.65m);
        Assert.True(t.MaxPicksPerBatch <= NextDayPickGuard.WeakMarketMaxPicksPerBatch);
        Assert.True(t.PreferAbsoluteDirection);
        Assert.Equal(-1.0m, t.IndexWeakDayThresholdPct);
    }

    [Fact]
    public void IsExecutableAvoidPattern_And_SectorAlias()
    {
        Assert.True(NextDayPickGuard.IsExecutableAvoidPattern("高开低走追缺口"));
        Assert.False(NextDayPickGuard.IsExecutableAvoidPattern("市场情绪一般需要观察"));
        Assert.True(NextDayPickGuard.SectorAliasMatch("化学制药", "医药生物偏弱"));
        Assert.True(NextDayPickGuard.SectorAliasMatch("光伏设备", "新能源"));
        Assert.False(NextDayPickGuard.SectorAliasMatch("银行", "半导体"));
    }

    [Fact]
    public void IsWeakMarketDay_Uses_Threshold()
    {
        var t = NextDayPickThresholds.Default with { IndexWeakDayThresholdPct = -1.0m };
        Assert.True(NextDayPickGuard.IsWeakMarketDay(-1.2m, t));
        Assert.False(NextDayPickGuard.IsWeakMarketDay(-0.5m, t));
        Assert.False(NextDayPickGuard.IsWeakMarketDay(null, t));
    }

    [Fact]
    public void ClassifyTheme_Blocks_Pure_Narrative()
    {
        Assert.Equal(NextDayThemeClass.NarrativeBlocked, NextDayPickGuard.ClassifyTheme("有重大利好"));
        Assert.Equal(NextDayThemeClass.NarrativeBlocked, NextDayPickGuard.ClassifyTheme("重磅利好"));
        Assert.Equal(NextDayThemeClass.SoftPenalty, NextDayPickGuard.ClassifyTheme("有利好"));
        // 空心口号（无板块）即使带放量也软降权
        Assert.Equal(NextDayThemeClass.SoftPenalty, NextDayPickGuard.ClassifyTheme("有利好，持续放量"));
        Assert.Equal(NextDayThemeClass.Ok, NextDayPickGuard.ClassifyTheme("超跌反弹"));
        Assert.Equal(NextDayThemeClass.Ok, NextDayPickGuard.ClassifyTheme("半导体超跌反弹+放量"));
        Assert.True(NextDayPickGuard.RequiresVolumeSurge("有利好，持续放量"));
        Assert.False(NextDayPickGuard.RequiresVolumeSurge("超跌反弹"));
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
