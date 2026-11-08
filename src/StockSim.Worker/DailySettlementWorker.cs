using StockSim.Application.Services;

namespace StockSim.Worker;

public sealed class DailySettlementWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailySettlementWorker> _logger;
    private DateTime _lastSettled = DateTime.MinValue;

    public DailySettlementWorker(IServiceScopeFactory scopeFactory, ILogger<DailySettlementWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            // 演示：每天 15:05 后结算一次；开发期也允许手动页面结算
            if (now.TimeOfDay >= new TimeSpan(15, 5, 0) && _lastSettled.Date < now.Date)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var pnl = scope.ServiceProvider.GetRequiredService<PnLAppService>();
                    await pnl.SettleAsync(now.Date, stoppingToken);
                    var goals = scope.ServiceProvider.GetRequiredService<InvestmentGoalAppService>();
                    await goals.SettleAllActiveAsync(now.Date, stoppingToken);
                    _lastSettled = now.Date;
                    _logger.LogInformation("Daily settlement + goal progress done for {Date}", now.Date);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Daily settlement failed");
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
