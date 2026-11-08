using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.Strategies;

namespace StockSim.Domain.Trading;

public sealed class MatchResult
{
    public required Order Order { get; init; }
    public Trade? Trade { get; init; }
}

public interface IMatchingEngine
{
    MatchResult PlaceAndFill(
        SimAccount account,
        Position? position,
        TradeSignal signal,
        DateTime tradeDate,
        bool isManual,
        int? maxTradesPerDay,
        int tradesToday,
        decimal? cashBudget = null,
        Action<decimal>? onCashBudgetChanged = null);
}

public sealed class InstantMatchingEngine : IMatchingEngine
{
    private readonly IFeeCalculator _feeCalculator;
    private readonly IRiskChecker _riskChecker;

    public InstantMatchingEngine(IFeeCalculator feeCalculator, IRiskChecker riskChecker)
    {
        _feeCalculator = feeCalculator;
        _riskChecker = riskChecker;
    }

    public MatchResult PlaceAndFill(
        SimAccount account,
        Position? position,
        TradeSignal signal,
        DateTime tradeDate,
        bool isManual,
        int? maxTradesPerDay,
        int tradesToday,
        decimal? cashBudget = null,
        Action<decimal>? onCashBudgetChanged = null)
    {
        var order = new Order
        {
            SimAccountId = account.Id,
            StrategyDefinitionId = signal.StrategyDefinitionId,
            StockPoolId = signal.StockPoolId,
            Symbol = signal.Symbol,
            Side = signal.Action == SignalAction.Buy ? OrderSide.Buy : OrderSide.Sell,
            Quantity = signal.Quantity,
            Price = signal.Price,
            TradeDate = tradeDate.Date,
            IsManual = isManual,
            Status = OrderStatus.Pending
        };

        if (maxTradesPerDay.HasValue && tradesToday >= maxTradesPerDay.Value && !isManual)
        {
            order.Status = OrderStatus.Rejected;
            order.RejectReason = "超过单日做T次数上限";
            return new MatchResult { Order = order };
        }

        // Quote 校验在上层传入 RiskChecker 前完成；此处用账户/持仓再校验一次资金与可卖。
        var fakeQuote = new MarketData.QuoteTick
        {
            Symbol = signal.Symbol,
            LastPrice = signal.Price,
            Bid = signal.Price,
            Ask = signal.Price,
            Timestamp = DateTime.UtcNow
        };

        var risk = _riskChecker.Validate(signal, account, position, fakeQuote, tradesToday, cashBudget);
        if (!risk.IsAllowed)
        {
            order.Status = OrderStatus.Rejected;
            order.RejectReason = risk.Reason;
            return new MatchResult { Order = order };
        }

        var fee = _feeCalculator.Calculate(order.Side, order.Price, order.Quantity);
        var amount = order.Price * order.Quantity;
        var budget = cashBudget ?? account.Cash;

        if (order.Side == OrderSide.Buy)
        {
            var cost = amount + fee.Total;
            if (budget < cost)
            {
                order.Status = OrderStatus.Rejected;
                order.RejectReason = "含费用后资金不足";
                return new MatchResult { Order = order };
            }

            budget -= cost;
            account.Cash -= cost;
            if (position is null)
            {
                position = new Position
                {
                    SimAccountId = account.Id,
                    Symbol = order.Symbol,
                    Name = order.Symbol,
                    Quantity = order.Quantity,
                    BaseQuantity = order.Quantity,
                    SellableQuantity = account.TradingMode == TradingMode.SimIntradaySellAllowed
                        ? order.Quantity
                        : 0,
                    AvgCost = order.Price
                };
                account.Positions.Add(position);
            }
            else
            {
                var newQty = position.Quantity + order.Quantity;
                position.AvgCost = newQty == 0
                    ? 0
                    : ((position.AvgCost * position.Quantity) + amount) / newQty;
                position.Quantity = newQty;
                if (position.BaseQuantity < 100 && newQty >= 100)
                    position.BaseQuantity = newQty;
                if (account.TradingMode == TradingMode.SimIntradaySellAllowed)
                {
                    position.SellableQuantity += order.Quantity;
                }
            }
        }
        else
        {
            var credit = amount - fee.Total;
            budget += credit;
            account.Cash += credit;
            position!.Quantity -= order.Quantity;
            position.SellableQuantity = Math.Max(0, position.SellableQuantity - order.Quantity);
            if (position.Quantity == 0)
            {
                position.AvgCost = 0;
            }
        }

        if (cashBudget.HasValue)
        {
            onCashBudgetChanged?.Invoke(budget);
        }

        order.Status = OrderStatus.Filled;
        var trade = new Trade
        {
            OrderId = order.Id,
            SimAccountId = account.Id,
            StrategyDefinitionId = order.StrategyDefinitionId,
            StockPoolId = order.StockPoolId,
            Symbol = order.Symbol,
            Side = order.Side,
            Quantity = order.Quantity,
            Price = order.Price,
            Commission = fee.Commission,
            StampTax = fee.StampTax,
            TransferFee = fee.TransferFee,
            Slippage = fee.Slippage,
            TotalFee = fee.Total,
            Amount = amount,
            TradeDate = tradeDate.Date,
            TradedAt = DateTime.UtcNow
        };

        return new MatchResult { Order = order, Trade = trade };
    }
}
