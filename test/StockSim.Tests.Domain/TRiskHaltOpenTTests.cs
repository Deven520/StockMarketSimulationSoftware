using StockSim.Domain.Entities;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Tests.Domain;

public class TRiskHaltOpenTTests
{
    private static QuoteTick Q(string symbol, decimal last) => new()
    {
        Symbol = symbol,
        LastPrice = last,
        Bid = last - 0.01m,
        Ask = last + 0.01m,
        Volume = 1_000_000,
        ChangePercent = -2m,
        Timestamp = DateTime.Now
    };

    private static EvaluateContext Ctx(
        StrategyParameters policy,
        SymbolRuntimeState runtime,
        Position pos,
        QuoteTick quote,
        decimal anchor,
        DateTime now,
        decimal availableCash = 80_000m)
    {
        var overrides = runtime.Overrides;
        var eff = policy.ToEffective(overrides);
        var deviation = anchor > 0 ? (quote.LastPrice - anchor) / anchor * 100m : 0m;
        return new EvaluateContext
        {
            Policy = policy,
            Effective = eff,
            Quote = quote,
            Position = pos,
            Runtime = runtime,
            AnchorPrice = anchor,
            DeviationPct = deviation,
            VolumeRatio = 1.2m,
            Now = now,
            StrategyDefinitionId = Guid.NewGuid(),
            CanDoT = true,
            AvailableCash = availableCash,
            ReferencePrice = anchor
        };
    }

    [Fact]
    public void RiskHalt_Allows_OpenT_BuyBack_When_Price_Not_Chase()
    {
        var policy = StrategyParameters.Default();
        policy.Risk.DayLossStopPct = -1.5m;
        var runtime = new SymbolRuntimeState
        {
            Symbol = "300308",
            TradeDate = DateTime.Today,
            State = TState.SoldHigh,
            OpenTQty = 100,
            LastSellPrice = 400m,
            DayRealizedPnL = -50_000m
        };
        var pos = new Position
        {
            Symbol = "300308",
            BaseQuantity = 600,
            Quantity = 500,
            SellableQuantity = 500,
            AvgCost = 450m
        };
        var now = DateTime.Today.AddHours(10).AddMinutes(5);
        var ctx = Ctx(policy, runtime, pos, Q("300308", 395m), anchor: 410m, now);

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.Buy, sig.Action);
        Assert.True(
            sig.Reason.Contains("关OpenT", StringComparison.Ordinal)
            || sig.Reason.Contains("低吸买回", StringComparison.Ordinal)
            || sig.Reason.Contains("买回", StringComparison.Ordinal));
    }

    [Fact]
    public void RiskHalt_Does_Not_Chase_OpenT_BuyBack_Above_Sell()
    {
        var policy = StrategyParameters.Default();
        policy.Risk.DayLossStopPct = -1.5m;
        var runtime = new SymbolRuntimeState
        {
            Symbol = "002371",
            TradeDate = DateTime.Today,
            State = TState.SoldHigh,
            OpenTQty = 100,
            LastSellPrice = 551m,
            DayRealizedPnL = -30_000m
        };
        var pos = new Position
        {
            Symbol = "002371",
            BaseQuantity = 400,
            Quantity = 300,
            SellableQuantity = 300,
            AvgCost = 540m
        };
        var now = DateTime.Today.AddHours(14).AddMinutes(56);
        var ctx = Ctx(policy, runtime, pos, Q("002371", 569m), anchor: 550m, now);

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.None, sig.Action);
        Assert.DoesNotContain("买回 OpenT", sig.Reason.Replace("关OpenT", ""));
        Assert.True(
            sig.Reason.Contains("留待次日", StringComparison.Ordinal)
            || sig.Reason.Contains("不追买", StringComparison.Ordinal)
            || sig.Reason.Contains("买回价过高", StringComparison.Ordinal)
            || sig.Reason.Contains("高于卖出价", StringComparison.Ordinal));
    }

    [Fact]
    public void ForceFlat_Prefers_Excess_Reduce_Over_Chase_Buy()
    {
        var policy = StrategyParameters.Default();
        policy.Session.ForceFlatBefore = "14:55";
        var runtime = new SymbolRuntimeState
        {
            Symbol = "600893",
            TradeDate = DateTime.Today,
            State = TState.Idle,
            OpenTQty = 0,
            LastBuyPrice = 34.76m
        };
        var pos = new Position
        {
            Symbol = "600893",
            BaseQuantity = 1000,
            Quantity = 1700,
            SellableQuantity = 1700,
            AvgCost = 36m
        };
        var now = DateTime.Today.AddHours(14).AddMinutes(56);
        var ctx = Ctx(policy, runtime, pos, Q("600893", 34.88m), anchor: 35.2m, now);

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.Sell, sig.Action);
        Assert.False(sig.CountsTowardOpenT);
        Assert.True(sig.Quantity >= 600); // 尽量一次减到基准附近
        Assert.True(runtime.ForceFlatExcessReduced);
    }

    [Fact]
    public void DayLoss_Blocks_Idle_New_Sell_Leg()
    {
        var policy = StrategyParameters.Default();
        policy.Risk.DayLossStopPct = -1.5m;
        policy.Factors.UseVolumeConfirm = false;
        var runtime = new SymbolRuntimeState
        {
            Symbol = "300502",
            TradeDate = DateTime.Today,
            State = TState.Idle,
            OpenTQty = 0,
            DayRealizedPnL = -20_000m
        };
        var pos = new Position
        {
            Symbol = "300502",
            BaseQuantity = 600,
            Quantity = 600,
            SellableQuantity = 600,
            AvgCost = 180m
        };
        var now = DateTime.Today.AddHours(10).AddMinutes(8);
        // 相对锚点大涨，若无日亏门控会触发高抛
        var ctx = Ctx(policy, runtime, pos, Q("300502", 170m), anchor: 165m, now);

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.None, sig.Action);
        Assert.True(
            sig.Reason.Contains("禁止新开", StringComparison.Ordinal)
            || sig.Reason.Contains("触发停机", StringComparison.Ordinal)
            || sig.Reason.Contains("风控冻结", StringComparison.Ordinal));
    }

    [Fact]
    public void SoldHigh_Does_Not_Chase_When_Price_Above_LastSell()
    {
        var policy = StrategyParameters.Default();
        policy.Factors.UseVolumeConfirm = false;
        var runtime = new SymbolRuntimeState
        {
            Symbol = "002371",
            TradeDate = DateTime.Today,
            State = TState.SoldHigh,
            OpenTQty = 100,
            LastSellPrice = 551m
        };
        var pos = new Position
        {
            Symbol = "002371",
            BaseQuantity = 400,
            Quantity = 300,
            SellableQuantity = 300,
            AvgCost = 540m
        };
        var now = DateTime.Today.AddHours(11).AddMinutes(30);
        var ctx = Ctx(policy, runtime, pos, Q("002371", 560m), anchor: 550m, now);

        var sig = new IntradayTStrategyEvaluator().Evaluate(ctx);
        Assert.Equal(SignalAction.None, sig.Action);
        Assert.Contains("高于卖出价", sig.Reason);
    }
}
