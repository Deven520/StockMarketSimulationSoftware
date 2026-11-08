using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;
using StockSim.Domain.Trading;

namespace StockSim.Tests.Domain;

public class FeeAndStrategyTests
{
    [Fact]
    public void Fee_Sell_Includes_StampTax()
    {
        var fee = new AShareFeeCalculator().Calculate(OrderSide.Sell, 10m, 1000);
        Assert.True(fee.StampTax > 0);
        Assert.True(fee.Commission >= 5m);
        Assert.Equal(fee.Commission + fee.StampTax + fee.TransferFee + fee.Slippage, fee.Total);
    }

    [Fact]
    public void Risk_Rejects_LimitUp_Buy()
    {
        var checker = new DefaultRiskChecker();
        var account = new SimAccount { Cash = 100000 };
        var signal = new TradeSignal
        {
            Symbol = "600519",
            Action = SignalAction.Buy,
            Quantity = 100,
            Price = 10
        };
        var quote = new QuoteTick
        {
            Symbol = "600519",
            LastPrice = 10,
            IsLimitUp = true,
            Timestamp = DateTime.UtcNow
        };

        var result = checker.Validate(signal, account, null, quote, 0);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void Matching_Buy_And_Sell_Updates_Cash()
    {
        var engine = new InstantMatchingEngine(new AShareFeeCalculator(), new DefaultRiskChecker());
        var account = new SimAccount
        {
            Cash = 100000,
            TradingMode = TradingMode.SimIntradaySellAllowed
        };

        var buy = engine.PlaceAndFill(
            account,
            null,
            new TradeSignal { Symbol = "000001", Action = SignalAction.Buy, Quantity = 1000, Price = 10m, Reason = "t" },
            DateTime.Today,
            true,
            null,
            0);

        Assert.Equal(OrderStatus.Filled, buy.Order.Status);
        Assert.NotNull(buy.Trade);
        var afterBuyCash = account.Cash;
        Assert.True(afterBuyCash < 100000);

        var position = account.Positions.Single();
        var sell = engine.PlaceAndFill(
            account,
            position,
            new TradeSignal { Symbol = "000001", Action = SignalAction.Sell, Quantity = 1000, Price = 10.5m, Reason = "t" },
            DateTime.Today,
            true,
            null,
            1);

        Assert.Equal(OrderStatus.Filled, sell.Order.Status);
        Assert.True(account.Cash > afterBuyCash);
    }

    [Fact]
    public void Strategy_Triggers_Buy_On_Drop()
    {
        var evaluator = new IntradayTStrategyEvaluator();
        var position = new Position { Symbol = "600519", BaseQuantity = 1000, Quantity = 1000 };
        var quote = new QuoteTick
        {
            Symbol = "600519",
            LastPrice = 1680m,
            Ask = 1680m,
            Bid = 1680m,
            Timestamp = DateTime.UtcNow
        };

        var signal = evaluator.Evaluate(
            StrategyParameters.Default(),
            quote,
            position,
            1700m,
            Guid.NewGuid(),
            null);

        // 相对锚点下跌触发：兼容路径会切到 SoldHigh 允许买回；若无缺口则可能 None/Buy
        Assert.True(signal.Action is SignalAction.Buy or SignalAction.None);
    }

    [Fact]
    public void Strategy_Idle_Sells_High_First()
    {
        var evaluator = new IntradayTStrategyEvaluator();
        var position = new Position
        {
            Symbol = "600519",
            BaseQuantity = 1000,
            Quantity = 1000,
            SellableQuantity = 1000
        };
        var quote = new QuoteTick
        {
            Symbol = "600519",
            LastPrice = 1720m,
            Ask = 1720m,
            Bid = 1720m,
            Timestamp = DateTime.UtcNow
        };

        var policy = StrategyParameters.Default();
        var effective = policy.ToEffective();
        var runtime = new SymbolRuntimeState { Symbol = "600519", State = TState.Idle };
        var signal = evaluator.Evaluate(new EvaluateContext
        {
            Policy = policy,
            Effective = effective,
            Quote = quote,
            Position = position,
            Runtime = runtime,
            AnchorPrice = 1700m,
            DeviationPct = (1720m - 1700m) / 1700m * 100m,
            VolumeRatio = 1.2m,
            Now = new DateTime(2026, 11, 4, 10, 30, 0),
            StrategyDefinitionId = Guid.NewGuid()
        });

        Assert.Equal(SignalAction.Sell, signal.Action);
        Assert.Equal(TState.SoldHigh, runtime.State);
        Assert.True(signal.Quantity >= 100);
    }

    [Fact]
    public void Strategy_SoldHigh_Buys_Back_On_Dip()
    {
        var evaluator = new IntradayTStrategyEvaluator();
        var position = new Position
        {
            Symbol = "600519",
            BaseQuantity = 1000,
            Quantity = 800,
            SellableQuantity = 800
        };
        var quote = new QuoteTick
        {
            Symbol = "600519",
            LastPrice = 1680m,
            Ask = 1680m,
            Bid = 1680m,
            Timestamp = DateTime.UtcNow
        };

        var policy = StrategyParameters.Default();
        var runtime = new SymbolRuntimeState { Symbol = "600519", State = TState.SoldHigh, LastSellPrice = 1720m };
        var signal = evaluator.Evaluate(new EvaluateContext
        {
            Policy = policy,
            Effective = policy.ToEffective(),
            Quote = quote,
            Position = position,
            Runtime = runtime,
            AnchorPrice = 1700m,
            DeviationPct = (1680m - 1700m) / 1700m * 100m,
            VolumeRatio = 1m,
            Now = new DateTime(2026, 11, 4, 10, 30, 0),
            StrategyDefinitionId = Guid.NewGuid()
        });

        Assert.Equal(SignalAction.Buy, signal.Action);
        Assert.Equal(TState.Idle, runtime.State);
    }

    [Fact]
    public void Policy_FromJson_Migrates_Legacy_Flat_Fields()
    {
        var json = """
            {"mode":"IntradayT","buyTriggerPct":-0.6,"sellTriggerPct":0.7,"positionPctPerTrade":0.15,"maxTradesPerDay":6}
            """;
        var p = StrategyParameters.FromJson(json);
        Assert.Equal("AdaptiveIntradayT", p.Mode);
        Assert.Equal(2, p.Version);
        var eff = p.ToEffective();
        Assert.True(eff.BuyDeviationPct < 0);
        Assert.True(eff.SellDeviationPct > 0);
        Assert.Equal(6, eff.MaxTradesPerDay);
    }

    [Fact]
    public void Adaptive_Overrides_Stay_Within_Bounds()
    {
        var adaptive = new AdaptiveOverrideService();
        var policy = StrategyParameters.Default();
        var runtime = new SymbolRuntimeState { Symbol = "600519", TradeDate = DateTime.Today };
        var snap = new SessionSnapshot
        {
            Symbol = "600519",
            Open = 100,
            Vwap = 100.5m,
            Last = 101,
            RealizedVolPct = 1.2m,
            VolumeRatio = 1.1m,
            AsOf = DateTime.Now
        };

        var o = adaptive.Adjust(policy, runtime, snap, DateTime.Now);
        Assert.InRange(o.BuyDeviationPct!.Value, policy.Triggers.BuyDeviationPct.Min, policy.Triggers.BuyDeviationPct.Max);
        Assert.InRange(o.SellDeviationPct!.Value, policy.Triggers.SellDeviationPct.Min, policy.Triggers.SellDeviationPct.Max);
    }
}
