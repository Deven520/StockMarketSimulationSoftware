using System.Text.Json;
using Microsoft.Extensions.Logging;
using StockSim.Domain.MarketData;

namespace StockSim.MarketData.Replay;

/// <summary>东方财富公开资讯列表（失败时返回空列表，不阻断策略生成）。</summary>
public sealed class EastMoneyMarketNewsProvider : IMarketNewsProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<EastMoneyMarketNewsProvider> _logger;

    public EastMoneyMarketNewsProvider(HttpClient http, ILogger<EastMoneyMarketNewsProvider> logger)
    {
        _http = http;
        _logger = logger;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public async Task<IReadOnlyList<MarketNewsItem>> GetHotNewsAsync(int take = 15, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 30);
        try
        {
            // 财经要闻栏目
            var url =
                $"https://np-listapi.eastmoney.com/comm/web/getNewsByColumns?client=web&biz=web_news_col&column=350&order=1&needInteractData=0&page_index=1&page_size={take}&req_trace={Guid.NewGuid():N}";
            using var doc = await GetJsonAsync(url, ct);
            return ParseNewsList(doc.RootElement, take);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fetch hot news failed");
            return Array.Empty<MarketNewsItem>();
        }
    }

    public async Task<IReadOnlyList<MarketNewsItem>> GetStockNewsAsync(string symbol, int take = 10, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 20);
        symbol = new string((symbol ?? "").Where(char.IsDigit).ToArray());
        if (symbol.Length != 6) return Array.Empty<MarketNewsItem>();

        try
        {
            var url =
                $"https://search-api-web.eastmoney.com/search/jsonp?cb=jQuery&param=%7B%22uid%22%3A%22%22%2C%22keyword%22%3A%22{symbol}%22%2C%22type%22%3A%5B%22cmsArticleWebOld%22%5D%2C%22client%22%3A%22web%22%2C%22clientType%22%3A%22web%22%2C%22clientVersion%22%3A%22curr%22%2C%22param%22%3A%7B%22cmsArticleWebOld%22%3A%7B%22searchScope%22%3A%22default%22%2C%22sort%22%3A%22default%22%2C%22pageIndex%22%3A1%2C%22pageSize%22%3A{take}%7D%7D%7D";
            var text = await _http.GetStringAsync(url, ct);
            var jsonStart = text.IndexOf('(');
            var jsonEnd = text.LastIndexOf(')');
            if (jsonStart < 0 || jsonEnd <= jsonStart) return Array.Empty<MarketNewsItem>();
            var json = text[(jsonStart + 1)..jsonEnd];
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result)) return Array.Empty<MarketNewsItem>();
            if (!result.TryGetProperty("cmsArticleWebOld", out var articles) ||
                articles.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<MarketNewsItem>();
            }

            var list = new List<MarketNewsItem>();
            foreach (var a in articles.EnumerateArray().Take(take))
            {
                var title = a.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(title)) continue;
                list.Add(new MarketNewsItem
                {
                    Title = title!,
                    Summary = a.TryGetProperty("content", out var c) ? Truncate(c.GetString(), 120) : null,
                    Source = a.TryGetProperty("mediaName", out var m) ? m.GetString() : "东方财富",
                    Url = a.TryGetProperty("url", out var u) ? u.GetString() : null
                });
            }

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fetch stock news failed for {Symbol}", symbol);
            return Array.Empty<MarketNewsItem>();
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync(url, ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static IReadOnlyList<MarketNewsItem> ParseNewsList(JsonElement root, int take)
    {
        if (!root.TryGetProperty("data", out var data)) return Array.Empty<MarketNewsItem>();
        JsonElement listEl;
        if (data.TryGetProperty("list", out listEl) && listEl.ValueKind == JsonValueKind.Array)
        {
            /* ok */
        }
        else if (data.ValueKind == JsonValueKind.Array)
        {
            listEl = data;
        }
        else
        {
            return Array.Empty<MarketNewsItem>();
        }

        var list = new List<MarketNewsItem>();
        foreach (var item in listEl.EnumerateArray().Take(take))
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString()
                : item.TryGetProperty("Title", out var t2) ? t2.GetString() : null;
            if (string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new MarketNewsItem
            {
                Title = title!,
                Summary = item.TryGetProperty("digest", out var d) ? d.GetString()
                    : item.TryGetProperty("summary", out var s) ? s.GetString() : null,
                Source = item.TryGetProperty("mediaName", out var m) ? m.GetString() : "东方财富",
                PublishedAt = TryParseTime(item),
                Url = item.TryGetProperty("url", out var u) ? u.GetString()
                    : item.TryGetProperty("Url", out var u2) ? u2.GetString() : null
            });
        }

        return list;
    }

    private static DateTime? TryParseTime(JsonElement item)
    {
        foreach (var name in new[] { "showTime", "date", "publishTime" })
        {
            if (!item.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) continue;
            if (DateTime.TryParse(p.GetString(), out var dt)) return dt;
        }

        return null;
    }

    private static string? Truncate(string? s, int n)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        return s.Length <= n ? s : s[..n] + "…";
    }
}
