using System.Collections.Concurrent;
using StockSim.Domain.MarketData;

namespace StockSim.Domain.Strategies;

/// <summary>盘中会话：开盘价、近似 VWAP、短窗波动与量比。</summary>
public sealed class IntradaySessionTracker
{
    private readonly ConcurrentDictionary<string, SessionStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    public SessionSnapshot Update(QuoteTick quote, DateTime now)
    {
        var key = quote.Symbol;
        var stats = _stats.GetOrAdd(key, _ => new SessionStats(now.Date));
        if (stats.TradeDate != now.Date)
        {
            stats = new SessionStats(now.Date);
            _stats[key] = stats;
        }

        stats.Observe(quote, now);
        return stats.Snapshot(quote);
    }

    public SessionSnapshot? TryGet(string symbol)
        => _stats.TryGetValue(symbol, out var s) ? s.LastSnapshot : null;

    public decimal ResolveAnchor(string anchorMode, SessionSnapshot snap)
    {
        return anchorMode.ToLowerInvariant() switch
        {
            "open" => snap.Open > 0 ? snap.Open : snap.Last,
            "vwap" or "session_vwap" => snap.Vwap > 0 ? snap.Vwap : (snap.Open > 0 ? snap.Open : snap.Last),
            _ => snap.Vwap > 0 ? snap.Vwap : snap.Open
        };
    }

    private sealed class SessionStats
    {
        public DateTime TradeDate { get; }
        private decimal _open;
        private decimal _vwapNum;
        private decimal _vwapDen;
        private long _lastVolume;
        private readonly Queue<decimal> _returns = new();
        private decimal _prevPrice;
        private readonly Queue<(DateTime t, long vol)> _volWindow = new();
        public SessionSnapshot? LastSnapshot { get; private set; }

        public SessionStats(DateTime tradeDate) => TradeDate = tradeDate.Date;

        public void Observe(QuoteTick quote, DateTime now)
        {
            if (_open <= 0) _open = quote.LastPrice;
            var volDelta = quote.Volume > _lastVolume ? quote.Volume - _lastVolume : Math.Max(quote.Volume, 1);
            _lastVolume = Math.Max(_lastVolume, quote.Volume);
            if (volDelta <= 0) volDelta = 1;

            _vwapNum += quote.LastPrice * volDelta;
            _vwapDen += volDelta;

            if (_prevPrice > 0)
            {
                var ret = (quote.LastPrice - _prevPrice) / _prevPrice * 100m;
                _returns.Enqueue(ret);
                while (_returns.Count > 30) _returns.Dequeue();
            }

            _prevPrice = quote.LastPrice;
            _volWindow.Enqueue((now, volDelta));
            while (_volWindow.Count > 0 && (now - _volWindow.Peek().t).TotalMinutes > 30)
                _volWindow.Dequeue();

            LastSnapshot = Snapshot(quote);
        }

        public SessionSnapshot Snapshot(QuoteTick quote)
        {
            var vwap = _vwapDen > 0 ? _vwapNum / _vwapDen : quote.LastPrice;
            var vol = RealizedVol();
            var ratio = VolumeRatio();
            return new SessionSnapshot
            {
                Symbol = quote.Symbol,
                Open = _open > 0 ? _open : quote.LastPrice,
                Vwap = vwap,
                Last = quote.LastPrice,
                RealizedVolPct = vol,
                VolumeRatio = ratio,
                AsOf = quote.Timestamp == default ? DateTime.Now : quote.Timestamp
            };
        }

        private decimal RealizedVol()
        {
            if (_returns.Count < 2) return 0.5m;
            var arr = _returns.ToArray();
            var mean = arr.Average();
            var varSum = arr.Sum(x => (x - mean) * (x - mean));
            var std = (decimal)Math.Sqrt((double)(varSum / arr.Length));
            return Math.Max(0.1m, std);
        }

        private decimal VolumeRatio()
        {
            if (_volWindow.Count < 4) return 1m;
            var arr = _volWindow.Select(x => (decimal)x.vol).ToArray();
            var recent = arr.TakeLast(Math.Min(5, arr.Length)).Average();
            var baseline = arr.Average();
            if (baseline <= 0) return 1m;
            return Math.Clamp(recent / baseline, 0.2m, 5m);
        }
    }
}

public sealed class SessionSnapshot
{
    public required string Symbol { get; init; }
    public decimal Open { get; init; }
    public decimal Vwap { get; init; }
    public decimal Last { get; init; }
    public decimal RealizedVolPct { get; init; }
    public decimal VolumeRatio { get; init; }
    public DateTime AsOf { get; init; }
}
