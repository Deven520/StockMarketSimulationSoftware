using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using StockSim.Domain.MarketData;

namespace StockSim.MarketData.Replay;

/// <summary>
/// 东财 push2 TLS/链路失败时的公开行情备用（腾讯行情 + 新浪快照）。
/// 仅作真价锚点与日K，不回退模拟价。
/// </summary>
internal static class PublicQuoteFallback
{
    private static readonly Regex SinaLine = new(
        @"hq_str_(?<code>[a-z]{2}\d{6})\s*=\s*""(?<body>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ToMarketCode(string symbol)
    {
        if (symbol.StartsWith('6') || symbol.StartsWith('5') || symbol.StartsWith('9'))
            return "sh" + symbol;
        if (symbol.StartsWith('4') || symbol.StartsWith('8'))
            return "bj" + symbol;
        return "sz" + symbol;
    }

    public static async Task<LightStockQuote?> TryGetLightQuoteAsync(
        HttpClient http,
        ILogger logger,
        string symbol,
        string? name,
        CancellationToken ct)
    {
        var tx = await TryTencentQuoteAsync(http, logger, symbol, name, ct);
        if (tx is not null) return tx;
        return await TrySinaQuoteAsync(http, logger, symbol, name, ct);
    }

    public static async Task<(QuoteBoard Board, IReadOnlyList<DailyBar> Daily, string Note)?> TryGetDetailPartsAsync(
        HttpClient http,
        ILogger logger,
        string symbol,
        string? name,
        CancellationToken ct)
    {
        var code = ToMarketCode(symbol);
        LightStockQuote? light = await TryTencentQuoteAsync(http, logger, symbol, name, ct)
                                 ?? await TrySinaQuoteAsync(http, logger, symbol, name, ct);
        var daily = await TryTencentDailyAsync(http, logger, code, ct);

        if (light is null && daily.Count == 0)
            return null;

        var lastDaily = daily.Count > 0 ? daily[^1] : null;
        var last = light?.Last
                   ?? (lastDaily is not null ? lastDaily.Close : 0m);
        if (last <= 0) return null;

        var pre = light?.PreClose is > 0
            ? light.PreClose.Value
            : (daily.Count > 1 ? daily[^2].Close : lastDaily?.Close ?? last);
        var open = lastDaily?.Open ?? last;
        var high = lastDaily?.High ?? last;
        var low = lastDaily?.Low ?? last;
        var change = last - pre;
        var changePct = pre > 0 ? change / pre * 100m : light?.ChangePercent ?? 0m;
        var displayName = !string.IsNullOrWhiteSpace(light?.Name) ? light!.Name : (name ?? symbol);
        var source = daily.Count > 0
            ? "数据来源：腾讯/新浪公开行情备用（东财不可用；含日K）。仅供模拟盘研究。"
            : "数据来源：腾讯/新浪公开行情备用（东财不可用；无日K降级）。仅供模拟盘研究。";

        var board = new QuoteBoard(
            symbol,
            displayName,
            Round4(last),
            Round4(open),
            Round4(high),
            Round4(low),
            Round4(pre),
            Round4(change),
            Round2(changePct),
            lastDaily?.Volume ?? 0,
            Round2(lastDaily?.Amount ?? 0),
            0,
            DateTime.Now);

        return (board, daily, source);
    }

    private static async Task<LightStockQuote?> TryTencentQuoteAsync(
        HttpClient http, ILogger logger, string symbol, string? name, CancellationToken ct)
    {
        try
        {
            var code = ToMarketCode(symbol);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://qt.gtimg.cn/q={code}");
            req.Headers.TryAddWithoutValidation("Referer", "https://finance.qq.com/");
            using var resp = await http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            var start = text.IndexOf('"');
            var end = text.LastIndexOf('"');
            if (start < 0 || end <= start) return null;
            var parts = text[(start + 1)..end].Split('~');
            // 1=名 2=码 3=现价 4=昨收 5=今开 33=最高 34=最低（字段随版本略有偏移，优先 3/4）
            if (parts.Length < 5) return null;
            var last = ParseDec(parts[3]);
            var pre = ParseDec(parts[4]);
            if (last <= 0) last = pre;
            if (last <= 0) return null;
            var nm = string.IsNullOrWhiteSpace(parts[1]) ? (name ?? symbol) : parts[1];
            var changePct = pre > 0 ? (last - pre) / pre * 100m : 0m;
            return new LightStockQuote
            {
                Symbol = symbol,
                Name = nm,
                Last = Round4(last),
                ChangePercent = Round2(changePct),
                PreClose = pre > 0 ? Round4(pre) : null,
                AsOf = DateTime.Now,
                IsSimulated = false
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Tencent quote fallback failed for {Symbol}", symbol);
            return null;
        }
    }

    private static async Task<LightStockQuote?> TrySinaQuoteAsync(
        HttpClient http, ILogger logger, string symbol, string? name, CancellationToken ct)
    {
        try
        {
            var code = ToMarketCode(symbol);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://hq.sinajs.cn/list={code}");
            req.Headers.TryAddWithoutValidation("Referer", "https://finance.sina.com.cn/");
            using var resp = await http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            // 新浪偶发 GBK；优先按声明编码，否则 UTF-8 / GB18030 兜底
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            var text = DecodeSina(bytes, resp.Content.Headers.ContentType?.CharSet);
            var m = SinaLine.Match(text);
            if (!m.Success) return null;
            var parts = m.Groups["body"].Value.Split(',');
            // 0名 1开 2昨收 3现价 4高 5低
            if (parts.Length < 4) return null;
            var open = ParseDec(parts[1]);
            var pre = ParseDec(parts[2]);
            var last = ParseDec(parts[3]);
            if (last <= 0) last = pre > 0 ? pre : open;
            if (last <= 0) return null;
            var nm = string.IsNullOrWhiteSpace(parts[0]) ? (name ?? symbol) : parts[0];
            var changePct = pre > 0 ? (last - pre) / pre * 100m : 0m;
            return new LightStockQuote
            {
                Symbol = symbol,
                Name = nm,
                Last = Round4(last),
                ChangePercent = Round2(changePct),
                PreClose = pre > 0 ? Round4(pre) : null,
                AsOf = DateTime.Now,
                IsSimulated = false
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Sina quote fallback failed for {Symbol}", symbol);
            return null;
        }
    }

    private static async Task<IReadOnlyList<DailyBar>> TryTencentDailyAsync(
        HttpClient http, ILogger logger, string marketCode, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var url =
                $"https://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param={marketCode},day,,,120,qfq";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://finance.qq.com/");
            using var resp = await http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return Array.Empty<DailyBar>();
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty(marketCode, out var stock)
                || stock.ValueKind != JsonValueKind.Object)
                return Array.Empty<DailyBar>();

            JsonElement barsEl;
            if (stock.TryGetProperty("qfqday", out var qfq) && qfq.ValueKind == JsonValueKind.Array)
                barsEl = qfq;
            else if (stock.TryGetProperty("day", out var day) && day.ValueKind == JsonValueKind.Array)
                barsEl = day;
            else
                return Array.Empty<DailyBar>();

            var list = new List<DailyBar>();
            foreach (var row in barsEl.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 5) continue;
                var dateStr = row[0].GetString();
                if (!DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;
                if (date.Date > DateTime.Today) continue;
                list.Add(new DailyBar
                {
                    Date = date.Date,
                    Open = GetArrDec(row, 1),
                    Close = GetArrDec(row, 2),
                    High = GetArrDec(row, 3),
                    Low = GetArrDec(row, 4),
                    Volume = (long)GetArrDec(row, 5),
                    Amount = 0
                });
            }

            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Tencent daily fallback failed for {Code}", marketCode);
            return Array.Empty<DailyBar>();
        }
    }

    private static string DecodeSina(byte[] bytes, string? charset)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
                return Encoding.GetEncoding(charset).GetString(bytes);
        }
        catch { /* ignore */ }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static decimal GetArrDec(JsonElement arr, int i)
    {
        if (i >= arr.GetArrayLength()) return 0;
        var el = arr[i];
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : (decimal)el.GetDouble(),
            JsonValueKind.String => ParseDec(el.GetString() ?? "0"),
            _ => 0
        };
    }

    private static decimal ParseDec(string s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static decimal Round4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    internal readonly record struct QuoteBoard(
        string Symbol,
        string Name,
        decimal Last,
        decimal Open,
        decimal High,
        decimal Low,
        decimal PreClose,
        decimal Change,
        decimal ChangePercent,
        long Volume,
        decimal Amount,
        decimal TurnoverRate,
        DateTime AsOf);
}
