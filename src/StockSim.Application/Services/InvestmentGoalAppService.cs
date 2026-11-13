using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockSim.Application.Dtos;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;

namespace StockSim.Application.Services;

/// <summary>投资目标：AI 两段选股、入选跟踪、每日达成率与次日命中对账。</summary>
public class InvestmentGoalAppService
{
    public const decimal DefaultBaselineCash = 200_000m;

    private readonly IStockSimDataStore _db;
    private readonly ICursorAiClient _ai;
    private readonly StockPoolAppService _pools;
    private readonly CachedLightQuoteService _quotes;
    private readonly IMarketNewsProvider _news;
    private readonly IStockDetailProvider _detailProvider;
    private readonly NextDayPickReviewAppService _nextDayPickReviews;
    private readonly NextDayPickFilterAppService _nextDayPickFilter;
    private readonly ILogger<InvestmentGoalAppService> _logger;

    public InvestmentGoalAppService(
        IStockSimDataStore db,
        ICursorAiClient ai,
        StockPoolAppService pools,
        CachedLightQuoteService quotes,
        IMarketNewsProvider news,
        IStockDetailProvider detailProvider,
        NextDayPickReviewAppService nextDayPickReviews,
        NextDayPickFilterAppService nextDayPickFilter,
        ILogger<InvestmentGoalAppService> logger)
    {
        _db = db;
        _ai = ai;
        _pools = pools;
        _quotes = quotes;
        _news = news;
        _detailProvider = detailProvider;
        _nextDayPickReviews = nextDayPickReviews;
        _nextDayPickFilter = nextDayPickFilter;
        _logger = logger;
    }

    /// <summary>删除投资目标及其入选股、进度记录。</summary>
    public async Task<(bool Ok, string Message)> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var goal = await _db.InvestmentGoals.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (goal is null) return (false, "目标不存在");

