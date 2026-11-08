namespace StockSim.Application.Services.Alerts;

public interface ITradeAlertNotifier
{
    Task NotifyAsync(TradeAlertMessage message, CancellationToken cancellationToken = default);

    /// <summary>页面「监控测试」：忽略 Enabled 与去重，返回是否发出及说明。</summary>
    Task<(bool Ok, string Message)> SendTestAsync(
        TradeAlertMessage message,
        CancellationToken cancellationToken = default);
}

public sealed record TradeAlertMessage(
    string Symbol,
    string Name,
    string Side,
    int Quantity,
    decimal Price,
    DateTime TradedAt,
    string? StrategyName,
    Guid? TradeId = null)
{
    public string FormatText(string titlePrefix = "StockSim")
        => $"[{titlePrefix}] {Side} {Symbol} {Name} {Quantity}股 @{Price:F2}"
           + (string.IsNullOrWhiteSpace(StrategyName) ? "" : $" 策略:{StrategyName}")
           + $" {TradedAt:HH:mm:ss}";
}
