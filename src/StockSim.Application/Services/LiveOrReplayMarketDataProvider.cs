using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StockSim.Domain.MarketData;

namespace StockSim.Application.Services;

/// <summary>
/// 优先用东财轻量报价（与列表同源）；失败时回退 Replay CSV。
/// Worker 建底仓/做 T 依赖此源，不能仅靠演示 CSV。
/// </summary>
public sealed class LiveOrReplayMarketDataProvider : IMarketDataProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMarketDataProvider _replay;
    private readonly ILogger<LiveOrReplayMarketDataProvider> _logger;

    public LiveOrReplayMarketDataProvider(
        IServiceScopeFactory scopeFactory,
        IMarketDataProvider replay,
        ILogger<LiveOrReplayMarketDataProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _replay = replay;
        _logger = logger;
    }

    public string Name => $"Live+{_replay.Name}";

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => _replay.ConnectAsync(cancellationToken);

    public async Task<QuoteTick?> GetLastAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var detail = scope.ServiceProvider.GetService<IStockDetailProvider>();
            if (detail is not null)
            {
                var light = await detail.GetLightQuoteAsync(symbol, null, cancellationToken);
                if (light is not null && light.Last > 0)
                {
                    return new QuoteTick
                    {
                        Symbol = light.Symbol,
                        LastPrice = light.Last,
                        Bid = light.Last,
                        Ask = light.Last,
                        Volume = 0,
                        ChangePercent = light.ChangePercent,
                        IsSuspended = false,
                        IsLimitUp = light.ChangePercent >= 9.5m,
                        IsLimitDown = light.ChangePercent <= -9.5m,
                        Timestamp = light.AsOf == default ? DateTime.Now : light.AsOf
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live quote failed for {Symbol}, fallback replay", symbol);
        }

        return await _replay.GetLastAsync(symbol, cancellationToken);
    }

    public async Task<IReadOnlyList<QuoteTick>> GetBatchAsync(
        IEnumerable<string> symbols,
        CancellationToken cancellationToken = default)
    {
        var list = new List<QuoteTick>();
        foreach (var symbol in symbols)
        {
            var tick = await GetLastAsync(symbol, cancellationToken);
            if (tick is not null) list.Add(tick);
        }

        return list;
    }
}
