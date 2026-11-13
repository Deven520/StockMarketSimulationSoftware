using StockSim.Domain.Entities;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Tests.Domain;

public class ForceFlatOpenTQtyTests
{
    private static QuoteTick Q(decimal last) => new()
    {
        Symbol = "300502",
        LastPrice = last,
        Bid = last - 0.01m,
        Ask = last + 0.01m,
        Volume = 1_000_000,
        ChangePercent = -1m,
        Timestamp = DateTime.Now
    };

    [Fact]
    public void ForceFlat_Buys_Back_Only_OpenTQty_Once()
    {
        var policy = StrategyParameters.Default();
        policy.Session.ForceFlatBefore = "14:55";
        var runtime = new SymbolRuntimeState
        {
            Symbol = "300502",
            TradeDate = DateTime.Today,
            State = TState.Idle,
            OpenTQty = 100,
            LastSellPrice = 220m
        };
        var pos = new Position
        {
            Symbol = "300502",
            BaseQuantity = 600,
            Quantity = 500,
            SellableQuantity = 500,
            AvgCost = 200m
        };
        var eff = policy.ToEffective();
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = Q(205m),
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 210m,
            DeviationPct = -2m,
            Now = new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, 14, 56, 0),
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            ReferencePrice = 210m
        };

        var eval = new IntradayTStrategyEvaluator();
        var sig1 = eval.Evaluate(ctx);
        Assert.Equal(SignalAction.Buy, sig1.Action);
        Assert.True(sig1.Quantity <= 100);

        // 模拟成交后清腿并补回底仓
        runtime.OpenTQty = 0;
        runtime.LastSellPrice = null;
        runtime.State = TState.Idle;
        pos.Quantity = 600;
        var sig2 = eval.Evaluate(ctx);
        Assert.Equal(SignalAction.None, sig2.Action);
        Assert.Contains("ForceFlat", sig2.Reason);
    }

    [Fact]
    public void ForceFlat_Excess_Sell_Does_Not_Count_Toward_OpenT_Nor_BuyBack()
    {
        var policy = StrategyParameters.Default();
        policy.Session.ForceFlatBefore = "14:55";
        var runtime = new SymbolRuntimeState
        {
            Symbol = "300502",
            TradeDate = DateTime.Today,
            State = TState.Idle,
            OpenTQty = 0,
            LastBuyPrice = 190m
        };
        var pos = new Position
        {
            Symbol = "300502",
            BaseQuantity = 600,
            Quantity = 900,
            SellableQuantity = 900,
            AvgCost = 188m
        };
        var eff = policy.ToEffective();
        var now = new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, 14, 56, 0);
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = Q(187m),
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 193m,
            DeviationPct = -3m,
            Now = now,
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = 50_000m,
            ReferencePrice = 205m
        };

        var eval = new IntradayTStrategyEvaluator();
        var sig1 = eval.Evaluate(ctx);
        Assert.Equal(SignalAction.Sell, sig1.Action);
        Assert.False(sig1.CountsTowardOpenT);
        Assert.Contains("减仓", sig1.Reason);
        Assert.True(runtime.ForceFlatExcessReduced);

        // 模拟成交：不计入 OpenT，仓位减少一手
        pos.Quantity = 800;
        runtime.OpenTQty = 0;
        runtime.State = TState.Idle;
        var sig2 = eval.Evaluate(ctx);
        // 仍可继续减超配，但绝不能买回
        Assert.NotEqual(SignalAction.Buy, sig2.Action);
        if (sig2.Action == SignalAction.Sell)
            Assert.False(sig2.CountsTowardOpenT);
    }

    [Fact]
    public void SoftPrefer_Allows_Sell_When_Deviation_Met()
    {
        var policy = StrategyParameters.Default();
        policy.Forecast = new ForecastPolicy
        {
            ForDate = DateTime.Today.ToString("yyyy-MM-dd"),
            PredictedHigh = 257m,
            PredictedLow = 200m,
            SoftBandMode = "softPrefer",
            CapHighByLimitPct = 12m,
            Source = "ai"
        };
        policy.Triggers.SellDeviationPct = new ParamRange { Base = 1.0m, Min = 0.5m, Max = 1.5m };
        var runtime = new SymbolRuntimeState { Symbol = "300502", State = TState.Idle };
        var pos = new Position
        {
            Symbol = "300502",
            BaseQuantity = 600,
            Quantity = 600,
            SellableQuantity = 600,
            AvgCost = 200m
        };
        var overrides = new RuntimeOverrides { SellDeviationPct = 1.0m, PositionPctPerTrade = 0.2m };
        var eff = policy.ToEffective(overrides);
        var ctx = new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = Q(224m),
            Position = pos,
            Runtime = runtime,
            AnchorPrice = 218m,
            DeviationPct = (224m - 218m) / 218m * 100m,
            VolumeRatio = 1.2m,
            Now = new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, 10, 5, 0),
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            ReferencePrice = 212m,
            PullbackFromHighPct = 0.2m
        };

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.Sell, sig.Action);
        Assert.Contains("高抛", sig.Reason);
    }
}
