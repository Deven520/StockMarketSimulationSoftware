using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Application.Services;

/// <summary>
/// 一股一策：入池自动 AI 生成；夜间结合当日数据更新专属策略；并维护次日高低价预测与准确率。
/// </summary>
public class PerStockStrategyService
{
    private readonly IStockSimDataStore _db;
    private readonly ICursorAiClient _ai;
    private readonly IStockDetailProvider _details;
    private readonly IMarketNewsProvider _news;
    private readonly StrategyAppService _strategies;
    private readonly ILogger<PerStockStrategyService> _logger;

    public PerStockStrategyService(
        IStockSimDataStore db,
        ICursorAiClient ai,
        IStockDetailProvider details,
        IMarketNewsProvider news,
        StrategyAppService strategies,
        ILogger<PerStockStrategyService> logger)
    {
        _db = db;
        _ai = ai;
        _details = details;
        _news = news;
        _strategies = strategies;
        _logger = logger;
    }

    /// <summary>入池即时：创建默认参数专属策略（不调 AI）。</summary>
    public async Task<(bool Ok, string Message)> EnsureDefaultStrategyAsync(Guid poolItemId, CancellationToken ct = default)
    {
        var item = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Id == poolItemId, ct);
        if (item is null) return (false, "池成分不存在");
        if (item.BoundStrategyId.HasValue) return (true, "已有专属策略");

        var created = await _strategies.CreateAsync(new Dtos.CreateStrategyRequest(
            DefaultStrategyName(item.Name),
            $"池内专属策略 | {item.Symbol} {item.Name}",
            StrategyParameters.Default().ToJson(),
            "入池默认策略"), ct);

