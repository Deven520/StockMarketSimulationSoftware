namespace StockSim.Domain.MarketData;

public sealed class StockSuggestItem
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public string? Market { get; init; }
}

public interface IStockSuggestProvider
{
    Task<IReadOnlyList<StockSuggestItem>> SuggestAsync(string query, int take = 10, CancellationToken ct = default);

    /// <summary>用代码或名称解析唯一标的；无法唯一确定时返回 null。</summary>
    Task<StockSuggestItem?> ResolveAsync(string? symbolOrName, CancellationToken ct = default);
}
