using System.Globalization;
using Microsoft.Extensions.Configuration;
using StockSim.Domain.MarketData;

namespace StockSim.MarketData.Replay;

public sealed class ReplayMarketDataProvider : IMarketDataProvider
{
    private readonly string _replayDirectory;
    private readonly Dictionary<string, Queue<QuoteTick>> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuoteTick> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ReplayMarketDataProvider(IConfiguration configuration)
    {
        _replayDirectory = configuration.GetValue<string>("MarketData:ReplayDirectory")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "replay"));
    }

    public string Name => "Replay";

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _queues.Clear();
            _last.Clear();
            if (!Directory.Exists(_replayDirectory))
            {
                return Task.CompletedTask;
            }

            foreach (var file in Directory.GetFiles(_replayDirectory, "*.csv"))
            {
                var symbol = Path.GetFileNameWithoutExtension(file).Split('_')[0];
                var ticks = LoadCsv(symbol, file);
                _queues[symbol] = new Queue<QuoteTick>(ticks);
                if (ticks.Count > 0)
                {
                    _last[symbol] = ticks[0];
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<QuoteTick?> GetLastAsync(string symbol, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_queues.TryGetValue(symbol, out var queue) && queue.Count > 0)
            {
                var tick = queue.Dequeue();
                _last[symbol] = tick;
                return Task.FromResult<QuoteTick?>(tick);
            }

            return Task.FromResult(_last.TryGetValue(symbol, out var last) ? last : null);
        }
    }

    public async Task<IReadOnlyList<QuoteTick>> GetBatchAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
    {
        var list = new List<QuoteTick>();
        foreach (var symbol in symbols)
        {
            var tick = await GetLastAsync(symbol, cancellationToken);
            if (tick is not null) list.Add(tick);
        }

        return list;
    }

    private static List<QuoteTick> LoadCsv(string symbol, string path)
    {
        var lines = File.ReadAllLines(path);
        var start = lines.Length > 0 && lines[0].Contains("Price", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var result = new List<QuoteTick>();
        for (var i = start; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length < 2) continue;
            if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var price)) continue;

            var ts = parts.Length > 0 && DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
                ? t
                : DateTime.UtcNow;

            var change = parts.Length > 2 && decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var c) ? c : 0m;
            var suspended = parts.Length > 3 && parts[3] == "1";
            var limitUp = parts.Length > 4 && parts[4] == "1";
            var limitDown = parts.Length > 5 && parts[5] == "1";

            result.Add(new QuoteTick
            {
                Symbol = symbol,
                LastPrice = price,
                Bid = price,
                Ask = price,
                ChangePercent = change,
                IsSuspended = suspended,
                IsLimitUp = limitUp,
                IsLimitDown = limitDown,
                Timestamp = ts
            });
        }

        return result;
    }
}