        await BindSymbolAsync(created.Id, item.StockPoolId, item.Symbol, ct);
        item.BoundStrategyId = created.Id;
        item.StrategyGeneratedAt = DateTime.UtcNow;
        item.StrategyUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return (true, $"已为 {item.Name} 创建默认策略");
    }

    /// <summary>新股票入池后 AI 生成/刷新（可后台调用）。</summary>
    public Task<Dtos.StrategyAiRunResult> GenerateForNewItemAsync(Guid poolItemId, CancellationToken ct = default)
        => GenerateOrRefreshAsync(poolItemId, isNightlyUpdate: false, ct);

    /// <summary>夜间刷新：在原有策略上更新参数版本。</summary>
    public Task<Dtos.StrategyAiRunResult> RefreshItemAsync(Guid poolItemId, CancellationToken ct = default)
        => GenerateOrRefreshAsync(poolItemId, isNightlyUpdate: true, ct);

    /// <summary>刷新全部池内已绑定策略的股票（每晚 22:00）。</summary>
    public async Task<(int Ok, int Fail)> RefreshAllBoundItemsAsync(CancellationToken ct = default)
    {
        var ids = await _db.StockPoolItems
            .Where(i => i.BoundStrategyId != null)
            .Select(i => i.Id)
            .ToListAsync(ct);

        var ok = 0;
        var fail = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await RefreshItemAsync(id, ct);
                if (result.Ok) ok++;
                else
                {
                    fail++;
                    _logger.LogWarning("Nightly strategy refresh failed for {ItemId}: {Msg}", id, result.Message);
                }
            }
            catch (Exception ex)
            {
                fail++;
                _logger.LogError(ex, "Nightly strategy refresh exception for {ItemId}", id);
            }
        }

        return (ok, fail);
    }

    /// <summary>为尚无专属策略的池成分补建默认策略（兼容旧数据，不调 AI）。</summary>
    public async Task<(int Ok, int Fail)> EnsureMissingStrategiesAsync(CancellationToken ct = default)
    {
        var ids = await _db.StockPoolItems
            .Where(i => i.BoundStrategyId == null)
            .Select(i => i.Id)
            .ToListAsync(ct);

        var ok = 0;
        var fail = 0;
        foreach (var id in ids)
        {
            var r = await EnsureDefaultStrategyAsync(id, ct);
            if (r.Ok) ok++;
            else fail++;
        }

        return (ok, fail);
    }

    public async Task<IReadOnlyList<Dtos.PriceRangeForecastDto>> GetForecastHistoryAsync(
        string symbol,
        int take = 30,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 100);
        var rows = await _db.PriceRangeForecasts
            .Where(f => f.Symbol == symbol)
            .OrderByDescending(f => f.ForecastForDate)
            .ThenByDescending(f => f.PredictedAt)
            .Take(take)
            .ToListAsync(ct);

        return rows.Select(ToDto).ToList();
    }

    public async Task<(bool Ok, string Message)> DeleteForecastAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.PriceRangeForecasts.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (row is null) return (false, "预测记录不存在");
        _db.PriceRangeForecasts.Remove(row);
        await _db.SaveChangesAsync(ct);
        return (true, "已删除预测记录");
    }

    private async Task<Dtos.StrategyAiRunResult> GenerateOrRefreshAsync(
        Guid poolItemId,
        bool isNightlyUpdate,
        CancellationToken ct)
    {
        var item = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Id == poolItemId, ct);
        if (item is null) return new Dtos.StrategyAiRunResult(false, "池成分不存在");

        var detail = await _details.GetDetailAsync(item.Symbol, item.Name, ct);
        var daily21 = (detail?.DailyBars ?? Array.Empty<DailyBar>())
            .OrderByDescending(d => d.Date)
            .Take(21)
            .OrderBy(d => d.Date)
            .Select(d => new
            {
                date = d.Date.ToString("yyyy-MM-dd"),
                d.Open,
                d.High,
                d.Low,
                d.Close,
                d.Volume,
                d.Amount
            })
            .ToList();

        // 夜更：先对账「今日（及未结）预测 vs 实际高低」
        object? forecastEvaluation = null;
        if (isNightlyUpdate)
        {
            forecastEvaluation = await EvaluatePendingForecastsAsync(item, detail, ct);
        }

        var hotNews = await _news.GetHotNewsAsync(12, ct);
        var stockNews = await _news.GetStockNewsAsync(item.Symbol, 8, ct);

        object? currentParams = null;
        object? todayTrades = Array.Empty<object>();
        if (item.BoundStrategyId.HasValue)
        {
            var strategy = await _db.Strategies.Include(s => s.Versions)
                .FirstOrDefaultAsync(s => s.Id == item.BoundStrategyId, ct);
            var current = strategy?.Versions.FirstOrDefault(v => v.Id == strategy.CurrentVersionId)
                          ?? strategy?.Versions.MaxBy(v => v.VersionNumber);
            currentParams = current?.ParametersJson;
            todayTrades = await _db.Trades
                .Where(t => t.Symbol == item.Symbol && t.TradeDate == DateTime.Today)
                .OrderByDescending(t => t.CreationTime)
                .Take(50)
                .Select(t => new { t.Side, t.Quantity, t.Price, t.TotalFee, t.CreationTime })
                .ToListAsync(ct);
        }

        var mode = isNightlyUpdate ? "NightlyRefresh" : "OnPoolEntry";
        var recentDecisions = await _db.StrategyDecisionLogs
            .Where(d => d.Symbol == item.Symbol && d.TradeDate == DateTime.Today)
            .OrderByDescending(d => d.DecidedAt)
            .Take(40)
            .Select(d => new { d.State, d.Action, d.Reason, d.DeviationPct, d.BlockedBy, d.DecidedAt })
            .ToListAsync(ct);

        var userGoal = await ResolveUserGoalAsync(item, ct);
        var goalClause = userGoal is null
            ? "用户未绑定有效投资目标时，按稳健做T边界输出。"
            : $"用户预期目标：预期收益 {userGoal.ExpectedReturnPct}% 、预期周期 {userGoal.HorizonDays} 天"
              + (string.IsNullOrWhiteSpace(userGoal.ThemePreference) ? "" : $"、题材偏好「{userGoal.ThemePreference.Trim()}」")
              + "。请在策略边界（aggression/triggers/sizing/risk）与高低价预测中体现该目标节奏，勿承诺收益。";

        var asOfLocal = DateTime.Now;
        var forecastTargetDate = PriceForecastMetrics.ResolveForecastTargetDate(asOfLocal);
        var forecastIsSameDay = PriceForecastMetrics.IsSameDayForecastWindow(asOfLocal);
        var forecastDayLabel = forecastIsSameDay ? "当日（收盘前）" : "下一交易日";
        var (meanHighDev, meanLowDev, avgAiAcc) = await LoadAiBiasStatsAsync(item.Symbol, ct);
        var calibrationHint = avgAiAcc is null
            ? "尚无足够 AI 预测对账样本；请独立给出高低价，勿用参考价±固定百分比占位。"
            : $"近史 AI 预测平均偏差 高={meanHighDev:F2}% 低={meanLowDev:F2}% ，综合准确率={avgAiAcc:F1}；请据此纠偏，禁止输出占位区间。";
        var prompt = isNightlyUpdate
            ? $"请为 {item.Symbol} {item.Name} 输出 AdaptiveIntradayT 策略画像 StrategyPolicy(version=2)。{goalClause}结合近21日行情、今日成交、决策日志、热点新闻与「高低价预测对账结果」迭代；只定边界与偏好（triggers/sizing/risk/session/style），不要输出盘中下单指令。必须在 parameters.forecast（或根级 forecast）给出对{forecastDayLabel} {forecastTargetDate:yyyy-MM-dd} 的 predictedHigh / predictedLow（元），并设 source=ai、countsTowardAccuracy=true。{calibrationHint}规则：交易日 15:00 前预测当日高低，收盘后/非交易日预测下一交易日高低。"
            : $"请为 {item.Symbol} {item.Name} 生成 AdaptiveIntradayT 策略画像 StrategyPolicy(version=2)：style/aggression/session/triggers(含 anchor=vwap 与 buy/sellDeviationPct 的 base/min/max)/sizing/risk/factors/aiNotes/forecast。{goalClause}forecast 须含 forDate={forecastTargetDate:yyyy-MM-dd}、predictedHigh、predictedLow、source=ai、countsTowardAccuracy=true（预测{forecastDayLabel}最高/最低价）。{calibrationHint}规则：交易日 15:00 前预测当日高低，收盘后/非交易日预测下一交易日高低。用于状态机「先高抛后低吸」做T，参数是边界不是死脚本。";

        var payload = JsonSerializer.Serialize(new
        {
            mode,
            schema = "StrategyPolicy/v2",
            symbol = item.Symbol,
            name = item.Name,
            poolId = item.StockPoolId,
            userGoal = userGoal is null ? null : new
            {
                goalId = userGoal.Id,
                expectedReturnPct = userGoal.ExpectedReturnPct,
                horizonDays = userGoal.HorizonDays,
                themePreference = userGoal.ThemePreference,
                status = userGoal.Status.ToString(),
                startDate = userGoal.StartDate?.ToString("yyyy-MM-dd"),
                targetEndDate = userGoal.TargetEndDate?.ToString("yyyy-MM-dd")
            },
            lookbackDays = 21,
            dailyBars = daily21,
            quote = detail?.Board is null ? null : new
            {
                detail.Board.Last,
                detail.Board.Open,
                detail.Board.High,
                detail.Board.Low,
                detail.Board.ChangePercent,
                detail.Board.PreClose,
                detail.Board.TurnoverRate
            },
            fundamentals = detail?.Fundamentals,
            hotNews = hotNews.Select(n => new { n.Title, n.Summary, n.Source, n.PublishedAt }),
            stockNews = stockNews.Select(n => new { n.Title, n.Summary, n.Source }),
            currentParameters = currentParams,
            todayTrades,
            recentDecisions,
            forecastEvaluation,
            forecastCalibration = new
            {
                meanHighDeviationPct = meanHighDev,
                meanLowDeviationPct = meanLowDev,
                avgAiCombinedAccuracyPct = avgAiAcc
            },
            atrBaselineHint = PriceForecastMetrics.TryBuildAtrBaseline(detail?.DailyBars, forecastTargetDate),
            forecastTargetDate = forecastTargetDate.ToString("yyyy-MM-dd"),
            forecastScope = forecastIsSameDay ? "sameTradingDay" : "nextTradingDay",
            forecastRule = "交易日且本地时间<15:00预测当日高低；否则预测下一交易日高低；占位预测不得计入准确率",
            expectedOutput = new
            {
                analysis = "string",
                steps = new[] { "string" },
                parameters = StrategyParameters.Default(),
                forecast = new
                {
                    forDate = forecastTargetDate.ToString("yyyy-MM-dd"),
                    predictedHigh = 0,
                    predictedLow = 0,
                    rationale = "string",
                    source = "ai",
                    countsTowardAccuracy = true
                },
                changeNote = "string",
                risk = "string"
            },
            asOf = asOfLocal
        });

        var job = new AiJob
        {
            JobType = AiJobType.StrategyUpdate,
            Status = AiJobStatus.Running,
            Prompt = prompt,
            InputPayload = payload
        };
        _db.AiJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        try
        {
            var output = await _ai.RunStrategyUpdateAsync(prompt, payload, ct);
            job.OutputPayload = output;
            var parsed = ParseStrategyOutput(output);
            job.AnalysisSummary = parsed.Analysis;
            job.AnalysisStepsJson = parsed.StepsJson;

            var parametersJson = parsed.ParametersJson ?? StrategyParameters.Default().ToJson();
            var policy = StrategyParameters.FromJson(parametersJson);
            var atrBaseline = PriceForecastMetrics.TryBuildAtrBaseline(detail?.DailyBars, forecastTargetDate);
            ApplyOperationalForecast(
                policy,
                parsed.Forecast,
                atrBaseline,
                meanHighDev,
                meanLowDev,
                avgAiAcc,
                forecastTargetDate);
            parametersJson = policy.ToJson();

            Guid strategyId;
            Guid? versionId = null;
            if (!item.BoundStrategyId.HasValue)
            {
                var created = await _strategies.CreateAsync(new Dtos.CreateStrategyRequest(
                    DefaultStrategyName(item.Name),
                    $"池内专属策略 | {item.Symbol} {item.Name}",
                    parametersJson,
                    parsed.ChangeNote ?? "入池AI自动生成"), ct);

                await BindSymbolAsync(created.Id, item.StockPoolId, item.Symbol, ct);

                item.BoundStrategyId = created.Id;
                item.StrategyGeneratedAt = DateTime.UtcNow;
                item.StrategyUpdatedAt = DateTime.UtcNow;
                strategyId = created.Id;
                versionId = await GetCurrentVersionIdAsync(strategyId, ct);
            }
            else
            {
                await _strategies.UpdateParamsAsync(
                    item.BoundStrategyId.Value,
                    new Dtos.UpdateStrategyParamsRequest(
                        parametersJson,
                        parsed.ChangeNote ?? (isNightlyUpdate ? "夜间AI自动更新" : "AI刷新")),
                    ct);
                item.StrategyUpdatedAt = DateTime.UtcNow;
                if (!item.StrategyGeneratedAt.HasValue)
                {
                    item.StrategyGeneratedAt = DateTime.UtcNow;
                }

                await BindSymbolAsync(item.BoundStrategyId.Value, item.StockPoolId, item.Symbol, ct);
                strategyId = item.BoundStrategyId.Value;
                versionId = await GetCurrentVersionIdAsync(strategyId, ct);
            }

            // AI/校准预测：占位不落准确率表；ATR 基线始终落库对照
            if (policy.Forecast is not null
                && policy.Forecast.PredictedHigh > 0
                && policy.Forecast.PredictedLow > 0
                && !PriceForecastMetrics.IsPlaceholderForecast(policy.Forecast))
            {
                await UpsertPendingForecastAsync(
                    item,
                    policy.Forecast,
                    strategyId,
                    versionId,
                    job.Id,
                    prompt,
                    forecastTargetDate,
                    ct);
            }

            if (atrBaseline is not null)
            {
                await UpsertPendingForecastAsync(
                    item,
                    atrBaseline,
                    strategyId,
                    versionId,
                    job.Id,
                    "[ATR baseline]",
                    forecastTargetDate,
                    ct);
            }

            var suggestion = new AiSuggestion
            {
                AiJobId = job.Id,
                PayloadJson = parsed.RawSuggestionJson ?? output,
                Symbol = item.Symbol,
                Name = item.Name,
                Reason = parsed.ChangeNote ?? parsed.Analysis,
                Risk = parsed.Risk,
                IsAccepted = true,
                ReviewedAt = DateTime.UtcNow
            };
            _db.AiSuggestions.Add(suggestion);

            job.Status = AiJobStatus.Accepted;
            job.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return new Dtos.StrategyAiRunResult(
                true,
                isNightlyUpdate
                    ? $"已更新 {item.Symbol} 专属策略"
                    : $"已为 {item.Symbol} 生成专属策略",
                job.Id,
                prompt,
                payload,
                output);
        }
        catch (Exception ex)
        {
            job.Status = AiJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            if (!item.BoundStrategyId.HasValue)
            {
                try
                {
                    var created = await _strategies.CreateAsync(new Dtos.CreateStrategyRequest(
                        DefaultStrategyName(item.Name),
                        $"池内专属策略(默认参数) | AI失败: {Truncate(ex.Message, 80)}",
                        StrategyParameters.Default().ToJson(),
                        "AI失败回退默认参数"), ct);
                    await BindSymbolAsync(created.Id, item.StockPoolId, item.Symbol, ct);
                    item.BoundStrategyId = created.Id;
                    item.StrategyGeneratedAt = DateTime.UtcNow;
                    item.StrategyUpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    return new Dtos.StrategyAiRunResult(
                        true,
                        $"AI失败，已用默认参数创建 {item.Symbol} 策略: {ex.Message}",
                        job.Id,
                        prompt,
                        payload,
                        job.OutputPayload ?? ex.Message);
                }
                catch (Exception inner)
                {
                    return new Dtos.StrategyAiRunResult(
                        false,
                        $"创建策略失败: {inner.Message}",
                        job.Id,
                        prompt,
                        payload,
                        job.OutputPayload ?? ex.Message);
                }
            }

            return new Dtos.StrategyAiRunResult(
                false,
                ex.Message,
                job.Id,
                prompt,
                payload,
                job.OutputPayload ?? ex.Message);
        }
    }

    /// <summary>
    /// 解析该股关联的用户投资目标（优先 Active，其次同代码最近目标；回退到最新 Active 目标）。
    /// </summary>
    private async Task<InvestmentGoal?> ResolveUserGoalAsync(StockPoolItem item, CancellationToken ct)
    {
        var linkedGoalIds = await _db.InvestmentGoalStocks
            .Where(s => s.Symbol == item.Symbol || s.PoolItemId == item.Id)
            .Select(s => s.InvestmentGoalId)
            .Distinct()
            .ToListAsync(ct);

        if (linkedGoalIds.Count > 0)
        {
            var linked = await _db.InvestmentGoals
                .Where(g => linkedGoalIds.Contains(g.Id)
                            && g.Status != InvestmentGoalStatus.Cancelled
                            && g.Status != InvestmentGoalStatus.Draft)
                .OrderByDescending(g => g.Status == InvestmentGoalStatus.Active)
                .ThenByDescending(g => g.CreationTime)
                .FirstOrDefaultAsync(ct);
            if (linked is not null) return linked;
        }

        // 单用户场景：未直接关联时，取最新进行中目标，保证提示词仍带预期收益/周期
        return await _db.InvestmentGoals
            .Where(g => g.Status == InvestmentGoalStatus.Active)
            .OrderByDescending(g => g.CreationTime)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<object?> EvaluatePendingForecastsAsync(
        StockPoolItem item,
        StockDetailBundle? detail,
        CancellationToken ct)
    {
        var today = DateTime.Today;
        var now = DateTime.Now;

        // 模拟/回退行情不得写入准确率（会把演示价当成 Actual）
        if (detail is null || detail.IsSimulated)
        {
            _logger.LogWarning(
                "Skip forecast evaluation for {Symbol}: detail {Reason}",
                item.Symbol,
                detail is null ? "missing" : "simulated/fallback");
            return null;
        }

        var lookbackFrom = today.AddDays(-PriceForecastMetrics.EvaluationLookbackDays);
        // Pending / Skipped 需对账；已 Evaluated 在拿到真日K后允许纠错重算（修复 Board/模拟误写）
        var rows = await _db.PriceRangeForecasts
            .Where(f => f.Symbol == item.Symbol
                        && f.ForecastForDate <= today
                        && f.ForecastForDate >= lookbackFrom
                        && (f.Status == ForecastStatus.Pending
                            || f.Status == ForecastStatus.Skipped
                            || f.Status == ForecastStatus.Evaluated))
            .OrderBy(f => f.ForecastForDate)
            .ToListAsync(ct);

        if (rows.Count == 0) return null;

        var results = new List<object>();
        foreach (var row in rows)
        {
            var target = row.ForecastForDate.Date;
            if (!PriceForecastMetrics.CanFinalizeForecast(target, now))
                continue;

            var actual = PriceForecastMetrics.ResolveActualHighLowFromDailyBars(detail.DailyBars, target);
            if (actual is null)
            {
                // 超过 5 个自然日仍无日K则跳过，避免永久挂起
                if (row.Status == ForecastStatus.Pending
                    && (today - target).TotalDays > 5)
                {
                    row.Status = ForecastStatus.Skipped;
                    row.EvaluatedAt = now;
                    row.Notes = "超时未取到目标日日K高低价，已跳过";
                    results.Add(new
                    {
                        forecastId = row.Id,
                        symbol = row.Symbol,
                        name = row.Name,
                        forecastForDate = target.ToString("yyyy-MM-dd"),
                        status = "Skipped",
                        reason = row.Notes
                    });
                }

                continue;
            }

            var (highDev, lowDev, highAcc, lowAcc, combined, rangeHit, _) = PriceForecastMetrics.Evaluate(
                row.PredictedHigh,
                row.PredictedLow,
                actual.Value.High,
                actual.Value.Low);

            // 已对账、日K一致且得分未变则跳过；容差/公式变更时仍重写
            if (row.Status == ForecastStatus.Evaluated
                && row.ActualHigh == actual.Value.High
                && row.ActualLow == actual.Value.Low
                && row.CombinedAccuracyPct == combined
                && row.HighAccuracyPct == highAcc
                && row.LowAccuracyPct == lowAcc
                && row.RangeHit == rangeHit)
                continue;

            var reevaluated = row.Status == ForecastStatus.Evaluated
                              || row.Status == ForecastStatus.Skipped;

            row.ActualHigh = actual.Value.High;
            row.ActualLow = actual.Value.Low;
            row.HighDeviationPct = highDev;
            row.LowDeviationPct = lowDev;
            row.HighAccuracyPct = highAcc;
            row.LowAccuracyPct = lowAcc;
            row.CombinedAccuracyPct = combined;
            row.RangeHit = rangeHit;
            row.EvaluatedAt = now;
            row.Status = ForecastStatus.Evaluated;
            row.CountsTowardAccuracy = PriceForecastMetrics.ShouldCountTowardAccuracy(row.Source);
            row.Notes = reevaluated
                ? $"来源 {row.Source}；容差 {PriceForecastMetrics.DefaultTolerancePct}%；已按日K重算"
                : $"来源 {row.Source}；容差 {PriceForecastMetrics.DefaultTolerancePct}%";

            results.Add(new
            {
                forecastId = row.Id,
                symbol = row.Symbol,
                name = row.Name,
                forecastForDate = target.ToString("yyyy-MM-dd"),
                predictedHigh = row.PredictedHigh,
                predictedLow = row.PredictedLow,
                actualHigh = row.ActualHigh,
                actualLow = row.ActualLow,
                highDeviationPct = highDev,
                lowDeviationPct = lowDev,
                highAccuracyPct = highAcc,
                lowAccuracyPct = lowAcc,
                combinedAccuracyPct = combined,
                rangeHit,
                reevaluated,
                status = "Evaluated"
            });
        }

        await _db.SaveChangesAsync(ct);
        return results.Count == 0 ? null : results;
    }

    private async Task UpsertPendingForecastAsync(
        StockPoolItem item,
        ForecastPolicy forecast,
        Guid strategyId,
        Guid? versionId,
        Guid aiJobId,
        string? promptText,
        DateTime forecastTargetDate,
        CancellationToken ct)
    {
        // 以系统规则解析的目标日为准，避免 AI 写错 forDate
        var forDate = forecastTargetDate.Date;
        var high = Math.Max(forecast.PredictedHigh, forecast.PredictedLow);
        var low = Math.Min(forecast.PredictedHigh, forecast.PredictedLow);
        if (high <= 0 || low <= 0) return;

        var source = PriceForecastMetrics.ParseSource(forecast.Source, forecast.CountsTowardAccuracy);
        if (source == ForecastSource.Placeholder) return; // 双保险：占位不入库

        var counts = forecast.CountsTowardAccuracy
                     ?? PriceForecastMetrics.ShouldCountTowardAccuracy(source);

        var existing = await _db.PriceRangeForecasts.FirstOrDefaultAsync(
            f => f.Symbol == item.Symbol
                 && f.ForecastForDate == forDate
                 && f.Source == source
                 && f.Status == ForecastStatus.Pending,
            ct);

        if (existing is not null)
        {
            existing.Name = item.Name;
            existing.PredictedHigh = high;
            existing.PredictedLow = low;
            existing.PredictedAt = DateTime.Now;
            existing.Source = source;
            existing.CountsTowardAccuracy = counts;
            existing.StrategyDefinitionId = strategyId;
            existing.StrategyVersionId = versionId;
            existing.AiJobId = aiJobId;
            existing.Prompt = promptText;
            existing.Notes = forecast.Rationale;
        }
        else
        {
            _db.PriceRangeForecasts.Add(new PriceRangeForecast
            {
                Symbol = item.Symbol,
                Name = item.Name,
                ForecastForDate = forDate,
                PredictedAt = DateTime.Now,
                PredictedHigh = high,
                PredictedLow = low,
                Status = ForecastStatus.Pending,
                Source = source,
                CountsTowardAccuracy = counts,
                StrategyDefinitionId = strategyId,
                StrategyVersionId = versionId,
                AiJobId = aiJobId,
                Prompt = promptText,
                Notes = forecast.Rationale
            });
        }
    }

    private async Task<Guid?> GetCurrentVersionIdAsync(Guid strategyId, CancellationToken ct)
    {
        var strategy = await _db.Strategies.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == strategyId, ct);
        return strategy?.CurrentVersionId;
    }

    /// <summary>
    /// 运营用预测写入 Policy：真实 AI（可校准）优先，否则 ATR 基线；禁止 ±% 占位污染。
    /// 低准确率时下调 aggression。
    /// </summary>
    private static void ApplyOperationalForecast(
        StrategyParameters policy,
        ForecastPolicy? parsedForecast,
        ForecastPolicy? atrBaseline,
        decimal meanHighDev,
        decimal meanLowDev,
        decimal? avgAiAcc,
        DateTime forecastTargetDate)
    {
        var target = forecastTargetDate.Date.ToString("yyyy-MM-dd");
        ForecastPolicy? operational = null;

        var ai = parsedForecast ?? policy.Forecast;
        if (ai is not null
            && ai.PredictedHigh > 0
            && ai.PredictedLow > 0
            && !PriceForecastMetrics.IsPlaceholderForecast(ai))
        {
            if (ai.PredictedHigh < ai.PredictedLow)
                (ai.PredictedHigh, ai.PredictedLow) = (ai.PredictedLow, ai.PredictedHigh);
            ai.ForDate = target;
            ai.AsOf ??= DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            ai.Source ??= PriceForecastMetrics.ToSourceKey(ForecastSource.Ai);
            ai.CountsTowardAccuracy ??= true;

            // 有历史偏差样本则校准
            if (avgAiAcc.HasValue && (Math.Abs(meanHighDev) >= 0.05m || Math.Abs(meanLowDev) >= 0.05m))
                operational = PriceForecastMetrics.ApplyBiasCalibration(ai, meanHighDev, meanLowDev);
            else
                operational = ai;
        }
        else if (atrBaseline is not null)
        {
            atrBaseline.ForDate = target;
            operational = atrBaseline;
        }

        policy.Forecast = operational;

        var scale = PriceForecastMetrics.ResolveAggressionScale(avgAiAcc);
        if (scale < 1m)
        {
            policy.Aggression = Math.Round(Math.Clamp(policy.Aggression * scale, 0.05m, 1m), 3);
            policy.AiNotes = (policy.AiNotes ?? string.Empty)
                + $" | 近史AI准确率{avgAiAcc:F1}偏低，aggression×{scale:F2}";
        }
    }

    private async Task<(decimal MeanHighDev, decimal MeanLowDev, decimal? AvgCombined)> LoadAiBiasStatsAsync(
        string symbol,
        CancellationToken ct)
    {
        var from = DateTime.Today.AddDays(-PriceForecastMetrics.EvaluationLookbackDays);
        var rows = await _db.PriceRangeForecasts.AsNoTracking()
            .Where(f => f.Symbol == symbol
                        && f.Status == ForecastStatus.Evaluated
                        && f.CountsTowardAccuracy
                        && f.ForecastForDate >= from
                        && (f.Source == ForecastSource.Ai || f.Source == ForecastSource.CalibratedAi)
                        && f.HighDeviationPct != null
                        && f.LowDeviationPct != null
                        && f.CombinedAccuracyPct != null)
            .OrderByDescending(f => f.ForecastForDate)
            .Take(20)
            .ToListAsync(ct);

        if (rows.Count < 2) return (0m, 0m, null);

        var meanHigh = Math.Round(rows.Average(r => r.HighDeviationPct!.Value), 4);
        var meanLow = Math.Round(rows.Average(r => r.LowDeviationPct!.Value), 4);
        var avgAcc = Math.Round(rows.Average(r =>
            PriceForecastMetrics.RescoreCombinedAccuracy(r.HighDeviationPct, r.LowDeviationPct)
            ?? r.CombinedAccuracyPct!.Value), 2);
        return (meanHigh, meanLow, avgAcc);
    }

    private async Task BindSymbolAsync(Guid strategyId, Guid poolId, string symbol, CancellationToken ct)
    {
        var exists = await _db.StrategyBindings.AnyAsync(
            b => b.StrategyDefinitionId == strategyId
                 && b.StockPoolId == poolId
                 && b.Symbol == symbol, ct);
        if (exists) return;

        _db.StrategyBindings.Add(new StrategyBinding
        {
            StrategyDefinitionId = strategyId,
            StockPoolId = poolId,
            Symbol = symbol,
            Priority = 100,
            IsEnabled = true
        });
        await _db.SaveChangesAsync(ct);
    }

    private static ParsedStrategyAi ParseStrategyOutput(string output)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "{}" : output);
        var root = doc.RootElement;
        var analysis = GetString(root, "analysis") ?? GetString(root, "summary");
        string? stepsJson = null;
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            stepsJson = steps.GetRawText();
        }

        string? parametersJson = null;
        JsonElement? parametersEl = null;
        if (root.TryGetProperty("parameters", out var p) || root.TryGetProperty("policy", out p))
        {
            parametersEl = p;
            parametersJson = p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText();
        }
        else if (root.TryGetProperty("mode", out _) || root.TryGetProperty("triggers", out _))
        {
            parametersEl = root;
            parametersJson = root.GetRawText();
        }

        var forecast = TryParseForecast(root);
        if (forecast is null && parametersEl is { } pe)
            forecast = TryParseForecast(pe);

        return new ParsedStrategyAi(
            analysis,
            stepsJson,
            parametersJson,
            GetString(root, "changeNote"),
            GetString(root, "risk"),
            root.TryGetProperty("parameters", out _) ? root.GetRawText() : output,
            forecast);
    }

    private static ForecastPolicy? TryParseForecast(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        JsonElement fcEl = default;
        var hasObject = el.TryGetProperty("forecast", out fcEl) && fcEl.ValueKind == JsonValueKind.Object;
        if (!hasObject)
        {
            // 根级扁平字段
            var high = GetDecimal(el, "predictedHigh");
            var low = GetDecimal(el, "predictedLow");
            if (high is null || low is null) return null;
            return new ForecastPolicy
            {
                ForDate = GetString(el, "forDate") ?? GetString(el, "forecastForDate"),
                PredictedHigh = high.Value,
                PredictedLow = low.Value,
                AsOf = GetString(el, "asOf"),
                Rationale = GetString(el, "rationale") ?? GetString(el, "forecastRationale"),
                Source = GetString(el, "source"),
                CountsTowardAccuracy = GetBool(el, "countsTowardAccuracy")
            };
        }

        return new ForecastPolicy
        {
            ForDate = GetString(fcEl, "forDate") ?? GetString(fcEl, "forecastForDate"),
            PredictedHigh = GetDecimal(fcEl, "predictedHigh") ?? 0,
            PredictedLow = GetDecimal(fcEl, "predictedLow") ?? 0,
            AsOf = GetString(fcEl, "asOf"),
            Rationale = GetString(fcEl, "rationale"),
            Source = GetString(fcEl, "source"),
            CountsTowardAccuracy = GetBool(fcEl, "countsTowardAccuracy")
        };
    }

    private static DateTime? ParseForDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            return d.Date;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
            return d.Date;
        return null;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static decimal? GetDecimal(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p))
            return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d))
            return d;
        if (p.ValueKind == JsonValueKind.String
            && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d))
            return d;
        return null;
    }

    private static bool? GetBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p))
            return null;
        if (p.ValueKind is JsonValueKind.True) return true;
        if (p.ValueKind is JsonValueKind.False) return false;
        if (p.ValueKind == JsonValueKind.String
            && bool.TryParse(p.GetString(), out var b))
            return b;
        return null;
    }

    private static string DefaultStrategyName(string? stockName)
    {
        var name = string.IsNullOrWhiteSpace(stockName) ? "未命名" : stockName.Trim();
        return name.EndsWith("策略", StringComparison.Ordinal) ? name : name + "策略";
    }

    private static string Truncate(string? s, int n)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s.Length <= n ? s : s[..n] + "…";
    }

    private static Dtos.PriceRangeForecastDto ToDto(PriceRangeForecast f)
    {
        decimal? highAbs = f.ActualHigh is { } ah
            ? PriceForecastMetrics.AbsoluteError(f.PredictedHigh, ah)
            : null;
        decimal? lowAbs = f.ActualLow is { } al
            ? PriceForecastMetrics.AbsoluteError(f.PredictedLow, al)
            : null;
        decimal? meanAbs = f.HighDeviationPct is { } hd && f.LowDeviationPct is { } ld
            ? PriceForecastMetrics.MeanAbsDeviationPct(hd, ld)
            : null;
        var combined = PriceForecastMetrics.RescoreCombinedAccuracy(f.HighDeviationPct, f.LowDeviationPct)
                       ?? f.CombinedAccuracyPct;

        return new(
            f.Id,
            f.Symbol,
            f.Name,
            f.ForecastForDate,
            f.PredictedAt,
            f.PredictedHigh,
            f.PredictedLow,
            f.ActualHigh,
            f.ActualLow,
            f.HighDeviationPct,
            f.LowDeviationPct,
            f.HighDeviationPct is { } hDev ? PriceForecastMetrics.AccuracyPct(hDev) : f.HighAccuracyPct,
            f.LowDeviationPct is { } lDev ? PriceForecastMetrics.AccuracyPct(lDev) : f.LowAccuracyPct,
            combined,
            f.RangeHit,
            f.Status.ToString(),
            f.EvaluatedAt,
            f.Notes,
            f.Prompt,
            f.Source.ToString(),
            f.CountsTowardAccuracy,
            highAbs,
            lowAbs,
            meanAbs,
            PriceForecastMetrics.AccuracyGrade(combined));
    }

    private sealed record ParsedStrategyAi(
        string? Analysis,
        string? StepsJson,
        string? ParametersJson,
        string? ChangeNote,
        string? Risk,
        string? RawSuggestionJson,
        ForecastPolicy? Forecast);
}