        var stocks = await _db.InvestmentGoalStocks.Where(s => s.InvestmentGoalId == id).ToListAsync(ct);
        var progress = await _db.GoalProgressDailies.Where(p => p.InvestmentGoalId == id).ToListAsync(ct);
        _db.InvestmentGoalStocks.RemoveRange(stocks);
        _db.GoalProgressDailies.RemoveRange(progress);
        _db.InvestmentGoals.Remove(goal);
        await _db.SaveChangesAsync(ct);
        return (true, "已删除投资目标");
    }

    public async Task<IReadOnlyList<InvestmentGoalDto>> ListAsync(CancellationToken ct = default)
    {
        var goals = await _db.InvestmentGoals
            .OrderByDescending(g => g.CreationTime)
            .Take(50)
            .ToListAsync(ct);
        var ids = goals.Select(g => g.Id).ToList();
        var stockCounts = await _db.InvestmentGoalStocks
            .Where(s => ids.Contains(s.InvestmentGoalId))
            .GroupBy(s => s.InvestmentGoalId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var allProgress = await _db.GoalProgressDailies
            .Where(p => ids.Contains(p.InvestmentGoalId))
            .ToListAsync(ct);
        var latestMap = allProgress
            .GroupBy(p => p.InvestmentGoalId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.TradeDate).First());

        var allStocks = ids.Count == 0
            ? new List<InvestmentGoalStock>()
            : await _db.InvestmentGoalStocks.Where(s => ids.Contains(s.InvestmentGoalId)).ToListAsync(ct);
        var stocksByGoal = allStocks.GroupBy(s => s.InvestmentGoalId)
            .ToDictionary(x => x.Key, x => x.ToList());

        return goals.Select(g =>
        {
            latestMap.TryGetValue(g.Id, out var p);
            stockCounts.TryGetValue(g.Id, out var cnt);
            stocksByGoal.TryGetValue(g.Id, out var gs);
            return MapGoal(g, cnt, p?.AchievementRate, p?.PortfolioReturnPct, gs);
        }).ToList();
    }

    public async Task<InvestmentGoalDetailDto?> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        var g = await _db.InvestmentGoals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return null;
        var stocks = await _db.InvestmentGoalStocks.Where(s => s.InvestmentGoalId == id).ToListAsync(ct);
        var suggestionIds = stocks.Where(s => s.SuggestionId.HasValue).Select(s => s.SuggestionId!.Value).ToList();
        var suggestionMap = suggestionIds.Count == 0
            ? new Dictionary<Guid, AiSuggestion>()
            : await _db.AiSuggestions
                .Where(s => suggestionIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, ct);
        var progress = await _db.GoalProgressDailies
            .Where(p => p.InvestmentGoalId == id)
            .OrderBy(p => p.TradeDate)
            .ToListAsync(ct);
        var latest = progress.LastOrDefault();
        var pickReviews = g.TradeStyle == StockTradeStyle.NextDayRise
            ? await _nextDayPickReviews.ListForGoalAsync(id, 30, ct)
            : null;
        decimal? avgRs = null;
        if (pickReviews is { Count: > 0 })
        {
            var rsRows = pickReviews.Where(r => r.RelativeStrengthPct.HasValue).ToList();
            if (rsRows.Count > 0)
                avgRs = Math.Round(rsRows.Average(r => r.RelativeStrengthPct!.Value), 2);
        }

        return new InvestmentGoalDetailDto(
            MapGoal(g, stocks.Count, latest?.AchievementRate, latest?.PortfolioReturnPct, stocks, avgRs),
            stocks.Select(s =>
            {
                AiSuggestion? sug = null;
                if (s.SuggestionId.HasValue) suggestionMap.TryGetValue(s.SuggestionId.Value, out sug);
                return new InvestmentGoalStockDto(
                    s.Id, s.Symbol, s.Name, s.PoolItemId, s.BaselineCash,
                    sug?.SelectionLogic ?? sug?.Reason,
                    sug?.Reason,
                    sug?.Risk,
                    s.BaselinePrice,
                    s.TargetTradeDate,
                    s.ActualClose,
                    s.NextDayReturnPct,
                    s.DirectionHit,
                    s.TargetHit,
                    s.EvaluatedAt);
            }).ToList(),
            progress.Select(p => new GoalProgressDailyDto(
                p.TradeDate, p.PortfolioReturnPct, p.AchievementRate,
                p.TotalMarketValue, p.TotalBaseline, p.RealizedPnL, p.UnrealizedPnL, p.DayIndex, p.Notes)).ToList(),
            pickReviews);
    }

    /// <summary>
    /// 创建目标并入队 Goal→Prompt（异步）；不再阻塞 HTTP。
    /// 返回占位 AiJob（Created）与队列项 Id。
    /// </summary>
    public async Task<(InvestmentGoal Goal, AiJob Job, Guid QueueItemId)> CreateGoalAndPickAsync(
        CreateInvestmentGoalRequest request,
        CancellationToken ct = default)
    {
        if (request.ExpectedReturnPct <= 0) throw new ArgumentException("预期收益须大于 0");
        if (request.HorizonDays < 1) throw new ArgumentException("预期周期至少 1 天");
        if (string.IsNullOrWhiteSpace(request.ThemePreference)) throw new ArgumentException("请填写题材偏好");

        var tradeStyle = request.TradeStyle switch
        {
            StockTradeStyle.TrendSwing => StockTradeStyle.TrendSwing,
            StockTradeStyle.NextDayRise => StockTradeStyle.NextDayRise,
            _ => StockTradeStyle.IntradayT
        };
        var styleLabel = StockTradeStyleHelper.ToLabel(tradeStyle);
        var horizonDays = tradeStyle == StockTradeStyle.NextDayRise ? 1 : request.HorizonDays;
        var nextTradeDay = PriceForecastMetrics.NextTradingDay(DateTime.Today);

        // P0：次日预涨预期收益硬钳制（默认温和隔日涨幅，避免 5%～7% 废掉目标命中口径）
        var expected = tradeStyle == StockTradeStyle.NextDayRise
            ? NextDayPickGuard.ClampExpectedReturnPct(request.ExpectedReturnPct)
            : request.ExpectedReturnPct;

        var goal = new InvestmentGoal
        {
            ExpectedReturnPct = expected,
            HorizonDays = horizonDays,
            ThemePreference = request.ThemePreference.Trim(),
            TradeStyle = tradeStyle,
            Status = InvestmentGoalStatus.Draft
        };
        _db.InvestmentGoals.Add(goal);
        await _db.SaveChangesAsync(ct);

        var marketContext = await BuildMarketContextAsync(tradeStyle, nextTradeDay, ct);
        object? recentPickReviews = null;
        object? pickCalibration = null;
        if (tradeStyle == StockTradeStyle.NextDayRise)
        {
            recentPickReviews = await _nextDayPickReviews.LoadRecentReviewsForPromptAsync(15, ct);
            pickCalibration = await _nextDayPickFilter.LoadCalibrationForPromptAsync(ct);
        }

        var correlationId = Guid.NewGuid();
        var goalPayload = JsonSerializer.Serialize(new
        {
            expectedReturnPct = goal.ExpectedReturnPct,
            expectedReturnNote = tradeStyle == StockTradeStyle.NextDayRise
                ? $"已钳制在 {NextDayPickGuard.MinExpectedReturnPct}～{NextDayPickGuard.MaxExpectedReturnPct}%；主 KPI 为方向命中+相对强弱"
                : null,
            horizonDays = goal.HorizonDays,
            themePreference = goal.ThemePreference,
            tradeStyle = tradeStyle.ToString(),
            tradeStyleLabel = styleLabel,
            nextTradingDay = nextTradeDay.ToString("yyyy-MM-dd"),
            asOf = DateTime.Now,
            marketContext,
            recentNextDayPickReviews = recentPickReviews,
            nextDayPickCalibration = pickCalibration
        });

        var job = new AiJob
        {
            JobType = AiJobType.StockPick,
            Status = AiJobStatus.Created,
            Prompt = $"（排队中）{styleLabel} · 题材「{goal.ThemePreference}」· 预期 {goal.ExpectedReturnPct}% / {goal.HorizonDays} 天",
            InputPayload = JsonSerializer.Serialize(new
            {
                goalId = goal.Id,
                expectedReturnPct = goal.ExpectedReturnPct,
                horizonDays = goal.HorizonDays,
                themePreference = goal.ThemePreference,
                tradeStyle = tradeStyle.ToString(),
                nextTradingDay = nextTradeDay.ToString("yyyy-MM-dd"),
                queued = true
            })
        };
        _db.AiJobs.Add(job);
        goal.StockPickJobId = job.Id;
        await _db.SaveChangesAsync(ct);

        var queueItem = new AiQueueItem
        {
            TaskType = AiQueueTaskType.GoalToPrompt,
            Status = AiQueueItemStatus.Pending,
            Title = $"目标→提示词 · {styleLabel} · {goal.ThemePreference}",
            CorrelationId = correlationId,
            RelatedGoalId = goal.Id,
            RelatedAiJobId = job.Id,
            InputPayload = goalPayload
        };
        _db.AiQueueItems.Add(queueItem);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Goal {GoalId} queued GoalToPrompt {QueueId}, job {JobId}",
            goal.Id, queueItem.Id, job.Id);

        return (goal, job, queueItem.Id);
    }

    /// <summary>队列消费者：目标 → 选股提示词，成功后入队 StockPick。</summary>
    public async Task ProcessQueuedGoalToPromptAsync(AiQueueItem queueItem, CancellationToken ct = default)
    {
        if (!queueItem.RelatedGoalId.HasValue || !queueItem.RelatedAiJobId.HasValue)
            throw new InvalidOperationException("GoalToPrompt 缺少 GoalId/AiJobId");

        var goal = await _db.InvestmentGoals.FirstOrDefaultAsync(g => g.Id == queueItem.RelatedGoalId, ct)
                   ?? throw new InvalidOperationException("投资目标不存在");
        var job = await _db.AiJobs.FirstOrDefaultAsync(j => j.Id == queueItem.RelatedAiJobId, ct)
                  ?? throw new InvalidOperationException("选股 Job 不存在");

        job.Status = AiJobStatus.Running;
        await _db.SaveChangesAsync(ct);

        string prompt;
        string? rationale;
        string? rawOutput = null;
        try
        {
            rawOutput = await _ai.RunGoalToPromptAsync(queueItem.InputPayload ?? "{}", ct);
            (prompt, rationale) = ParseGoalPromptOutput(rawOutput, goal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Goal→Prompt AI failed, using template prompt");
            prompt = BuildFallbackPrompt(goal);
            rationale = $"AI 生成提示词失败，已用模板：{ex.Message}";
            rawOutput = JsonSerializer.Serialize(new { prompt, rationale, fallback = true, error = ex.Message });
        }

        queueItem.OutputPayload = rawOutput;
        goal.GeneratedPrompt = prompt;
        goal.PromptRationale = rationale;
        job.Prompt = prompt;
        job.InputPayload = JsonSerializer.Serialize(new
        {
            goalId = goal.Id,
            expectedReturnPct = goal.ExpectedReturnPct,
            horizonDays = goal.HorizonDays,
            themePreference = goal.ThemePreference,
            generatedPrompt = prompt
        });
        await _db.SaveChangesAsync(ct);

        // 串联选股任务
        _db.AiQueueItems.Add(new AiQueueItem
        {
            TaskType = AiQueueTaskType.StockPick,
            Status = AiQueueItemStatus.Pending,
            Title = $"选股 · {goal.ThemePreference}",
            CorrelationId = queueItem.CorrelationId,
            RelatedGoalId = goal.Id,
            RelatedAiJobId = job.Id,
            InputPayload = prompt
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>队列消费者：提示词 → 候选股票。</summary>
    public async Task ProcessQueuedStockPickAsync(AiQueueItem queueItem, CancellationToken ct = default)
    {
        if (!queueItem.RelatedGoalId.HasValue || !queueItem.RelatedAiJobId.HasValue)
            throw new InvalidOperationException("StockPick 缺少 GoalId/AiJobId");

        var goal = await _db.InvestmentGoals.FirstOrDefaultAsync(g => g.Id == queueItem.RelatedGoalId, ct)
                   ?? throw new InvalidOperationException("投资目标不存在");
        var job = await _db.AiJobs.FirstOrDefaultAsync(j => j.Id == queueItem.RelatedAiJobId, ct)
                  ?? throw new InvalidOperationException("选股 Job 不存在");

        var prompt = !string.IsNullOrWhiteSpace(queueItem.InputPayload)
            ? queueItem.InputPayload!
            : (goal.GeneratedPrompt ?? BuildFallbackPrompt(goal));

        job.Status = AiJobStatus.Running;
        job.Prompt = prompt;
        await _db.SaveChangesAsync(ct);

        try
        {
            var output = await _ai.RunStockPickAsync(prompt, job.InputPayload, ct);
            // 选股阶段不过硬过滤；入库勾选时再过滤。先落库候选，避免解析后半步异常导致整单失败。
            var count = ApplyStockPickOutput(job, output, _db);
            await _db.SaveChangesAsync(ct);

            if (count == 0)
            {
                _logger.LogWarning("Stock pick returned 0 items for goal {GoalId}, retrying", goal.Id);
                var retryPrompt = BuildForcedStockPickPrompt(goal, prompt);
                job.Prompt = retryPrompt;
                job.InputPayload = JsonSerializer.Serialize(new
                {
                    goalId = goal.Id,
                    expectedReturnPct = goal.ExpectedReturnPct,
                    horizonDays = goal.HorizonDays,
                    themePreference = goal.ThemePreference,
                    generatedPrompt = retryPrompt,
                    retry = true
                });
                output = await _ai.RunStockPickAsync(retryPrompt, job.InputPayload, ct);
                var old = await _db.AiSuggestions.Where(s => s.AiJobId == job.Id).ToListAsync(ct);
                _db.AiSuggestions.RemoveRange(old);
                count = ApplyStockPickOutput(job, output, _db);
                await _db.SaveChangesAsync(ct);
            }

            queueItem.OutputPayload = output;

            if (count == 0)
            {
                job.Status = AiJobStatus.Failed;
                job.ErrorMessage =
                    "选股未返回任何候选股票（items 为空）。请重试或检查 Cursor Agent 输出。";
                job.CompletedAt = DateTime.UtcNow;
                goal.Status = InvestmentGoalStatus.Draft;
                await _db.SaveChangesAsync(ct);
                throw new InvalidOperationException(job.ErrorMessage);
            }

            if (goal.TradeStyle == StockTradeStyle.NextDayRise)
            {
                try
                {
                    var all = await _db.AiSuggestions.Where(s => s.AiJobId == job.Id).ToListAsync(ct);
                    var (_, notes) = await _nextDayPickFilter.FilterSuggestionsAsync(
                        goal, all, strict: false, ct: ct);
                    if (notes.Count > 0)
                        job.AnalysisSummary = (job.AnalysisSummary ?? "") +
                                              $"\n【硬过滤预检，入库时生效】{string.Join("；", notes.Take(6))}";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "NextDay hard-filter preview failed for job {JobId}", job.Id);
                }
            }

            goal.StockPickJobId = job.Id;
            goal.Status = InvestmentGoalStatus.PendingSelection;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (job.Status != AiJobStatus.Failed)
        {
            job.Status = AiJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            goal.Status = InvestmentGoalStatus.Draft;
            queueItem.OutputPayload ??= ex.ToString();
            await _db.SaveChangesAsync(ct);
            throw;
        }
    }

    /// <summary>用户勾选建议股：入库并激活目标。</summary>
    public async Task<(bool Ok, string Message)> AcceptSuggestionsAsync(
        Guid goalId,
        IReadOnlyList<Guid> suggestionIds,
        CancellationToken ct = default)
    {
        var goal = await _db.InvestmentGoals.FirstOrDefaultAsync(g => g.Id == goalId, ct);
        if (goal is null) return (false, "目标不存在");
        if (!goal.StockPickJobId.HasValue) return (false, "目标尚未关联选股任务");
        if (suggestionIds.Count == 0) return (false, "请至少选择一只股票");

        var pool = await _pools.GetOrCreateDefaultPoolAsync(ct);
        var suggestions = await _db.AiSuggestions
            .Where(s => s.AiJobId == goal.StockPickJobId && suggestionIds.Contains(s.Id))
            .ToListAsync(ct);
        if (suggestions.Count == 0) return (false, "未找到选中的建议");

        IReadOnlyList<AiSuggestion> toAccept = suggestions;
        IReadOnlyList<string> rejectNotes = Array.Empty<string>();
        if (goal.TradeStyle == StockTradeStyle.NextDayRise)
        {
            // 入库用「路径/追高」过滤；confidence 文本已兼容 medium 等，避免误杀
            var filtered = await _nextDayPickFilter.FilterSuggestionsAsync(
                goal, suggestions, strict: true, ct: ct);
            toAccept = filtered.Accepted;
            rejectNotes = filtered.RejectNotes;
            if (toAccept.Count == 0)
            {
                // 仍允许用户强行入库：回退为原勾选，但提示风险
                toAccept = suggestions;
                rejectNotes = rejectNotes.Concat(new[]
                {
                    "硬过滤未通过，已按你的勾选强制入库，请自行甄别风险"
                }).ToList();
            }
        }

        var added = 0;
        foreach (var s in toAccept)
        {
            var symbol = s.Symbol;
            var name = s.Name ?? symbol;
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            var tags = goal.TradeStyle switch
            {
                StockTradeStyle.NextDayRise => "AI目标选股,次日上涨",
                StockTradeStyle.TrendSwing => "AI目标选股,趋势",
                _ => "AI目标选股,做T"
            };
            // 池内保留 NextDayRise，便于分组；引擎仍按 IsTrendLike 处理
            var (item, _) = await _pools.AddItemSmartAsync(
                pool.Id, symbol, name, tags, PoolItemSource.Ai, goal.TradeStyle, ct);
            if (item is null) continue;

            var exists = await _db.InvestmentGoalStocks.AnyAsync(
                x => x.InvestmentGoalId == goalId && x.Symbol == item.Symbol, ct);
            if (!exists)
            {
                var quote = await _quotes.GetQuoteAsync(item.Symbol, item.Name, ct);
                var targetDay = goal.TradeStyle == StockTradeStyle.NextDayRise
                    ? (item.PredictedRiseDate ?? PriceForecastMetrics.NextTradingDay(DateTime.Today))
                    : (DateTime?)null;
                _db.InvestmentGoalStocks.Add(new InvestmentGoalStock
                {
                    InvestmentGoalId = goalId,
                    Symbol = item.Symbol,
                    Name = item.Name,
                    PoolItemId = item.Id,
                    SuggestionId = s.Id,
                    BaselineCash = item.InitialCash > 0 ? item.InitialCash : DefaultBaselineCash,
                    BaselinePrice = quote?.Last,
                    TargetTradeDate = targetDay
                });
            }

            s.IsAccepted = true;
            s.ReviewedAt = DateTime.UtcNow;
            added++;
        }

        var job = await _db.AiJobs.FindAsync([goal.StockPickJobId.Value], ct);
        if (job is not null)
        {
            job.Status = AiJobStatus.Accepted;
            job.CompletedAt ??= DateTime.UtcNow;
        }

        goal.Status = InvestmentGoalStatus.Active;
        goal.StartDate ??= DateTime.Today;
        goal.TargetEndDate ??= DateTime.Today.AddDays(goal.HorizonDays);
        await _db.SaveChangesAsync(ct);

        // 立即记一笔 Day0 进度
        await SettleGoalAsync(goal.Id, DateTime.Today, ct);
        if (goal.TradeStyle == StockTradeStyle.NextDayRise)
            await EvaluateNextDayPicksAsync(goal.Id, DateTime.Now, ct);

        var msg = $"已入选 {added} 只股票并开始跟踪目标达成率";
        if (rejectNotes.Count > 0)
            msg += "；硬过滤跳过：" + string.Join("；", rejectNotes.Take(4));
        return (true, msg);
    }

    public async Task SettleAllActiveAsync(DateTime tradeDate, CancellationToken ct = default)
    {
        var goals = await _db.InvestmentGoals
            .Where(g => g.Status == InvestmentGoalStatus.Active
                        || (g.Status == InvestmentGoalStatus.Completed
                            && g.TradeStyle == StockTradeStyle.NextDayRise))
            .Select(g => new { g.Id, g.Status })
            .ToListAsync(ct);
        foreach (var g in goals)
        {
            try
            {
                if (g.Status == InvestmentGoalStatus.Active)
                    await SettleGoalAsync(g.Id, tradeDate.Date, ct);
                await EvaluateNextDayPicksAsync(g.Id, DateTime.Now, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Settle goal {GoalId} failed", g.Id);
            }
        }
    }

    /// <summary>
    /// 次日上涨预选对账：目标日收盘后用真源日K收盘 vs 基准价，写方向/目标命中。
    /// </summary>
    public async Task EvaluateNextDayPicksAsync(Guid goalId, DateTime localNow, CancellationToken ct = default)
    {
        var goal = await _db.InvestmentGoals.FirstOrDefaultAsync(g => g.Id == goalId, ct);
        if (goal is null || goal.TradeStyle != StockTradeStyle.NextDayRise) return;

        var stocks = await _db.InvestmentGoalStocks
            .Where(s => s.InvestmentGoalId == goalId && s.TargetTradeDate != null)
            .ToListAsync(ct);
        if (stocks.Count == 0) return;

        var newlyEvaluated = new List<Guid>();
        foreach (var gs in stocks)
        {
            var target = gs.TargetTradeDate!.Value.Date;
            if (!PriceForecastMetrics.CanFinalizeForecast(target, localNow))
                continue;
            if (gs.EvaluatedAt.HasValue
                && gs.ActualClose.HasValue
                && gs.NextDayReturnPct.HasValue)
                continue;
            if (gs.BaselinePrice is null or <= 0)
                continue;

            StockDetailBundle? detail;
            try
            {
                detail = await _detailProvider.GetDetailAsync(gs.Symbol, gs.Name, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NextDay evaluate detail failed for {Symbol}", gs.Symbol);
                continue;
            }

            if (detail is null || detail.IsSimulated) continue;
            var bar = detail.DailyBars.FirstOrDefault(d => d.Date.Date == target);
            if (bar is null || bar.Close <= 0) continue;

            var (ret, dirHit, tgtHit) = NextDayPickMetrics.Evaluate(
                gs.BaselinePrice.Value, bar.Close, goal.ExpectedReturnPct);
            gs.ActualClose = bar.Close;
            gs.NextDayReturnPct = ret;
            gs.DirectionHit = dirHit;
            gs.TargetHit = tgtHit;
            gs.EvaluatedAt = localNow;
            newlyEvaluated.Add(gs.Id);
        }

        if (newlyEvaluated.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            foreach (var id in newlyEvaluated)
            {
                try
                {
                    await _nextDayPickReviews.EnqueueFromGoalStockAsync(id, force: false, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Enqueue NextDayPickReview failed for goalStock {Id}", id);
                }
            }
        }
    }

    public async Task SettleGoalAsync(Guid goalId, DateTime tradeDate, CancellationToken ct = default)
    {
        var goal = await _db.InvestmentGoals.FirstOrDefaultAsync(g => g.Id == goalId, ct);
        if (goal is null || goal.Status != InvestmentGoalStatus.Active) return;

        var stocks = await _db.InvestmentGoalStocks.Where(s => s.InvestmentGoalId == goalId).ToListAsync(ct);
        if (stocks.Count == 0) return;

        var symbols = stocks.Select(s => s.Symbol).ToList();
        var poolItems = await _db.StockPoolItems
            .Where(i => symbols.Contains(i.Symbol))
            .ToListAsync(ct);
        var positions = await _db.Positions.Where(p => symbols.Contains(p.Symbol)).ToListAsync(ct);
        var quotes = await _quotes.GetQuotesAsync(symbols, ct);

        // 自目标启动日起该股累计已实现（卖出腿近似）
        var start = goal.StartDate?.Date ?? tradeDate.Date;
        var trades = await _db.Trades
            .Where(t => symbols.Contains(t.Symbol) && t.TradeDate >= start && t.TradeDate <= tradeDate.Date)
            .ToListAsync(ct);

        decimal totalBaseline = 0;
        decimal totalValue = 0;
        decimal realized = 0;
        decimal unrealized = 0;

        foreach (var gs in stocks)
        {
            totalBaseline += gs.BaselineCash;
            var item = poolItems.FirstOrDefault(i =>
                string.Equals(i.Symbol, gs.Symbol, StringComparison.OrdinalIgnoreCase));
            var pos = positions.FirstOrDefault(p =>
                string.Equals(p.Symbol, gs.Symbol, StringComparison.OrdinalIgnoreCase));
            quotes.TryGetValue(gs.Symbol, out var q);
            var last = q?.Last ?? gs.BaselinePrice ?? 0;
            var qty = pos?.Quantity ?? 0;
            var avg = pos?.AvgCost ?? 0;
            var cash = item?.AvailableCash ?? gs.BaselineCash;
            var mv = cash + last * qty;
            totalValue += mv;
            unrealized += qty > 0 && last > 0 ? (last - avg) * qty : 0;

            foreach (var t in trades.Where(t => string.Equals(t.Symbol, gs.Symbol, StringComparison.OrdinalIgnoreCase)))
            {
                if (t.Side == OrderSide.Sell)
                    realized += (t.Price - avg) * t.Quantity - t.TotalFee;
                else
                    realized -= t.TotalFee;
            }
        }

        if (totalBaseline <= 0) totalBaseline = stocks.Count * DefaultBaselineCash;
        var portfolioReturn = (totalValue - totalBaseline) / totalBaseline * 100m;
        var achievement = goal.ExpectedReturnPct == 0
            ? 0
            : portfolioReturn / goal.ExpectedReturnPct * 100m;

        var dayIndex = goal.StartDate.HasValue
            ? Math.Max(0, (tradeDate.Date - goal.StartDate.Value.Date).Days)
            : 0;

        var row = await _db.GoalProgressDailies
            .FirstOrDefaultAsync(p => p.InvestmentGoalId == goalId && p.TradeDate == tradeDate.Date, ct);
        if (row is null)
        {
            row = new GoalProgressDaily
            {
                InvestmentGoalId = goalId,
                TradeDate = tradeDate.Date
            };
            _db.GoalProgressDailies.Add(row);
        }

        row.PortfolioReturnPct = Math.Round(portfolioReturn, 4);
        row.AchievementRate = Math.Round(achievement, 4);
        row.TotalMarketValue = Math.Round(totalValue, 2);
        row.TotalBaseline = Math.Round(totalBaseline, 2);
        row.RealizedPnL = Math.Round(realized, 2);
        row.UnrealizedPnL = Math.Round(unrealized, 2);
        row.DayIndex = dayIndex;
        row.Notes = $"等权每股基准资金；第 {dayIndex}/{goal.HorizonDays} 天";

        if (goal.TargetEndDate.HasValue && tradeDate.Date >= goal.TargetEndDate.Value.Date)
        {
            goal.Status = InvestmentGoalStatus.Completed;
            row.Notes += "；周期已到期";
        }
        else if (achievement >= 100m && portfolioReturn >= goal.ExpectedReturnPct)
        {
            row.Notes += "；已达预期收益";
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <returns>有效候选条数（含合法 symbol）。</returns>
    private static int ApplyStockPickOutput(AiJob job, string output, IStockSimDataStore db)
    {
        job.OutputPayload = output;
        job.Status = AiJobStatus.SucceededPendingReview;
        job.CompletedAt = DateTime.UtcNow;

        if (!TryGetStockPickItems(output, out var analysis, out var stepsJson, out var itemJsons))
        {
            job.AnalysisSummary = "选股输出无法解析为 JSON，请重试。";
            return 0;
        }

        job.AnalysisSummary = analysis ?? "已生成候选股票，请勾选确认。";
        job.AnalysisStepsJson = stepsJson;

        var count = 0;
        foreach (var itemJson in itemJsons)
        {
            using var doc = JsonDocument.Parse(itemJson);
            var sug = AiSuggestionFactory.FromItem(job.Id, doc.RootElement);
            if (string.IsNullOrWhiteSpace(sug.Symbol)) continue;
            // Payload 必须在 document 存活时取走
            sug.PayloadJson = itemJson;
            db.AiSuggestions.Add(sug);
            count++;
        }

        if (count == 0 && itemJsons.Count > 0)
            job.AnalysisSummary = (job.AnalysisSummary ?? "") +
                                  $"（items={itemJsons.Count} 但无有效 symbol，请检查输出格式）";

        return count;
    }

    /// <summary>容忍 markdown / 杂质；用 JsonNode 提取 items JSON 字符串列表。</summary>
    private static bool TryGetStockPickItems(
        string? output,
        out string? analysis,
        out string? stepsJson,
        out List<string> itemJsons)
    {
        analysis = null;
        stepsJson = null;
        itemJsons = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return false;

        var text = output.Trim();
        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = text.IndexOf('{', fence);
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
                text = text[start..(end + 1)];
        }
        else
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
                text = text[start..(end + 1)];
        }

        try
        {
            var node = JsonNode.Parse(text) as JsonObject;
            if (node is null) return false;

            analysis = node["analysis"]?.GetValue<string>();
            if (node["steps"] is JsonArray steps)
                stepsJson = steps.ToJsonString();

            var itemsNode = node["items"]
                            ?? node["data"]?["items"]
                            ?? node["result"]?["items"];
            if (itemsNode is not JsonArray arr)
                return true;

            foreach (var el in arr)
            {
                if (el is null) continue;
                itemJsons.Add(el.ToJsonString());
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (string Prompt, string? Rationale) ParseGoalPromptOutput(string output, InvestmentGoal goal)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "{}" : output);
        var root = doc.RootElement;
        var prompt = root.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
        var rationale = root.TryGetProperty("rationale", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : root.TryGetProperty("analysis", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = BuildFallbackPrompt(goal);
        else
            prompt = SanitizeStockPickPrompt(prompt!, goal);
        return (prompt!, rationale);
    }

    /// <summary>
    /// 去掉「禁止列代码」类错误约束，并确保提示词包含目标与「必须输出具体股票」。
    /// </summary>
    private static string SanitizeStockPickPrompt(string prompt, InvestmentGoal goal)
    {
        var cleaned = prompt;
        string[] banPhrases =
        [
            "不要输出具体股票代码列表",
            "不要输出股票代码列表",
            "不要输出股票代码",
            "不要列股票代码",
            "不要列代码",
            "禁止输出代码列表",
            "禁止输出股票代码",
            "只输出选股逻辑与筛选条件",
            "只输出选股逻辑",
            "只给行业方向",
            "本步『只生成提示词』",
            "本步只生成提示词"
        ];
        foreach (var phrase in banPhrases)
            cleaned = cleaned.Replace(phrase, "", StringComparison.OrdinalIgnoreCase);

        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\n{3,}", "\n\n").Trim();

        var theme = goal.ThemePreference.Trim();
        var hasReturn = cleaned.Contains(goal.ExpectedReturnPct.ToString("0.##"), StringComparison.Ordinal)
                        || cleaned.Contains($"{goal.ExpectedReturnPct}%", StringComparison.Ordinal);
        var hasDays = cleaned.Contains(goal.HorizonDays.ToString(), StringComparison.Ordinal);
        var hasTheme = string.IsNullOrEmpty(theme)
                       || cleaned.Contains(theme, StringComparison.OrdinalIgnoreCase);
        var asksSymbols = cleaned.Contains("symbol", StringComparison.OrdinalIgnoreCase)
                          || cleaned.Contains("股票代码", StringComparison.Ordinal)
                          || cleaned.Contains("具体股票", StringComparison.Ordinal)
                          || cleaned.Contains("推荐", StringComparison.Ordinal);

        var parts = new List<string> { cleaned };
        if (!hasReturn || !hasDays || !hasTheme)
        {
            parts.Add(
                $"【投资目标硬约束】预期收益约 {goal.ExpectedReturnPct}%；预期周期约 {goal.HorizonDays} 个交易日；题材偏好「{theme}」。");
        }

        if (goal.TradeStyle == StockTradeStyle.NextDayRise)
        {
            parts.Add(
                "【选股类型】次日上涨预选：综合当日行情与量能、近史强弱、题材偏好、近期新闻与热点，" +
                "预选下一交易日更可能上涨的具体 A 股；不做日内高频做T；" +
                $"隔日温和目标约 {goal.ExpectedReturnPct}%（已钳制≤{NextDayPickGuard.MaxExpectedReturnPct}%）；" +
                "主 KPI 是方向命中与相对创业板指超额，不是冲涨停。");
            parts.Add(
                "【复盘约束】须消化 recentNextDayPickReviews 与 nextDayPickCalibration；" +
                "避免 ChaseHigh/WeakRelative/ThemeFade/GapDownOpen；同行业建议≤2 只；" +
                "items 必须含 symbol,name,reason,risk，并尽量含 thesis,catalystType,confidence(0-1),invalidation；" +
                "即使把握一般也须输出 3～5 只具体代码，禁止只写分析不给 items。");
        }
        else if (goal.TradeStyle == StockTradeStyle.TrendSwing)
        {
            parts.Add(
                "【选股类型】趋势股：优先底部反转/中期趋势初成标的；不做日内高频做T；适合观察跟踪。");
        }
        else
        {
            parts.Add(
                "【选股类型】做T股：优先流动性好、波动适中、适合日内高抛低吸的标的。");
        }

        if (!asksSymbols || banPhrases.Any(p => prompt.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(
                "【强制输出】请推荐 3~5 只真实可交易的 A 股，必须给出 6 位股票代码与简称；" +
                "以 JSON 的 items[{symbol,name,reason,risk}] 返回，禁止只写行业/主题而不给具体代码。");
        }

        return string.Join("\n\n", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string BuildFallbackPrompt(InvestmentGoal goal)
    {
        if (goal.TradeStyle == StockTradeStyle.NextDayRise)
        {
            return
                $"请推荐 3~5 只下一交易日更可能上涨的具体 A 股（必须含 6 位代码与简称）。" +
                $"题材偏好「{goal.ThemePreference}」；隔日温和目标约 {goal.ExpectedReturnPct}%（≤{NextDayPickGuard.MaxExpectedReturnPct}%）；" +
                "主目标：收涨且相对创业板指不弱；请消化 recentNextDayPickReviews 与 nextDayPickCalibration；" +
                "排除 ST、明显追高与极弱相对强弱；同行业建议不超过 2 只；" +
                "必须输出 3～5 只具体代码（禁止空 items）；字段含 symbol,name,reason,risk，并尽量含 thesis,catalystType,confidence,invalidation；" +
                "返回 JSON：analysis、items[{symbol,name,reason,risk,thesis,catalystType,confidence,invalidation}]。";
        }

        if (goal.TradeStyle == StockTradeStyle.TrendSwing)
        {
            return
                $"请推荐 3~5 只适合 A 股中期趋势/底部反转观察的具体股票（必须含 6 位代码与简称）。" +
                $"题材偏好「{goal.ThemePreference}」；希望约 {goal.HorizonDays} 个交易日内组合收益接近 {goal.ExpectedReturnPct}%；" +
                "优先基本面改善、底部结构清晰或趋势初成、非 ST；不做日内高频做T；" +
                "返回 JSON：analysis、items[{symbol,name,reason,risk}]。";
        }

        return
            $"请推荐 3~5 只适合 A 股模拟日内做T的具体股票（必须含 6 位代码与简称）。" +
            $"题材偏好「{goal.ThemePreference}」；希望约 {goal.HorizonDays} 个交易日内组合收益接近 {goal.ExpectedReturnPct}%；" +
            "优先流动性好、波动适中、非 ST；返回 JSON：analysis、items[{symbol,name,reason,risk}]。";
    }

    private async Task<object?> BuildMarketContextAsync(
        StockTradeStyle tradeStyle,
        DateTime nextTradingDay,
        CancellationToken ct)
    {
        if (tradeStyle != StockTradeStyle.NextDayRise) return null;
        try
        {
            var news = await _news.GetHotNewsAsync(12, ct);
            return new
            {
                nextTradingDay = nextTradingDay.ToString("yyyy-MM-dd"),
                guidance =
                    "请结合热点新闻、题材、强弱，以及 recentNextDayPickReviews / nextDayPickCalibration：" +
                    "主 KPI=方向命中+相对强弱；预期涨幅已钳制为温和隔日目标；" +
                    "须消化错因周统计、弱题材列表与 pickHints；勿重复 ChaseHigh/WeakRelative/ThemeFade/GapDownOpen。",
                hotNews = news.Select(n => new
                {
                    title = n.Title,
                    summary = n.Summary,
                    source = n.Source,
                    publishedAt = n.PublishedAt
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BuildMarketContext news failed");
            return new
            {
                nextTradingDay = nextTradingDay.ToString("yyyy-MM-dd"),
                guidance =
                    "新闻源暂不可用；请仍结合题材、公开市场常识与 recentNextDayPickReviews 失败模式预选次日偏多个股。"
            };
        }
    }

    private static string BuildForcedStockPickPrompt(InvestmentGoal goal, string previousPrompt) =>
        BuildFallbackPrompt(goal) +
        "\n\n（系统重试）上一轮未返回 items 股票列表。请忽略任何「不要输出代码」的表述，必须输出具体代码。" +
        "\n参考约束摘要：\n" + Truncate(previousPrompt, 800);

    private static string Truncate(string? s, int n)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s.Length <= n ? s : s[..n] + "…";
    }

    private static InvestmentGoalDto MapGoal(
        InvestmentGoal g,
        int stockCount,
        decimal? achievement,
        decimal? portfolioReturn,
        IReadOnlyList<InvestmentGoalStock>? stocks = null,
        decimal? avgRelativeStrengthPct = null)
    {
        decimal? dirRate = null;
        decimal? tgtRate = null;
        decimal? avgRet = null;
        if (stocks is { Count: > 0 })
        {
            var scored = stocks.Where(s => s.EvaluatedAt.HasValue && s.NextDayReturnPct.HasValue).ToList();
            if (scored.Count > 0)
            {
                dirRate = Math.Round(100m * scored.Count(s => s.DirectionHit == true) / scored.Count, 1);
                // 目标命中为弹性指标；方向命中 + 相对强弱才是主 KPI
                tgtRate = Math.Round(100m * scored.Count(s => s.TargetHit == true) / scored.Count, 1);
                avgRet = Math.Round(scored.Average(s => s.NextDayReturnPct!.Value), 2);
            }
        }

        return new(
            g.Id, g.ExpectedReturnPct, g.HorizonDays, g.ThemePreference, g.GeneratedPrompt, g.PromptRationale,
            g.Status, g.StartDate, g.TargetEndDate, g.StockPickJobId, stockCount, achievement, portfolioReturn,
            g.CreationTime, g.TradeStyle, dirRate, tgtRate, avgRet, avgRelativeStrengthPct);
    }
}
