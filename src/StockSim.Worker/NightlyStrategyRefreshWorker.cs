using StockSim.Application.Services;

namespace StockSim.Worker;

/// <summary>
/// 每天约 22:00（可配置）将池内策略刷新任务写入统一 AI 队列（不阻塞、不直接调 Agent）。
/// </summary>
public sealed class NightlyStrategyRefreshWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NightlyStrategyRefreshWorker> _logger;
    private readonly TimeSpan _runAt;
    private readonly string _stampPath;
    private DateTime _lastSuccessDate;
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    public NightlyStrategyRefreshWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<NightlyStrategyRefreshWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var hour = configuration.GetValue("Worker:NightlyStrategyRefreshHour", 22);
        var minute = configuration.GetValue("Worker:NightlyStrategyRefreshMinute", 0);
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        _runAt = new TimeSpan(hour, minute, 0);

        var workDir = configuration.GetValue<string>("AI:WorkDirectory")
                      ?? Path.Combine(AppContext.BaseDirectory, "data", "ai");
        Directory.CreateDirectory(workDir);
        _stampPath = Path.Combine(workDir, "nightly_strategy_refresh.stamp");
        _lastSuccessDate = ReadStamp();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Nightly strategy refresh worker started. RunAt={RunAt} LastSuccess={LastSuccess}",
            _runAt, _lastSuccessDate == DateTime.MinValue ? "(never)" : _lastSuccessDate.ToString("yyyy-MM-dd"));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                if (ShouldRun(now))
                {
                    if (_lastAttemptUtc != DateTime.MinValue
                        && DateTime.UtcNow - _lastAttemptUtc < TimeSpan.FromMinutes(15)
                        && _lastSuccessDate.Date < now.Date)
                    {
                        _logger.LogDebug("Nightly enqueue due but in backoff");
                    }
                    else
                    {
                        await RunOnceAsync(now, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nightly strategy refresh loop error");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private bool ShouldRun(DateTime now)
    {
        if (_lastSuccessDate.Date >= now.Date)
            return false;
        if (now.TimeOfDay >= _runAt)
            return true;
        if (_lastSuccessDate.Date < now.Date.AddDays(-1))
            return true;
        return false;
    }

    private async Task RunOnceAsync(DateTime now, CancellationToken stoppingToken)
    {
        _lastAttemptUtc = DateTime.UtcNow;
        _logger.LogInformation("Nightly strategy refresh enqueue starting at {Now}", now);

        using var scope = _scopeFactory.CreateScope();
        var perStock = scope.ServiceProvider.GetRequiredService<PerStockStrategyService>();
        var queue = scope.ServiceProvider.GetRequiredService<AiQueueAppService>();

        var ensured = await perStock.EnsureMissingStrategiesAsync(stoppingToken);
        var enqueued = await queue.EnqueueStrategyRefreshAllAsync(nightly: true, stoppingToken);

        WriteStamp(now.Date);
        _lastSuccessDate = now.Date;

        _logger.LogInformation(
            "Nightly strategy refresh enqueued. Ensured={EnsuredOk}/{EnsuredFail} Queued={Queued}",
            ensured.Ok, ensured.Fail, enqueued);
    }

    private DateTime ReadStamp()
    {
        try
        {
            if (!File.Exists(_stampPath)) return DateTime.MinValue;
            var text = File.ReadAllText(_stampPath).Trim();
            if (DateTime.TryParse(text, out var d)) return d.Date;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read nightly stamp");
        }

        return DateTime.MinValue;
    }

    private void WriteStamp(DateTime date)
    {
        try
        {
            File.WriteAllText(_stampPath, date.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write nightly stamp");
        }
    }
}
