using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StockSim.Domain.MarketData;

namespace StockSim.MarketData.Replay;

/// <summary>
/// 确定性模拟个股详情（日K/分K/资金流/估值），便于面板演示。
/// 可后续替换为真实行情适配器；标注 DataSourceNote。
/// </summary>
public sealed class SimulatedStockDetailProvider : IStockDetailProvider
{
    public Task<StockDetailBundle?> GetDetailAsync(string symbol, string? name = null, CancellationToken ct = default)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(symbol)) return Task.FromResult<StockDetailBundle?>(null);

        var seed = StableSeed(symbol);
        var rng = new Random(seed);
        var basePrice = 8m + (seed % 2000) / 10m;
        var nameResolved = string.IsNullOrWhiteSpace(name) ? $"模拟-{symbol}" : name!;

        var daily = BuildDailyBars(basePrice, rng, 250);
        var lastDaily = daily[^1];
        var minute = BuildMinuteBars(lastDaily.Open, lastDaily.Close, rng);
        var lastMinute = minute.Count > 0 ? minute[^1].Close : lastDaily.Close;
        var preClose = daily.Count > 1 ? daily[^2].Close : lastDaily.Open;
        var change = lastMinute - preClose;
        var changePct = preClose == 0 ? 0 : change / preClose * 100m;

        var board = new StockQuoteBoard
        {
            Symbol = symbol,
            Name = nameResolved,
            Last = Round4(lastMinute),
            Open = lastDaily.Open,
            High = Math.Max(lastDaily.High, minute.Max(m => m.High)),
            Low = Math.Min(lastDaily.Low, minute.Min(m => m.Low)),
            PreClose = preClose,
            Change = Round4(change),
            ChangePercent = Round2(changePct),
            Volume = lastDaily.Volume + minute.Sum(m => m.Volume),
            Amount = Round2(lastDaily.Amount),
            TurnoverRate = Round2(0.5m + (decimal)rng.NextDouble() * 4),
            AsOf = DateTime.Now
        };

        var mainIn = (decimal)(rng.NextDouble() * 3 + 0.2) * 1_0000_0000m / 100;
        var mainOut = (decimal)(rng.NextDouble() * 3 + 0.2) * 1_0000_0000m / 100;
        var fund = new FundFlowSnapshot
        {
            MainInflow = Round2(mainIn),
            MainOutflow = Round2(mainOut),
            MainNetInflow = Round2(mainIn - mainOut),
            RetailNetInflow = Round2((decimal)(rng.NextDouble() - 0.5) * 8000_0000m / 100),
            SuperLargeNet = Round2((decimal)(rng.NextDouble() - 0.5) * 5000_0000m / 100),
            LargeNet = Round2((decimal)(rng.NextDouble() - 0.5) * 4000_0000m / 100),
            MediumNet = Round2((decimal)(rng.NextDouble() - 0.5) * 3000_0000m / 100),
            SmallNet = Round2((decimal)(rng.NextDouble() - 0.5) * 2000_0000m / 100),
            AsOf = DateTime.Now
        };

        var totalCap = Round2(50 + (decimal)rng.NextDouble() * 2000);
        var circCap = Round2(Math.Min(totalCap, 30 + (decimal)rng.NextDouble() * totalCap));
        var fundamentals = new StockFundamentals
        {
            PeTtm = Round2(8 + (decimal)rng.NextDouble() * 40),
            Pb = Round2(0.6m + (decimal)rng.NextDouble() * 4),
            PsTtm = Round2(0.5m + (decimal)rng.NextDouble() * 8),
            TotalMarketCapYi = totalCap,
            CircMarketCapYi = circCap,
            Roe = Round2((decimal)(rng.NextDouble() * 25 - 2)),
            Industry = PickIndustry(seed)
        };

        var bundle = new StockDetailBundle
        {
            Board = board,
            Fundamentals = fundamentals,
            FundFlow = fund,
            DailyBars = daily,
            MinuteBars = minute,
            DataSourceNote = "模拟行情数据（回退/演示）。日期已限制为不超过今天。",
            IsSimulated = true
        };
        return Task.FromResult<StockDetailBundle?>(bundle);
    }

    public Task<IReadOnlyList<MinuteBar>> GetIntradayAsync(string symbol, DateTime tradeDate, CancellationToken ct = default)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var seed = StableSeed(symbol + tradeDate.ToString("yyyyMMdd"));
        var rng = new Random(seed);
        var basePrice = 8m + (seed % 2000) / 10m;
        var open = basePrice * (1 + (decimal)(rng.NextDouble() - 0.5) * 0.02m);
        var closeHint = open * (1 + (decimal)(rng.NextDouble() - 0.5) * 0.03m);
        var bars = BuildMinuteBarsForDate(tradeDate.Date, open, closeHint, rng);
        return Task.FromResult<IReadOnlyList<MinuteBar>>(bars);
    }

    public async Task<LightStockQuote?> GetLightQuoteAsync(string symbol, string? name = null, CancellationToken ct = default)
    {
        var detail = await GetDetailAsync(symbol, name, ct);
        if (detail is null) return null;
        return new LightStockQuote
        {
            Symbol = detail.Board.Symbol,
            Name = detail.Board.Name,
            Last = detail.Board.Last,
            ChangePercent = detail.Board.ChangePercent,
            PeTtm = detail.Fundamentals.PeTtm,
            Pb = detail.Fundamentals.Pb,
            Industry = detail.Fundamentals.Industry,
            AsOf = detail.Board.AsOf,
            IsSimulated = true
        };
    }

    public async Task<IReadOnlyList<LightStockQuote>> GetLightQuotesAsync(
        IReadOnlyList<string> symbols,
        CancellationToken ct = default)
    {
        var list = new List<LightStockQuote>();
        foreach (var s in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var q = await GetLightQuoteAsync(s, null, ct);
            if (q is not null) list.Add(q);
        }

        return list;
    }

    private static List<MinuteBar> BuildMinuteBarsForDate(DateTime date, decimal dayOpen, decimal dayCloseHint, Random rng)
    {
        var list = new List<MinuteBar>();
        var sessions = new[]
        {
            (new TimeSpan(9, 30, 0), new TimeSpan(11, 30, 0)),
            (new TimeSpan(13, 0, 0), new TimeSpan(15, 0, 0))
        };
        var price = dayOpen;
        foreach (var (start, end) in sessions)
        {
            for (var t = start; t < end; t = t.Add(TimeSpan.FromMinutes(5)))
            {
                var drift = (dayCloseHint - dayOpen) / 48m;
                var close = price + drift + (decimal)(rng.NextDouble() - 0.5) * price * 0.002m;
                var open = price;
                list.Add(new MinuteBar
                {
                    Time = date.Add(t),
                    Open = Round4(open),
                    High = Round4(Math.Max(open, close) * (1 + (decimal)rng.NextDouble() * 0.001m)),
                    Low = Round4(Math.Min(open, close) * (1 - (decimal)rng.NextDouble() * 0.001m)),
                    Close = Round4(close),
                    Volume = 20_000 + rng.Next(0, 150_000)
                });
                price = close;
            }
        }

        return list;
    }

    private static List<DailyBar> BuildDailyBars(decimal start, Random rng, int days)
    {
        // 从今天往前取交易日，避免生成“未来日期”
        var list = new List<DailyBar>(days);
        var price = start;
        var day = DateTime.Today;
        while (list.Count < days)
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                var open = price * (1 + (decimal)(rng.NextDouble() - 0.5) * 0.01m);
                var close = open * (1 + (decimal)(rng.NextDouble() - 0.5) * 0.04m);
                var high = Math.Max(open, close) * (1 + (decimal)rng.NextDouble() * 0.015m);
                var low = Math.Min(open, close) * (1 - (decimal)rng.NextDouble() * 0.015m);
                var vol = 1_000_000L + rng.Next(0, 20_000_000);
                list.Add(new DailyBar
                {
                    Date = day.Date,
                    Open = Round4(open),
                    High = Round4(high),
                    Low = Round4(low),
                    Close = Round4(close),
                    Volume = vol,
                    Amount = Round2((open + close) / 2 * vol)
                });
                price = close;
            }

            day = day.AddDays(-1);
        }

        list.Reverse();
        return list;
    }

    private static List<MinuteBar> BuildMinuteBars(decimal dayOpen, decimal dayCloseHint, Random rng)
    {
        var list = new List<MinuteBar>();
        var sessions = new[]
        {
            (new TimeSpan(9, 30, 0), new TimeSpan(11, 30, 0)),
            (new TimeSpan(13, 0, 0), new TimeSpan(15, 0, 0))
        };

        var price = dayOpen;
        var now = DateTime.Now;
        var endCap = now.Date == DateTime.Today ? now.TimeOfDay : new TimeSpan(15, 0, 0);

        foreach (var (start, end) in sessions)
        {
            for (var t = start; t < end; t = t.Add(TimeSpan.FromMinutes(1)))
            {
                if (t > endCap && now.Date == DateTime.Today) break;
                var drift = (dayCloseHint - dayOpen) / 240m;
                var close = price + drift + (decimal)(rng.NextDouble() - 0.5) * price * 0.002m;
                var open = price;
                var high = Math.Max(open, close) * (1 + (decimal)rng.NextDouble() * 0.001m);
                var low = Math.Min(open, close) * (1 - (decimal)rng.NextDouble() * 0.001m);
                list.Add(new MinuteBar
                {
                    Time = DateTime.Today.Add(t),
                    Open = Round4(open),
                    High = Round4(high),
                    Low = Round4(low),
                    Close = Round4(close),
                    Volume = 10_000 + rng.Next(0, 200_000)
                });
                price = close;
            }
        }

        if (list.Count == 0)
        {
            // 非盘中也给全日分时骨架便于查看
            foreach (var (start, end) in sessions)
            {
                for (var t = start; t < end; t = t.Add(TimeSpan.FromMinutes(5)))
                {
                    var close = price * (1 + (decimal)(rng.NextDouble() - 0.5) * 0.003m);
                    list.Add(new MinuteBar
                    {
                        Time = DateTime.Today.Add(t),
                        Open = Round4(price),
                        High = Round4(Math.Max(price, close)),
                        Low = Round4(Math.Min(price, close)),
                        Close = Round4(close),
                        Volume = 50_000 + rng.Next(0, 100_000)
                    });
                    price = close;
                }
            }
        }

        return list;
    }

    private static int StableSeed(string symbol)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(symbol));
        return BitConverter.ToInt32(hash, 0) & 0x7fffffff;
    }

    private static string PickIndustry(int seed) =>
        (seed % 6) switch
        {
            0 => "银行",
            1 => "白酒",
            2 => "新能源",
            3 => "半导体",
            4 => "医药",
            _ => "消费"
        };

    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static decimal Round4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
}
