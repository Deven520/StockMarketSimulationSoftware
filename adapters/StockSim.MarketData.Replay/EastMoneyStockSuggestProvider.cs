using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using StockSim.Domain.MarketData;

namespace StockSim.MarketData.Replay;

/// <summary>东方财富股票代码/名称联想。</summary>
public sealed class EastMoneyStockSuggestProvider : IStockSuggestProvider
{
    private static readonly Regex Digits = new(@"^\d{1,6}$", RegexOptions.Compiled);
    private readonly HttpClient _http;
    private readonly ILogger<EastMoneyStockSuggestProvider> _logger;

    public EastMoneyStockSuggestProvider(HttpClient http, ILogger<EastMoneyStockSuggestProvider> logger)
    {
        _http = http;
        _logger = logger;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public async Task<IReadOnlyList<StockSuggestItem>> SuggestAsync(string query, int take = 10, CancellationToken ct = default)
    {
        query = (query ?? "").Trim();
        if (query.Length < 1) return Array.Empty<StockSuggestItem>();
        take = Math.Clamp(take, 1, 20);

        try
        {
            var url =
                $"https://searchapi.eastmoney.com/api/suggest/get?input={Uri.EscapeDataString(query)}&type=14&token=D43XXQ4ZGfdxwFL1sdMgunVWa4BAAJO&count={take}";
            await using var stream = await _http.GetStreamAsync(url, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("QuotationCodeTable", out var table))
                return Array.Empty<StockSuggestItem>();
            if (!table.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<StockSuggestItem>();

            var list = new List<StockSuggestItem>();
            foreach (var row in data.EnumerateArray())
            {
                var code = row.TryGetProperty("Code", out var c) ? c.GetString() : null;
                var name = row.TryGetProperty("Name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) continue;
                // 只要 A 股 6 位
                var symbol = new string(code.Where(char.IsDigit).ToArray());
                if (symbol.Length != 6) continue;
                var market = row.TryGetProperty("SecurityTypeName", out var m) ? m.GetString() : null;
                list.Add(new StockSuggestItem { Symbol = symbol, Name = name!, Market = market });
                if (list.Count >= take) break;
            }

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stock suggest failed for {Query}", query);
            return Array.Empty<StockSuggestItem>();
        }
    }

    public async Task<StockSuggestItem?> ResolveAsync(string? symbolOrName, CancellationToken ct = default)
    {
        var q = (symbolOrName ?? "").Trim();
        if (q.Length == 0) return null;

        // 纯 6 位代码：优先精确
        if (Digits.IsMatch(q) && q.Length == 6)
        {
            var byCode = await SuggestAsync(q, 5, ct);
            var exact = byCode.FirstOrDefault(x => x.Symbol == q);
            if (exact is not null) return exact;
            // 代码联想失败时仍允许用该代码（名称暂用代码）
            return new StockSuggestItem { Symbol = q, Name = q };
        }

        var list = await SuggestAsync(q, 10, ct);
        if (list.Count == 0) return null;

        // 名称完全匹配优先
        var nameExact = list.FirstOrDefault(x =>
            string.Equals(x.Name, q, StringComparison.OrdinalIgnoreCase));
        if (nameExact is not null) return nameExact;

        // 仅一条结果则采用
        if (list.Count == 1) return list[0];

        // 代码前缀唯一
        if (Digits.IsMatch(q))
        {
            var prefixed = list.Where(x => x.Symbol.StartsWith(q, StringComparison.Ordinal)).ToList();
            if (prefixed.Count == 1) return prefixed[0];
        }

        // 名称包含且唯一
        var nameContains = list.Where(x => x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (nameContains.Count == 1) return nameContains[0];

        // 多条歧义：取第一条（UI 应通过联想选定）
        return list[0];
    }
}
