namespace StockSim.Domain.MarketData;

public sealed class QuoteTick
{
    public required string Symbol { get; init; }
    public decimal LastPrice { get; init; }
    public decimal Bid { get; init; }
    public decimal Ask { get; init; }
    public long Volume { get; init; }
    public decimal ChangePercent { get; init; }
    public bool IsSuspended { get; init; }
    public bool IsLimitUp { get; init; }
    public bool IsLimitDown { get; init; }
    public DateTime Timestamp { get; init; }
}

public interface IMarketDataProvider
{
    string Name { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<QuoteTick?> GetLastAsync(string symbol, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QuoteTick>> GetBatchAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default);
}
