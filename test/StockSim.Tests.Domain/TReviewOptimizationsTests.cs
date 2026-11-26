using StockSim.Domain.Entities;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Tests.Domain;

/// <summary>做T复盘 P0–P2：残腿优先、日亏放宽买回、强势日低吸、经济回合、路径反弹解锁。</summary>
public class TReviewOptimizationsTests
{
    private static QuoteTick Q(string symbol, decimal last, decimal changePct = 0m) => new()
    {
        Symbol = symbol,
        LastPrice = last,
        Bid = last - 0.01m,
        Ask = last + 0.01m,
        Volume = 1_000_000,
        ChangePercent = changePct,
        Timestamp = DateTime.Now
    };

    [Fact]
    public void Idle_With_OpenT_Does_Not_Open_New_Sell()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        policy.Risk.MinRoundTripEdgeBps = 0;
        policy.Triggers.SellDeviationPct = new ParamRange { Base = 0.3m, Min = 0.2m, Max = 0.8m };
        var runtime = new SymbolRuntimeState
        {
            Symbol = "600893",
            TradeDate = DateTime.Today,
            State = TState.Idle,
            OpenTQty = 200,
            LastSellPrice = 36m,
            PathRegime = IntradayPathRegime.HighOpenLowGo,
            PathPhase = PathPhase.Locked,
            Overrides = new RuntimeOverrides { PathLabel = "HighOpenLowGo" }
        };
        var pos = new Position
        {
            Symbol = "600893",
            BaseQuantity = 1000,
            Quantity = 800,
            SellableQuantity = 800,
            AvgCost = 34m
        };
        var quote = Q("600893", 36.5m);
        var eff = policy.ToEffective(runtime.Overrides);
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 35m,
            DeviationPct = (36.5m - 35m) / 35m * 100m,
            VolumeRatio = 1.2m,
            Now = DateTime.Today.AddHours(10).AddMinutes(30),
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 80_000m,
            ReferencePrice = 35m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.NotEqual(SignalAction.Sell, sig.Action);
        Assert.True(runtime.State is TState.SoldHigh or TState.Idle);
    }

    [Fact]
    public void DayLoss_Relaxes_OpenT_BuyBack_Above_LastSell()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        policy.Risk.DayLossStopPct = -0.01m; // 极易触发
        policy.Risk.MinRoundTripEdgeBps = 0;
        var runtime = new SymbolRuntimeState
        {
            Symbol = "002371",
            TradeDate = DateTime.Today,
            State = TState.SoldHigh,
            OpenTQty = 100,
            LastSellPrice = 400m,
            DayRealizedPnL = -5000m, // 相对底仓制造日亏
            PathRegime = IntradayPathRegime.LowOpenHighGo,
            PathPhase = PathPhase.Locked,
            Overrides = new RuntimeOverrides { PathLabel = "LowOpenHighGo" }
        };
        var pos = new Position
        {
            Symbol = "002371",
            BaseQuantity = 500,
            Quantity = 400,
            SellableQuantity = 400,
            AvgCost = 380m
        };
        // 买回价略高于卖出价：正常不追，日亏应放宽至 +0.3%
        var askPx = 400.8m;
        var quote = Q("002371", askPx);
        quote = new QuoteTick
        {
            Symbol = quote.Symbol,
            LastPrice = askPx,
            Bid = askPx - 0.05m,
            Ask = askPx,
            Volume = quote.Volume,
            ChangePercent = -2m,
            Timestamp = quote.Timestamp
        };
        var eff = policy.ToEffective(runtime.Overrides);
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 400m,
            DeviationPct = -1.5m,
            VolumeRatio = 1.2m,
            Now = DateTime.Today.AddHours(13),
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 200_000m,
            ReferencePrice = 400m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.Buy, sig.Action);
    }

    [Fact]
    public void Strong_HighGo_Skips_NearPredictedHigh_Abandon()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        policy.Risk.MinRoundTripEdgeBps = 0;
        policy.Forecast = new ForecastPolicy
        {
            PredictedHigh = 12.0m,
            PredictedLow = 10.5m,
            SoftBandMode = "softPrefer"
        };
        var runtime = new SymbolRuntimeState
        {
            Symbol = "002611",
            TradeDate = DateTime.Today,
            State = TState.SoldHigh,
            OpenTQty = 200,
            LastSellPrice = 12.05m,
            PathRegime = IntradayPathRegime.HighOpenHighGo,
            PathPhase = PathPhase.Locked,
            Overrides = new RuntimeOverrides
            {
                PathLabel = "HighOpenHighGo",
                SkipNearPredictedHighAbandon = true,
                BuyDeviationPct = -0.2m
            }
        };
        var pos = new Position
        {
            Symbol = "002611",
            BaseQuantity = 1000,
            Quantity = 800,
            SellableQuantity = 800,
            AvgCost = 11m
        };
        var last = 11.95m; // 近预测高 12.0
        var quote = Q("002611", last, changePct: 8m);
        quote = new QuoteTick
        {
            Symbol = quote.Symbol,
            LastPrice = last,
            Bid = last - 0.01m,
            Ask = last,
            Volume = quote.Volume,
            ChangePercent = 8m,
            Timestamp = quote.Timestamp
        };
        var eff = policy.ToEffective(runtime.Overrides);
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 11.5m,
            DeviationPct = (last - 11.5m) / 11.5m * 100m,
            VolumeRatio = 1.5m,
            Now = DateTime.Today.AddHours(10).AddMinutes(20),
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 50_000m,
            ReferencePrice = 11.5m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.DoesNotContain("逼近预测高", sig.Reason);
    }

    [Fact]
    public void LowOpenLowGo_Rebound_Unlocks_BlockNewT()
    {
        var pb = PathPlaybookResolver.Resolve(IntradayPathRegime.LowOpenLowGo);
        Assert.True(pb.BlockNewT);
        var unlocked = PathPlaybookResolver.ApplyIndexFilter(
            pb, IntradayPathRegime.LowOpenLowGo, indexChangePct: 0.8m);
        Assert.False(unlocked.BlockNewT);
        Assert.Contains("ReboundUnlock", unlocked.Label);
    }

    [Fact]
    public void MinRoundTripEdge_Requires_Capture_At_Least_TwoX_Fees()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        // 极窄带：捕获不足 2×费
        policy.Risk.MinRoundTripEdgeBps = 0;
        policy.Triggers.BuyDeviationPct = new ParamRange { Base = -0.15m, Min = -0.3m, Max = -0.1m };
        policy.Triggers.SellDeviationPct = new ParamRange { Base = 0.15m, Min = 0.1m, Max = 0.3m };
        var runtime = new SymbolRuntimeState
        {
            Symbol = "600893",
            TradeDate = DateTime.Today,
            State = TState.Idle,
            PathRegime = IntradayPathRegime.HighOpenLowGo,
            PathPhase = PathPhase.Locked,
            Overrides = new RuntimeOverrides { PathLabel = "HighOpenLowGo" }
        };
        var pos = new Position
        {
            Symbol = "600893",
            BaseQuantity = 1000,
            Quantity = 1000,
            SellableQuantity = 1000,
            AvgCost = 35m
        };
        var quote = Q("600893", 35.2m);
        var eff = policy.ToEffective(runtime.Overrides);
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 35m,
            DeviationPct = (35.2m - 35m) / 35m * 100m,
            VolumeRatio = 1.2m,
            Now = DateTime.Today.AddHours(10).AddMinutes(10),
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 50_000m,
            ReferencePrice = 35m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.None, sig.Action);
        Assert.Contains("费后净价差不足", sig.Reason);
    }
}
