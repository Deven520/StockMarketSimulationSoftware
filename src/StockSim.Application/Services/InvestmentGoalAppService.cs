using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockSim.Application.Dtos;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;

namespace StockSim.Application.Services;

/// <summary>投资目标：AI 两段选股、入选跟踪、每日达成率。</summary>
public class InvestmentGoalAppService
{
    public const decimal DefaultBaselineCash = 200_000m;

    private readonly IStockSimDataStore _db;
    private readonly ICursorAiClient _ai;
    private readonly StockPoolAppService _pools;
    private readonly CachedLightQuoteService _quotes;
    private readonly ILogger<InvestmentGoalAppService> _logger;

    public InvestmentGoalAppService(
        IStockSimDataStore db,
        ICursorAiClient ai,
        StockPoolAppService pools,
        CachedLightQuoteService quotes,
        ILogger<InvestmentGoalAppService> logger)
    {
        _db = db;
        _ai = ai;
        _pools = pools;
        _quotes = quotes;
        _logger = logger;
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

        return goals.Select(g =>
        {
            latestMap.TryGetValue(g.Id, out var p);
            stockCounts.TryGetValue(g.Id, out var cnt);
            return MapGoal(g, cnt, p?.AchievementRate, p?.PortfolioReturnPct);
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
        return new InvestmentGoalDetailDto(
            MapGoal(g, stocks.Count, latest?.AchievementRate, latest?.PortfolioReturnPct),
            stocks.Select(s =>
            {
                AiSuggestion? sug = null;
                if (s.SuggestionId.HasValue) suggestionMap.TryGetValue(s.SuggestionId.Value, out sug);
                return new InvestmentGoalStockDto(
                    s.Id, s.Symbol, s.Name, s.PoolItemId, s.BaselineCash,
                    sug?.SelectionLogic ?? sug?.Reason,
                    sug?.Reason,
                    sug?.Risk);
            }).ToList(),
            progress.Select(p => new GoalProgressDailyDto(
                p.TradeDate, p.PortfolioReturnPct, p.AchievementRate,
                p.TotalMarketValue, p.TotalBaseline, p.RealizedPnL, p.UnrealizedPnL, p.DayIndex, p.Notes)).ToList());
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

        var goal = new InvestmentGoal
        {
            ExpectedReturnPct = request.ExpectedReturnPct,
            HorizonDays = request.HorizonDays,
            ThemePreference = request.ThemePreference.Trim(),
            Status = InvestmentGoalStatus.Draft
        };
        _db.InvestmentGoals.Add(goal);
        await _db.SaveChangesAsync(ct);

        var correlationId = Guid.NewGuid();
        var goalPayload = JsonSerializer.Serialize(new
        {
            expectedReturnPct = goal.ExpectedReturnPct,
            horizonDays = goal.HorizonDays,
            themePreference = goal.ThemePreference,
            asOf = DateTime.Now
        });

        var job = new AiJob
        {
            JobType = AiJobType.StockPick,
            Status = AiJobStatus.Created,
            Prompt = $"（排队中）题材「{goal.ThemePreference}」· 预期 {goal.ExpectedReturnPct}% / {goal.HorizonDays} 天",
            InputPayload = JsonSerializer.Serialize(new
            {
                goalId = goal.Id,
                expectedReturnPct = goal.ExpectedReturnPct,
                horizonDays = goal.HorizonDays,
                themePreference = goal.ThemePreference,
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
            Title = $"目标→提示词 · {goal.ThemePreference}",
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
            var count = ApplyStockPickOutput(job, output, _db);

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
                // 清掉可能的空建议后重写
                var old = await _db.AiSuggestions.Where(s => s.AiJobId == job.Id).ToListAsync(ct);
                _db.AiSuggestions.RemoveRange(old);
                count = ApplyStockPickOutput(job, output, _db);
            }

            queueItem.OutputPayload = output;

            if (count == 0)
            {
                job.Status = AiJobStatus.Failed;
                job.ErrorMessage = "选股未返回任何候选股票（items 为空）。请重试或检查 Cursor Agent 输出。";
                job.CompletedAt = DateTime.UtcNow;
                goal.Status = InvestmentGoalStatus.Draft;
                await _db.SaveChangesAsync(ct);
                throw new InvalidOperationException(job.ErrorMessage);
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

        var added = 0;
        foreach (var s in suggestions)
        {
            var symbol = s.Symbol;
            var name = s.Name ?? symbol;
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            var (item, _) = await _pools.AddItemSmartAsync(pool.Id, symbol, name, "AI目标选股", PoolItemSource.Ai, ct);
            if (item is null) continue;

            var exists = await _db.InvestmentGoalStocks.AnyAsync(
                x => x.InvestmentGoalId == goalId && x.Symbol == item.Symbol, ct);
            if (!exists)
            {
                var quote = await _quotes.GetQuoteAsync(item.Symbol, item.Name, ct);
                _db.InvestmentGoalStocks.Add(new InvestmentGoalStock
                {
                    InvestmentGoalId = goalId,
                    Symbol = item.Symbol,
                    Name = item.Name,
                    PoolItemId = item.Id,
                    SuggestionId = s.Id,
                    BaselineCash = item.InitialCash > 0 ? item.InitialCash : DefaultBaselineCash,
                    BaselinePrice = quote?.Last
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

        return (true, $"已入选 {added} 只股票并开始跟踪目标达成率");
    }

    public async Task SettleAllActiveAsync(DateTime tradeDate, CancellationToken ct = default)
    {
        var ids = await _db.InvestmentGoals
            .Where(g => g.Status == InvestmentGoalStatus.Active)
            .Select(g => g.Id)
            .ToListAsync(ct);
        foreach (var id in ids)
        {
            try
            {
                await SettleGoalAsync(id, tradeDate.Date, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Settle goal {GoalId} failed", id);
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
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "{}" : output);
        var root = doc.RootElement;
        job.AnalysisSummary = root.TryGetProperty("analysis", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()
            : "已生成候选股票，请勾选确认。";
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            job.AnalysisStepsJson = steps.GetRawText();

        var count = 0;
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var sug = AiSuggestionFactory.FromItem(job.Id, item);
                if (string.IsNullOrWhiteSpace(sug.Symbol)) continue;
                db.AiSuggestions.Add(sug);
                count++;
            }
        }

        return count;
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

        if (!asksSymbols || banPhrases.Any(p => prompt.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(
                "【强制输出】请推荐 3~5 只真实可交易的 A 股，必须给出 6 位股票代码与简称；" +
                "以 JSON 的 items[{symbol,name,reason,risk}] 返回，禁止只写行业/主题而不给具体代码。");
        }

        return string.Join("\n\n", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string BuildFallbackPrompt(InvestmentGoal goal) =>
        $"请推荐 3~5 只适合 A 股模拟日内做T的具体股票（必须含 6 位代码与简称）。" +
        $"题材偏好「{goal.ThemePreference}」；希望约 {goal.HorizonDays} 个交易日内组合收益接近 {goal.ExpectedReturnPct}%；" +
        "优先流动性好、波动适中、非 ST；返回 JSON：analysis、items[{symbol,name,reason,risk}]。";

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
        decimal? portfolioReturn) =>
        new(g.Id, g.ExpectedReturnPct, g.HorizonDays, g.ThemePreference, g.GeneratedPrompt, g.PromptRationale,
            g.Status, g.StartDate, g.TargetEndDate, g.StockPickJobId, stockCount, achievement, portfolioReturn,
            g.CreationTime);
}
