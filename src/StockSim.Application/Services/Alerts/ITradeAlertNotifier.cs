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
    /// <summary>推送标题：买/卖 股票名称 股数 时间。</summary>
    public string FormatTitle()
    {
        var side = NormalizeSide(Side);
        var name = string.IsNullOrWhiteSpace(Name) ? Symbol : Name.Trim();
        return $"{side} {name} {Quantity}股 {TradedAt:HH:mm:ss}";
    }

    public string FormatText(string titlePrefix = "StockSim")
        => $"{FormatTitle()}"
           + $" | {Symbol} @{Price:F2}"
           + (string.IsNullOrWhiteSpace(StrategyName) ? "" : $" 策略:{StrategyName}")
           + (string.IsNullOrWhiteSpace(titlePrefix) ? "" : $" [{titlePrefix}]");

    private static string NormalizeSide(string side)
    {
        if (string.IsNullOrWhiteSpace(side)) return "买";
        if (side.Contains('卖', StringComparison.Ordinal) || side.Contains("Sell", StringComparison.OrdinalIgnoreCase))
            return "卖";
        if (side.Contains('买', StringComparison.Ordinal) || side.Contains("Buy", StringComparison.OrdinalIgnoreCase))
            return "买";
        return side.Trim();
    }
}
