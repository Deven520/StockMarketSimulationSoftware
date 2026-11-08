using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Domain.Trading;

public sealed class RiskCheckResult
{
    public bool IsAllowed { get; init; }
    public string? Reason { get; init; }

    public static RiskCheckResult Ok() => new() { IsAllowed = true };
    public static RiskCheckResult Reject(string reason) => new() { IsAllowed = false, Reason = reason };
}

public interface IRiskChecker
{
    RiskCheckResult Validate(
        TradeSignal signal,
        SimAccount account,
        Position? position,
        QuoteTick quote,
        int tradesToday,
        decimal? cashBudget = null);
}

public sealed class DefaultRiskChecker : IRiskChecker
{
    public RiskCheckResult Validate(
        TradeSignal signal,
        SimAccount account,
        Position? position,
        QuoteTick quote,
        int tradesToday,
        decimal? cashBudget = null)
    {
        if (signal.Action == SignalAction.None || signal.Quantity <= 0)
        {
            return RiskCheckResult.Reject("无效信号");
        }

        if (quote.IsSuspended)
        {
            return RiskCheckResult.Reject("停牌不可交易");
        }

        if (signal.Action == SignalAction.Buy && quote.IsLimitUp)
        {
            return RiskCheckResult.Reject("涨停不可买入");
        }

        if (signal.Action == SignalAction.Sell && quote.IsLimitDown)
        {
            return RiskCheckResult.Reject("跌停不可卖出");
        }

        if (signal.Quantity % 100 != 0)
        {
            return RiskCheckResult.Reject("数量须为100股整数倍");
        }

        if (signal.Action == SignalAction.Buy)
        {
            var need = signal.Price * signal.Quantity;
            var cash = cashBudget ?? account.Cash;
            if (cash < need)
            {
                return RiskCheckResult.Reject("可用资金不足");
            }
        }

        if (signal.Action == SignalAction.Sell)
        {
            if (position is null || position.Quantity <= 0)
            {
                return RiskCheckResult.Reject("底仓不足不可卖出做T");
            }

            var sellable = account.TradingMode == TradingMode.TPlusOneStrict
                ? position.SellableQuantity
                : position.Quantity;

            if (signal.Quantity > sellable)
            {
                return RiskCheckResult.Reject("可卖数量不足");
            }
        }

        return RiskCheckResult.Ok();
    }
}
