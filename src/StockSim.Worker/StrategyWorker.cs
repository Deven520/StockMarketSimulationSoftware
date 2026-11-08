using StockSim.Application.Services;

namespace StockSim.Worker;

public sealed class StrategyWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StrategyWorker> _logger;
    private readonly TimeSpan _interval;

    public StrategyWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<StrategyWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var seconds = configuration.GetValue("Worker:StrategyIntervalSeconds", 3);
        _interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Strategy worker started. Interval={Interval}s", _interval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<StrategyEngineService>();
                var filled = await engine.EvaluateOnceAsync(stoppingToken);
                if (filled > 0)
                {
                    _logger.LogInformation("Strategy filled {Count} trades", filled);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Strategy evaluation failed");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
