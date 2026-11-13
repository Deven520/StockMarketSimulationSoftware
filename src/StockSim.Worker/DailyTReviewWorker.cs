using StockSim.Application.Services;

namespace StockSim.Worker;

/// <summary>
/// 每个交易日约 15:30（可配置）为做T股入队「每日复盘」任务（不直接调 Agent）。
/// </summary>
public sealed class DailyTReviewWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailyTReviewWorker> _logger;
    private readonly TimeSpan _runAt;
    private readonly string _stampPath;
    private DateTime _lastSuccessDate;
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    public DailyTReviewWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<DailyTReviewWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var hour = configuration.GetValue("Worker:DailyTReviewHour", 15);
        var minute = configuration.GetValue("Worker:DailyTReviewMinute", 30);
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        _runAt = new TimeSpan(hour, minute, 0);

        var workDir = configuration.GetValue<string>("AI:WorkDirectory")
                      ?? Path.Combine(AppContext.BaseDirectory, "data", "ai");
        Directory.CreateDirectory(workDir);
        _stampPath = Path.Combine(workDir, "daily_t_review.stamp");
        _lastSuccessDate = ReadStamp();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Daily T review worker started. RunAt={RunAt} LastSuccess={LastSuccess}",
            _runAt, _lastSuccessDate == DateTime.MinValue ? "(never)" : _lastSuccessDate.ToString("yyyy-MM-dd"));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                if (ShouldRun(now))
                {
                    if (_lastAttemptUtc != DateTime.MinValue
                        && DateTime.UtcNow - _lastAttemptUtc < TimeSpan.FromMinutes(10)
                        && _lastSuccessDate.Date < now.Date)
                    {
                        _logger.LogDebug("Daily T review enqueue due but in backoff");
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
                _logger.LogError(ex, "Daily T review loop error");
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
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;
        if (_lastSuccessDate.Date >= now.Date)
            return false;
        // 须晚于结算（约 15:05）且达到配置时刻
        if (now.TimeOfDay >= _runAt)
            return true;
        // 漏跑：上一交易日未入队则补一次
        if (_lastSuccessDate.Date < now.Date.AddDays(-1) && now.TimeOfDay >= _runAt)
            return true;
        return false;
    }

    private async Task RunOnceAsync(DateTime now, CancellationToken stoppingToken)
    {
        _lastAttemptUtc = DateTime.UtcNow;
        _logger.LogInformation("Daily T review enqueue starting at {Now}", now);

        using var scope = _scopeFactory.CreateScope();
        var reviews = scope.ServiceProvider.GetRequiredService<DailyTReviewAppService>();
        var (queued, skippedOrDone, total) = await reviews.EnqueueAllIntradayTAsync(
            now.Date, force: false, stoppingToken);

        WriteStamp(now.Date);
        _lastSuccessDate = now.Date;
        _logger.LogInformation(
            "Daily T review for {Date}: queued={Queued} skippedOrDone={Skipped} totalT={Total}",
            now.Date, queued, skippedOrDone, total);
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
            _logger.LogWarning(ex, "Failed to read daily T review stamp");
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
            _logger.LogWarning(ex, "Failed to write daily T review stamp");
        }
    }
}
