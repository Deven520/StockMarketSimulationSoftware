using StockSim.Domain.Entities;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Tests.Domain;

public class TBoughtLowKeepBaseAndPathGateTests
{
    private static QuoteTick Q(string symbol, decimal last) => new()
    {
        Symbol = symbol,
        LastPrice = last,
        Bid = last - 0.01m,
        Ask = last + 0.01m,
        Volume = 1_000_000,
        ChangePercent = 1m,
        Timestamp = DateTime.Now
    };

    [Fact]
    public void BoughtLow_Sell_Does_Not_Count_Toward_OpenT_And_Keeps_Base()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        policy.Risk.MinRoundTripEdgeBps = 0;
        var runtime = new SymbolRuntimeState
        {
            Symbol = "002371",
            TradeDate = DateTime.Today,
            State = TState.BoughtLow,
            LastBuyPrice = 582m,
            PathRegime = IntradayPathRegime.LowOpenHighGo,
            PathPhase = PathPhase.Locked
        };
        var pos = new Position
        {
            Symbol = "002371",
            BaseQuantity = 200,
            Quantity = 300,
            SellableQuantity = 300,
            AvgCost = 580m
        };
        var now = DateTime.Today.AddHours(13).AddMinutes(5);
        var quote = Q("002371", 592m);
        var eff = policy.ToEffective(new RuntimeOverrides
        {
            PathLabel = "LowOpenHighGo",
            StyleHint = "momentum_t",
            SellDeviationPct = 0.8m
        });
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 580m,
            DeviationPct = (592m - 580m) / 580m * 100m,
            VolumeRatio = 1.2m,
            Now = now,
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 100_000m,
            ReferencePrice = 580m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.Sell, sig.Action);
        Assert.False(sig.CountsTowardOpenT);
        Assert.True(sig.Quantity <= 100); // 300-200=100 超额
        Assert.Contains("倒T平仓", sig.Reason);
    }

    [Fact]
    public void BoughtLow_Blocks_Sell_When_No_Excess_Above_Base()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        policy.Risk.MinRoundTripEdgeBps = 0;
        var runtime = new SymbolRuntimeState
        {
            Symbol = "002371",
            TradeDate = DateTime.Today,
            State = TState.BoughtLow,
            LastBuyPrice = 582m,
            PathRegime = IntradayPathRegime.LowOpenHighGo,
            PathPhase = PathPhase.Locked
        };
        // 已与底仓齐平：倒T无可平超额
        var pos = new Position
        {
            Symbol = "002371",
            BaseQuantity = 200,
            Quantity = 200,
            SellableQuantity = 200,
            AvgCost = 580m
        };
        var now = DateTime.Today.AddHours(13);
        var quote = Q("002371", 592m);
        var eff = policy.ToEffective(new RuntimeOverrides
        {
            PathLabel = "LowOpenHighGo",
            StyleHint = "momentum_t",
            SellDeviationPct = 0.5m
        });
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 580m,
            DeviationPct = (592m - 580m) / 580m * 100m,
            VolumeRatio = 1.2m,
            Now = now,
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 100_000m,
            ReferencePrice = 580m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.None, sig.Action);
        Assert.Contains("保留底仓", sig.Reason);
    }

    [Fact]
    public void Unknown_Path_Blocks_New_Sell_Leg()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        policy.Risk.MinRoundTripEdgeBps = 0;
        var runtime = new SymbolRuntimeState
        {
            Symbol = "002611",
            TradeDate = DateTime.Today,
            State = TState.Idle,
            PathRegime = IntradayPathRegime.Unknown,
            PathPhase = PathPhase.Hypothesis,
            Overrides = new RuntimeOverrides { PathLabel = "Unknown" }
        };
        var pos = new Position
        {
            Symbol = "002611",
            BaseQuantity = 7000,
            Quantity = 7000,
            SellableQuantity = 7000,
            AvgCost = 13m
        };
        var now = DateTime.Today.AddHours(9).AddMinutes(50);
        var quote = Q("002611", 12.94m);
        var eff = policy.ToEffective(runtime.Overrides);
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 12.82m,
            DeviationPct = (12.94m - 12.82m) / 12.82m * 100m,
            VolumeRatio = 1.2m,
            Now = now,
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 50_000m,
            ReferencePrice = 12.8m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.None, sig.Action);
        Assert.Contains("路径未确认", sig.Reason);
    }

    [Fact]
    public void PreferShrink_Blocks_New_Open_Sell_Leg()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        policy.Risk.MinRoundTripEdgeBps = 0;
        policy.Forecast = new ForecastPolicy
        {
            ForDate = DateTime.Today.ToString("yyyy-MM-dd"),
            PredictedHigh = 434m,
            PredictedLow = 380m,
            SoftBandMode = "softPrefer",
            Source = "ai",
            CountsTowardAccuracy = true
        };
        var runtime = new SymbolRuntimeState
        {
            Symbol = "300308",
            TradeDate = DateTime.Today,
            State = TState.Idle,
            PathRegime = IntradayPathRegime.LowOpenHighGo,
            PathPhase = PathPhase.Locked,
            Overrides = new RuntimeOverrides
            {
                PathLabel = "LowOpenHighGo",
                StyleHint = "momentum_t",
                SellDeviationPct = 0.66m,
                PullbackSellPct = 0.55m
            }
        };
        var pos = new Position
        {
            Symbol = "300308",
            BaseQuantity = 600,
            Quantity = 600,
            SellableQuantity = 600,
            AvgCost = 400m
        };
        var now = DateTime.Today.AddHours(9).AddMinutes(46);
        var quote = Q("300308", 390.21m);
        var eff = policy.ToEffective(runtime.Overrides);
        // 回撤触发卖点，但现价未进预测高抛区 → PreferShrink 硬跳过新开腿
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 384.8m,
            DeviationPct = (390.21m - 384.8m) / 384.8m * 100m,
            VolumeRatio = 1.2m,
            PullbackFromHighPct = 0.7m,
            Now = now,
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 80_000m,
            ReferencePrice = 385m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.None, sig.Action);
        Assert.Contains("跳过新开腿", sig.Reason);
    }

    [Fact]
    public void MinBase_Blocks_New_T_When_Only_One_Lot()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        var runtime = new SymbolRuntimeState
        {
            Symbol = "002371",
            TradeDate = DateTime.Today,
            State = TState.Idle,
            PathRegime = IntradayPathRegime.LowOpenHighGo,
            PathPhase = PathPhase.Locked
        };
        var pos = new Position
        {
            Symbol = "002371",
            BaseQuantity = 100,
            Quantity = 100,
            SellableQuantity = 100,
            AvgCost = 580m
        };
        var now = DateTime.Today.AddHours(10);
        var quote = Q("002371", 590m);
        var eff = policy.ToEffective(new RuntimeOverrides { PathLabel = "LowOpenHighGo" });
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 580m,
            DeviationPct = 1.7m,
            VolumeRatio = 1.2m,
            Now = now,
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 50_000m,
            ReferencePrice = 580m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.None, sig.Action);
        Assert.Contains("底仓不足", sig.Reason);
    }
}
