namespace StockSim.Domain.MarketData;

public sealed class MarketNewsItem
{
    public required string Title { get; init; }
    public string? Summary { get; init; }
    public string? Source { get; init; }
    public DateTime? PublishedAt { get; init; }
    public string? Url { get; init; }
}

/// <summary>热点/个股新闻（可替换为真实资讯源）。</summary>
public interface IMarketNewsProvider
{
    Task<IReadOnlyList<MarketNewsItem>> GetHotNewsAsync(int take = 15, CancellationToken ct = default);

    Task<IReadOnlyList<MarketNewsItem>> GetStockNewsAsync(string symbol, int take = 10, CancellationToken ct = default);
}
