using Microsoft.Extensions.Caching.Memory;
using StockSim.Domain.MarketData;

namespace StockSim.Application.Services;

/// <summary>轻量报价短时内存缓存（默认 30s）。</summary>
public sealed class CachedLightQuoteService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly IStockDetailProvider _provider;
    private readonly IMemoryCache _cache;

    public CachedLightQuoteService(IStockDetailProvider provider, IMemoryCache cache)
    {
        _provider = provider;
        _cache = cache;
    }

    public async Task<IReadOnlyDictionary<string, LightStockQuote>> GetQuotesAsync(
        IEnumerable<string> symbols,
        CancellationToken ct = default)
    {
        var keys = symbols
            .Select(Normalize)
            .Where(s => s.Length == 6)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, LightStockQuote>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var symbol in keys)
        {
            if (_cache.TryGetValue(CacheKey(symbol), out LightStockQuote? hit) && hit is not null)
                result[symbol] = hit;
            else
                missing.Add(symbol);
        }

        if (missing.Count == 0) return result;

        var fetched = await _provider.GetLightQuotesAsync(missing, ct);
        foreach (var q in fetched)
        {
            var symbol = Normalize(q.Symbol);
            _cache.Set(CacheKey(symbol), q, Ttl);
            result[symbol] = q;
        }

        return result;
    }

    public async Task<LightStockQuote?> GetQuoteAsync(string symbol, string? name = null, CancellationToken ct = default)
    {
        symbol = Normalize(symbol);
        if (symbol.Length != 6) return null;

        if (_cache.TryGetValue(CacheKey(symbol), out LightStockQuote? hit) && hit is not null)
            return hit;

        var q = await _provider.GetLightQuoteAsync(symbol, name, ct);
        if (q is not null)
            _cache.Set(CacheKey(symbol), q, Ttl);
        return q;
    }

    private static string Normalize(string symbol) =>
        new string((symbol ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string CacheKey(string symbol) => "lightquote:" + symbol;
}
