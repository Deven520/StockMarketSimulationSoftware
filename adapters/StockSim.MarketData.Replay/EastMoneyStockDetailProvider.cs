using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StockSim.Domain.MarketData;

namespace StockSim.MarketData.Replay;

/// <summary>
/// 东方财富公开行情接口适配（日K / 分时 / 报价估值 / 资金流）。
/// 注意：第三方网页接口可能变更或受限，生产环境请改用合规行情源。
/// </summary>
public sealed class EastMoneyStockDetailProvider : IStockDetailProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<EastMoneyStockDetailProvider> _logger;
    private readonly SimulatedStockDetailProvider _fallback = new();

    public EastMoneyStockDetailProvider(HttpClient http, ILogger<EastMoneyStockDetailProvider> logger)
    {
        _http = http;
        _logger = logger;
        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri("https://push2.eastmoney.com/");
        }

        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
    }

    public async Task<LightStockQuote?> GetLightQuoteAsync(string symbol, string? name = null, CancellationToken ct = default)
    {
        symbol = NormalizeSymbol(symbol);
        if (symbol.Length != 6 || !symbol.All(char.IsDigit))
            return null;

        try
        {
            var quote = await FetchQuoteAsync(ToSecId(symbol), ct);
            if (quote is null) return await _fallback.GetLightQuoteAsync(symbol, name, ct);

            var last = quote.Last;
            var preClose = quote.PreClose;
            var changePct = preClose == 0 ? 0 : (last - preClose) / preClose * 100m;
            return new LightStockQuote
            {
                Symbol = symbol,
                Name = string.IsNullOrWhiteSpace(quote.Name) ? (name ?? symbol) : quote.Name,
                Last = Round4(last),
                ChangePercent = Round2(changePct),
                PeTtm = quote.Fundamentals.PeTtm,
                Pb = quote.Fundamentals.Pb,
                Industry = quote.Fundamentals.Industry,
                AsOf = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Light quote failed for {Symbol}", symbol);
            return await _fallback.GetLightQuoteAsync(symbol, name, ct);
        }
    }

    public async Task<IReadOnlyList<LightStockQuote>> GetLightQuotesAsync(
        IReadOnlyList<string> symbols,
        CancellationToken ct = default)
    {
        var normalized = symbols
            .Select(NormalizeSymbol)
            .Where(s => s.Length == 6 && s.All(char.IsDigit))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalized.Count == 0) return Array.Empty<LightStockQuote>();

        var result = new List<LightStockQuote>(normalized.Count);
        const int chunkSize = 50;
        for (var i = 0; i < normalized.Count; i += chunkSize)
        {
            var chunk = normalized.Skip(i).Take(chunkSize).ToList();
            var batch = await FetchQuotesBatchAsync(chunk, ct);
            var got = new HashSet<string>(batch.Select(x => x.Symbol), StringComparer.Ordinal);
            result.AddRange(batch);

            // 批量未命中的回退单只（并行）
            var miss = chunk.Where(s => !got.Contains(s)).ToList();
            if (miss.Count == 0) continue;

            var singles = await Task.WhenAll(miss.Select(s => GetLightQuoteAsync(s, null, ct)));
            foreach (var q in singles)
            {
                if (q is not null) result.Add(q);
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<LightStockQuote>> FetchQuotesBatchAsync(
        IReadOnlyList<string> symbols,
        CancellationToken ct)
    {
        try
        {
            var secids = string.Join(",", symbols.Select(ToSecId));
            var url =
                "https://push2.eastmoney.com/api/qt/ulist.np/get?fltt=2&invt=2" +
                $"&secids={Uri.EscapeDataString(secids)}" +
                "&fields=f12,f14,f2,f3,f162,f167";

            using var doc = await GetJsonAsync(url, ct);
            var data = doc?.RootElement.GetPropertyOrNull("data");
            var diff = data?.GetPropertyOrNull("diff");
            if (diff is null || diff.Value.ValueKind != JsonValueKind.Array)
                return Array.Empty<LightStockQuote>();

            var list = new List<LightStockQuote>();
            foreach (var row in diff.Value.EnumerateArray())
            {
                var code = GetString(row, "f12");
                if (string.IsNullOrWhiteSpace(code)) continue;
                var symbol = NormalizeSymbol(code);
                if (symbol.Length != 6) continue;
                var name = GetString(row, "f14") ?? symbol;
                var last = GetDecimal(row, "f2");
                var changePct = GetDecimal(row, "f3");
                list.Add(new LightStockQuote
                {
                    Symbol = symbol,
                    Name = name,
                    Last = Round4(last),
                    ChangePercent = Round2(changePct),
                    PeTtm = NullIfZero(GetDecimal(row, "f162")),
                    Pb = NullIfZero(GetDecimal(row, "f167")),
                    Industry = null,
                    AsOf = DateTime.Now
                });
            }

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch light quote failed for {Count} symbols", symbols.Count);
            return Array.Empty<LightStockQuote>();
        }
    }

    public async Task<StockDetailBundle?> GetDetailAsync(string symbol, string? name = null, CancellationToken ct = default)
    {
        symbol = NormalizeSymbol(symbol);
        if (symbol.Length != 6 || !symbol.All(char.IsDigit))
        {
            return await _fallback.GetDetailAsync(symbol, name, ct);
        }

        try
        {
            var secid = ToSecId(symbol);
            var quoteTask = FetchQuoteAsync(secid, ct);
            var dailyTask = FetchDailyBarsAsync(secid, ct);
            var minuteTask = FetchMinuteBarsAsync(secid, ct);
            var flowTask = FetchFundFlowAsync(secid, ct);

            await Task.WhenAll(quoteTask, dailyTask, minuteTask, flowTask);

            var quote = await quoteTask;
            var daily = await dailyTask;
            var minutes = await minuteTask;
            var flow = await flowTask;

            if (daily.Count == 0 && quote is null)
            {
                _logger.LogWarning("EastMoney empty for {Symbol}, fallback simulated", symbol);
                var fb = await _fallback.GetDetailAsync(symbol, name, ct);
                if (fb is not null)
                {
                    fb = CloneWithNote(fb, "东方财富无数据，已回退模拟行情（日期已限制为不超过今天）。");
                }

                return fb;
            }

            var lastDaily = daily.Count > 0 ? daily[^1] : null;
            var lastMinute = minutes.Count > 0 ? minutes[^1] : null;
            var displayName = quote?.Name ?? name ?? symbol;

            var last = quote?.Last
                       ?? lastMinute?.Close
                       ?? lastDaily?.Close
                       ?? 0;
            var open = quote?.Open ?? lastDaily?.Open ?? last;
            var high = quote?.High ?? lastDaily?.High ?? last;
            var low = quote?.Low ?? lastDaily?.Low ?? last;
            var preClose = quote?.PreClose
                           ?? (daily.Count > 1 ? daily[^2].Close : lastDaily?.Open ?? last);
            var change = last - preClose;
            var changePct = preClose == 0 ? 0 : change / preClose * 100m;

            var board = new StockQuoteBoard
            {
                Symbol = symbol,
                Name = displayName,
                Last = Round4(last),
                Open = Round4(open),
                High = Round4(high),
                Low = Round4(low),
                PreClose = Round4(preClose),
                Change = Round4(change),
                ChangePercent = Round2(changePct),
                Volume = quote?.Volume ?? lastDaily?.Volume ?? 0,
                Amount = Round2(quote?.Amount ?? lastDaily?.Amount ?? 0),
                TurnoverRate = Round2(quote?.TurnoverRate ?? 0),
                AsOf = DateTime.Now
            };

            return new StockDetailBundle
            {
                Board = board,
                Fundamentals = quote?.Fundamentals ?? new StockFundamentals(),
                FundFlow = flow ?? new FundFlowSnapshot { AsOf = DateTime.Now },
                DailyBars = daily,
                MinuteBars = minutes,
                DataSourceNote = "数据来源：东方财富公开接口（实时/延时视接口而定）。仅供模拟盘研究，请遵守数据源使用条款。"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EastMoney fetch failed for {Symbol}", symbol);
            var fb = await _fallback.GetDetailAsync(symbol, name, ct);
            return fb is null ? null : CloneWithNote(fb, $"真实行情获取失败：{ex.Message}；已回退模拟数据。");
        }
    }

    private async Task<QuoteSnap?> FetchQuoteAsync(string secid, CancellationToken ct)
    {
        // fltt=2 返回真实小数；字段含最新价/名称/PE/PB/市值等
        var url =
            $"https://push2.eastmoney.com/api/qt/stock/get?fltt=2&invt=2&secid={secid}" +
            "&fields=f43,f44,f45,f46,f47,f48,f57,f58,f60,f116,f117,f162,f167,f168,f169,f170";

        using var doc = await GetJsonAsync(url, ct);
        if (doc is null) return null;
        var data = doc.RootElement.GetPropertyOrNull("data");
        if (data is null || data.Value.ValueKind != JsonValueKind.Object) return null;
        var d = data.Value;

        return new QuoteSnap(
            GetString(d, "f58") ?? string.Empty,
            GetDecimal(d, "f43"),
            GetDecimal(d, "f46"),
            GetDecimal(d, "f44"),
            GetDecimal(d, "f45"),
            GetDecimal(d, "f60"),
            (long)GetDecimal(d, "f47"),
            GetDecimal(d, "f48"),
            GetDecimal(d, "f168"),
            new StockFundamentals
            {
                PeTtm = NullIfZero(GetDecimal(d, "f162")),
                Pb = NullIfZero(GetDecimal(d, "f167")),
                TotalMarketCapYi = ToYi(GetDecimal(d, "f116")),
                CircMarketCapYi = ToYi(GetDecimal(d, "f117")),
                Industry = null
            });
    }

    private async Task<IReadOnlyList<DailyBar>> FetchDailyBarsAsync(string secid, CancellationToken ct)
    {
        var end = DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var beg = DateTime.Today.AddYears(-1).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var url =
            "https://push2his.eastmoney.com/api/qt/stock/kline/get" +
            $"?secid={secid}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57" +
            $"&klt=101&fqt=1&beg={beg}&end={end}&lmt=260";

        using var doc = await GetJsonAsync(url, ct);
        var klines = doc?.RootElement.GetPropertyOrNull("data")?.GetPropertyOrNull("klines");
        if (klines is null || klines.Value.ValueKind != JsonValueKind.Array) return Array.Empty<DailyBar>();

        var list = new List<DailyBar>();
        foreach (var item in klines.Value.EnumerateArray())
        {
            var s = item.GetString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            var p = s.Split(',');
            if (p.Length < 7) continue;
            if (!DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            if (date.Date > DateTime.Today) continue;

            list.Add(new DailyBar
            {
                Date = date.Date,
                Open = ParseDec(p[1]),
                Close = ParseDec(p[2]),
                High = ParseDec(p[3]),
                Low = ParseDec(p[4]),
                Volume = (long)ParseDec(p[5]),
                Amount = ParseDec(p[6])
            });
        }

        return list;
    }

    public async Task<IReadOnlyList<MinuteBar>> GetIntradayAsync(string symbol, DateTime tradeDate, CancellationToken ct = default)
    {
        symbol = NormalizeSymbol(symbol);
        if (symbol.Length != 6 || !symbol.All(char.IsDigit))
        {
            return await _fallback.GetIntradayAsync(symbol, tradeDate, ct);
        }

        try
        {
            var secid = ToSecId(symbol);
            var date = tradeDate.Date;
            // 当日优先分时；历史日用 5 分钟 K（更稳定）
            if (date == DateTime.Today)
            {
                var today = await FetchMinuteBarsAsync(secid, ct);
                if (today.Count > 0) return today;
            }

            var bars = await FetchIntradayKlineAsync(secid, date, klt: 5, ct);
            if (bars.Count == 0)
            {
                bars = await FetchIntradayKlineAsync(secid, date, klt: 1, ct);
            }

            if (bars.Count == 0)
            {
                return await _fallback.GetIntradayAsync(symbol, tradeDate, ct);
            }

            return bars;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EastMoney intraday failed for {Symbol} {Date}", symbol, tradeDate.Date);
            return await _fallback.GetIntradayAsync(symbol, tradeDate, ct);
        }
    }

    private async Task<IReadOnlyList<MinuteBar>> FetchIntradayKlineAsync(
        string secid,
        DateTime date,
        int klt,
        CancellationToken ct)
    {
        var d = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var url =
            "https://push2his.eastmoney.com/api/qt/stock/kline/get" +
            $"?secid={secid}&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57" +
            $"&klt={klt}&fqt=1&beg={d}&end={d}&lmt=1000";

        using var doc = await GetJsonAsync(url, ct);
        var klines = doc?.RootElement.GetPropertyOrNull("data")?.GetPropertyOrNull("klines");
        if (klines is null || klines.Value.ValueKind != JsonValueKind.Array) return Array.Empty<MinuteBar>();

        var list = new List<MinuteBar>();
        foreach (var item in klines.Value.EnumerateArray())
        {
            var s = item.GetString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            var p = s.Split(',');
            if (p.Length < 6) continue;
            // f51 可能是 "2026-11-02 09:35" 或 "2026-11-02"
            if (!DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            list.Add(new MinuteBar
            {
                Time = time,
                Open = ParseDec(p[1]),
                Close = ParseDec(p[2]),
                High = ParseDec(p[3]),
                Low = ParseDec(p[4]),
                Volume = (long)ParseDec(p[5])
            });
        }

        return list;
    }

    private async Task<IReadOnlyList<MinuteBar>> FetchMinuteBarsAsync(string secid, CancellationToken ct)
    {
        var url =
            "https://push2.eastmoney.com/api/qt/stock/trends2/get" +
            $"?secid={secid}&fields1=f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13" +
            "&fields2=f51,f52,f53,f54,f55,f56,f57,f58&iscr=0&ndays=1&iscca=0";

        using var doc = await GetJsonAsync(url, ct);
        var trends = doc?.RootElement.GetPropertyOrNull("data")?.GetPropertyOrNull("trends");
        if (trends is null || trends.Value.ValueKind != JsonValueKind.Array) return Array.Empty<MinuteBar>();

        var list = new List<MinuteBar>();
        decimal? prev = null;
        foreach (var item in trends.Value.EnumerateArray())
        {
            var s = item.GetString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            var p = s.Split(',');
            // 常见：yyyy-MM-dd HH:mm,价格,均价,成交量,...
            if (p.Length < 4) continue;
            if (!DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            var close = ParseDec(p[1]);
            var open = prev ?? close;
            var vol = (long)ParseDec(p[3]);
            list.Add(new MinuteBar
            {
                Time = time,
                Open = open,
                High = Math.Max(open, close),
                Low = Math.Min(open, close),
                Close = close,
                Volume = vol
            });
            prev = close;
        }

        return list;
    }

    private async Task<FundFlowSnapshot?> FetchFundFlowAsync(string secid, CancellationToken ct)
    {
        // 当日分时资金流向最后一根近似作为快照
        var url =
            "https://push2.eastmoney.com/api/qt/stock/fflow/kline/get" +
            $"?lmt=0&klt=1&secid={secid}&fields1=f1,f2,f3,f7&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61,f62,f63,f64,f65";

        using var doc = await GetJsonAsync(url, ct);
        var klines = doc?.RootElement.GetPropertyOrNull("data")?.GetPropertyOrNull("klines");
        if (klines is null || klines.Value.ValueKind != JsonValueKind.Array || klines.Value.GetArrayLength() == 0)
        {
            return null;
        }

        var last = klines.Value[klines.Value.GetArrayLength() - 1].GetString();
        if (string.IsNullOrWhiteSpace(last)) return null;
        var p = last.Split(',');
        // f51时间, f52主力净流入, f53小单, f54中单, f55大单, f56超大单 ...（单位多为元）
        if (p.Length < 6) return null;

        var mainNet = ParseDec(p[1]);
        var small = ParseDec(p[2]);
        var mid = ParseDec(p[3]);
        var large = ParseDec(p[4]);
        var superLarge = ParseDec(p[5]);
        var mainIn = Math.Max(mainNet, 0);
        var mainOut = Math.Max(-mainNet, 0);

        return new FundFlowSnapshot
        {
            MainNetInflow = Round2(mainNet),
            MainInflow = Round2(mainIn),
            MainOutflow = Round2(mainOut),
            RetailNetInflow = Round2(small),
            SmallNet = Round2(small),
            MediumNet = Round2(mid),
            LargeNet = Round2(large),
            SuperLargeNet = Round2(superLarge),
            AsOf = DateTime.Now
        };
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("HTTP {Code} for {Url}", (int)resp.StatusCode, url);
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string ToSecId(string symbol)
    {
        // 6开头 / 5开头(基金等少见) 沪市；0/3 深市；北交所 4/8 用 0 尝试
        if (symbol.StartsWith('6') || symbol.StartsWith('5') || symbol.StartsWith('9'))
            return $"1.{symbol}";
        return $"0.{symbol}";
    }

    private static string NormalizeSymbol(string symbol) =>
        new string((symbol ?? string.Empty).Where(char.IsDigit).ToArray());

    private static decimal ParseDec(string s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static decimal GetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return 0;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDecimal(out var d) ? d : (decimal)p.GetDouble(),
            JsonValueKind.String => ParseDec(p.GetString() ?? "0"),
            _ => 0
        };
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static decimal? NullIfZero(decimal v) => v == 0 ? null : v;
    private static decimal? ToYi(decimal v) => v == 0 ? null : Round2(v / 100000000m);
    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static decimal Round4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

    private static StockDetailBundle CloneWithNote(StockDetailBundle src, string note) =>
        new()
        {
            Board = src.Board,
            Fundamentals = src.Fundamentals,
            FundFlow = src.FundFlow,
            DailyBars = src.DailyBars,
            MinuteBars = src.MinuteBars,
            DataSourceNote = note,
            IsSimulated = src.IsSimulated
        };

    private sealed record QuoteSnap(
        string Name,
        decimal Last,
        decimal Open,
        decimal High,
        decimal Low,
        decimal PreClose,
        long Volume,
        decimal Amount,
        decimal TurnoverRate,
        StockFundamentals Fundamentals);
}

internal static class JsonElementExt
{
    public static JsonElement? GetPropertyOrNull(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty(name, out var p) ? p : null;
    }
}
