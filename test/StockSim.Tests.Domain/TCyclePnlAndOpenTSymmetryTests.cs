using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Tests.Domain;

public class TCyclePnlAndOpenTSymmetryTests
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
    public void CycleNet_Uses_SellBuy_Pair_Not_Cost_Basis()
    {
        var legs = new[]
        {
            new TCyclePnlEstimator.Leg(OrderSide.Sell, 13.01m, 100, 8m),
            new TCyclePnlEstimator.Leg(OrderSide.Buy, 12.89m, 100, 7m)
        };
        var (gross, fees, rounds) = TCyclePnlEstimator.Analyze(legs);
        Assert.Equal(12m, gross); // (13.01-12.89)*100
        Assert.Equal(15m, fees);
        Assert.Equal(-3m, TCyclePnlEstimator.CycleNet(legs));
        Assert.Equal(1, rounds);
    }

    [Fact]
    public void CycleNet_Fifo_Partial_Lots()
    {
        var legs = new[]
        {
            new TCyclePnlEstimator.Leg(OrderSide.Sell, 10m, 200, 10m),
            new TCyclePnlEstimator.Leg(OrderSide.Buy, 9.5m, 100, 5m),
            new TCyclePnlEstimator.Leg(OrderSide.Buy, 9.8m, 100, 5m)
        };
        var (gross, _, rounds) = TCyclePnlEstimator.Analyze(legs);
        Assert.Equal(70m, gross); // 0.5*100 + 0.2*100
        Assert.Equal(1, rounds); // 卖腿整笔关闭算 1 次完整往返
    }

    [Fact]
    public void SoldHigh_BuyBack_Caps_To_OpenTQty_When_No_Deficit()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        policy.Risk.MinRoundTripEdgeBps = 0;
        var runtime = new SymbolRuntimeState
        {
            Symbol = "600893",
            TradeDate = DateTime.Today,
            State = TState.SoldHigh,
            OpenTQty = 100,
            LastSellPrice = 36m
        };
        // Base 已与持仓齐平：无底仓缺口，旧逻辑会按 PositionPct 放大买回
        var pos = new Position
        {
            Symbol = "600893",
            BaseQuantity = 1000,
            Quantity = 1000,
            SellableQuantity = 1000,
            AvgCost = 36m
        };
        var now = DateTime.Today.AddHours(10).AddMinutes(30);
        var quote = Q("600893", 35.5m);
        var eff = policy.ToEffective();
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 36.5m,
            DeviationPct = (35.5m - 36.5m) / 36.5m * 100m,
            VolumeRatio = 1.2m,
            Now = now,
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 80_000m,
            ReferencePrice = 36m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.Buy, sig.Action);
        Assert.True(sig.Quantity <= 100, $"买回应≤OpenTQty，实际 {sig.Quantity}");
    }

    [Fact]
    public void LowOpenHighGo_Blocks_Early_Deviation_Sell_Away_From_Forecast_High()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        policy.Risk.MinRoundTripEdgeBps = 0;
        policy.Path.PullbackSellPct = 0.8m;
        policy.Forecast = new ForecastPolicy
        {
            ForDate = DateTime.Today.ToString("yyyy-MM-dd"),
            PredictedHigh = 580m,
            PredictedLow = 540m,
            SoftBandMode = "softPrefer",
            Source = "ai",
            CountsTowardAccuracy = true
        };
        var runtime = new SymbolRuntimeState
        {
            Symbol = "002371",
            TradeDate = DateTime.Today,
            State = TState.Idle,
            Overrides = new RuntimeOverrides
            {
                StyleHint = "momentum_t",
                PathLabel = "LowOpenHighGo",
                SellDeviationPct = 0.85m,
                BuyDeviationPct = -0.6m,
                PullbackSellPct = 0.6m
            }
        };
        var pos = new Position
        {
            Symbol = "002371",
            BaseQuantity = 400,
            Quantity = 400,
            SellableQuantity = 400,
            AvgCost = 550m
        };
        var now = DateTime.Today.AddHours(9).AddMinutes(45);
        // 早盘仅小幅偏离，远离预测高
        var quote = Q("002371", 556m);
        var eff = policy.ToEffective(runtime.Overrides);
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 550m,
            DeviationPct = (556m - 550m) / 550m * 100m,
            VolumeRatio = 1.2m,
            PullbackFromHighPct = 0.1m,
            Now = now,
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 100_000m,
            ReferencePrice = 550m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.None, sig.Action);
    }

    [Fact]
    public void MinRoundTripEdge_Blocks_Thin_Band()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        policy.Risk.MinRoundTripEdgeBps = 40m;
        policy.Triggers.BuyDeviationPct = new ParamRange { Base = -0.3m, Min = -0.5m, Max = -0.2m };
        policy.Triggers.SellDeviationPct = new ParamRange { Base = 0.3m, Min = 0.2m, Max = 0.5m };
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
        var now = DateTime.Today.AddHours(10).AddMinutes(10);
        var quote = Q("600893", 35.5m);
        var eff = policy.ToEffective(runtime.Overrides);
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 35m,
            DeviationPct = (35.5m - 35m) / 35m * 100m,
            VolumeRatio = 1.2m,
            Now = now,
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
