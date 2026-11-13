using System.Text;
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

/// <summary>
/// 次日预涨选股复盘：结算后入队 → AI 错因/hints → 供 GoalToPrompt/StockPick 注入。
/// </summary>
public sealed class NextDayPickReviewAppService
{
    public const string IndexSymbol = "399006";

    private readonly IStockSimDataStore _db;
    private readonly ICursorAiClient _ai;
    private readonly IStockDetailProvider _detailProvider;
    private readonly ILogger<NextDayPickReviewAppService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public NextDayPickReviewAppService(
        IStockSimDataStore db,
        ICursorAiClient ai,
        IStockDetailProvider detailProvider,
        ILogger<NextDayPickReviewAppService> logger)
    {
        _db = db;
        _ai = ai;
        _detailProvider = detailProvider;
        _logger = logger;
    }

    /// <summary>为已对账的目标入选股入队复盘。</summary>
    public async Task<(bool Ok, string Message, Guid? QueueId)> EnqueueFromGoalStockAsync(
        Guid goalStockId,
        bool force = false,
        CancellationToken ct = default)
    {
        var gs = await _db.InvestmentGoalStocks.FirstOrDefaultAsync(s => s.Id == goalStockId, ct);
        if (gs is null) return (false, "入选股不存在", null);
        if (gs.TargetTradeDate is null) return (false, "无目标交易日", null);
        if (gs.BaselinePrice is null or <= 0 || gs.ActualClose is null or <= 0
            || gs.NextDayReturnPct is null || !gs.EvaluatedAt.HasValue)
            return (false, "尚未完成次日对账，无法复盘", null);

        var goal = await _db.InvestmentGoals.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == gs.InvestmentGoalId, ct);

        return await EnqueueCoreAsync(
            symbol: gs.Symbol,
            name: gs.Name,
            target: gs.TargetTradeDate.Value.Date,
            baseline: gs.BaselinePrice.Value,
            actualClose: gs.ActualClose.Value,
            returnPct: gs.NextDayReturnPct.Value,
            directionHit: gs.DirectionHit == true,
            targetHit: gs.TargetHit == true,
            expectedReturnPct: goal?.ExpectedReturnPct ?? 0m,
            themePreference: goal?.ThemePreference,
            investmentGoalId: gs.InvestmentGoalId,
            goalStockId: gs.Id,
            stockPoolItemId: gs.PoolItemId,
            force: force,
            ct: ct);
    }

    /// <summary>为池内已结算次日预涨成分入队复盘。</summary>
    public async Task<(bool Ok, string Message, Guid? QueueId)> EnqueueFromPoolItemAsync(
        Guid poolItemId,
        bool force = false,
        CancellationToken ct = default)
    {
        var item = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Id == poolItemId, ct);
        if (item is null) return (false, "成分不存在", null);
        if (item.TradeStyle != StockTradeStyle.NextDayRise)
            return (false, "仅次日预涨股可复盘", null);
        if (item.PredictedRiseDate is null
            || item.RiseBaselinePrice is null or <= 0
            || item.RiseActualClose is null or <= 0
            || item.RiseReturnPct is null
            || !item.RiseEvaluatedAt.HasValue)
            return (false, "尚未完成次日结算，无法复盘", null);

        var target = item.PredictedRiseDate.Value.Date;
        var goalStock = await _db.InvestmentGoalStocks.AsNoTracking()
            .Where(s => s.Symbol == item.Symbol && s.TargetTradeDate == target)
            .OrderByDescending(s => s.CreationTime)
            .FirstOrDefaultAsync(ct);
        InvestmentGoal? goal = null;
        if (goalStock is not null)
            goal = await _db.InvestmentGoals.AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == goalStock.InvestmentGoalId, ct);

        var dirHit = item.RiseSuccess == true;
        var expected = goal?.ExpectedReturnPct ?? 0m;
        var tgtHit = NextDayPickMetrics.TargetHit(item.RiseReturnPct.Value, expected);

        return await EnqueueCoreAsync(
            symbol: item.Symbol,
            name: item.Name,
            target: target,
            baseline: item.RiseBaselinePrice.Value,
            actualClose: item.RiseActualClose.Value,
            returnPct: item.RiseReturnPct.Value,
            directionHit: dirHit,
            targetHit: tgtHit,
            expectedReturnPct: expected,
            themePreference: goal?.ThemePreference,
            investmentGoalId: goal?.Id,
            goalStockId: goalStock?.Id,
            stockPoolItemId: item.Id,
            force: force,
            ct: ct);
    }

    /// <summary>
    /// 对指定目标日：已对账的目标入选股 + 池内已结算项入队复盘。
    /// targetDate 为空时扫描近 lookbackDays 内所有已结算日。
    /// </summary>
    public async Task<(int Queued, int SkippedOrDone, int Total)> EnqueueSettledForDateAsync(
        DateTime? targetDate = null,
        bool force = false,
        int lookbackDays = 14,
        CancellationToken ct = default)
    {
        if (targetDate.HasValue)
            return await EnqueueSettledForSingleDateAsync(targetDate.Value.Date, force, ct);

        var since = DateTime.Today.AddDays(-Math.Clamp(lookbackDays, 1, 60));
        var poolDays = await _db.StockPoolItems.AsNoTracking()
            .Where(i => i.TradeStyle == StockTradeStyle.NextDayRise
                        && i.PredictedRiseDate != null
                        && i.PredictedRiseDate >= since
                        && i.RiseEvaluatedAt != null
                        && i.RiseActualClose != null
                        && i.RiseReturnPct != null)
            .Select(i => i.PredictedRiseDate!.Value)
            .Distinct()
            .ToListAsync(ct);
        var goalDays = await _db.InvestmentGoalStocks.AsNoTracking()
            .Where(s => s.TargetTradeDate != null
                        && s.TargetTradeDate >= since
                        && s.EvaluatedAt != null
                        && s.ActualClose != null
                        && s.NextDayReturnPct != null)
            .Select(s => s.TargetTradeDate!.Value)
            .Distinct()
            .ToListAsync(ct);

        var days = poolDays.Concat(goalDays).Select(d => d.Date).Distinct().OrderByDescending(d => d).ToList();
        var queued = 0;
        var skippedOrDone = 0;
        var total = 0;
        foreach (var d in days)
        {
            var (q, s, t) = await EnqueueSettledForSingleDateAsync(d, force, ct);
            queued += q;
            skippedOrDone += s;
            total += t;
        }

        return (queued, skippedOrDone, total);
    }

    private async Task<(int Queued, int SkippedOrDone, int Total)> EnqueueSettledForSingleDateAsync(
        DateTime day,
        bool force,
        CancellationToken ct)
    {
        day = day.Date;
        var queued = 0;
        var skippedOrDone = 0;
        var total = 0;

        // 池已结算但 Goal 未写对账时，先回填，便于优先走 Goal 路径
        await TryBackfillGoalEvalFromPoolAsync(day, ct);

        var goalStocks = await _db.InvestmentGoalStocks
            .Where(s => s.TargetTradeDate == day
                        && s.EvaluatedAt != null
                        && s.ActualClose != null
                        && s.NextDayReturnPct != null)
            .Select(s => new { s.Id, s.Symbol })
            .ToListAsync(ct);

        // 仅把「已成功按 Goal 入队/已有复盘」的标的视为 covered；未对账 Goal 不再挡池路径
        var coveredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gs in goalStocks)
        {
            total++;
            var (ok, _, qid) = await EnqueueFromGoalStockAsync(gs.Id, force, ct);
            if (ok && qid.HasValue) queued++;
            else if (ok) skippedOrDone++;
            if (ok) coveredSet.Add(gs.Symbol);
        }

        var poolItems = await _db.StockPoolItems
            .Where(i => i.TradeStyle == StockTradeStyle.NextDayRise
                        && i.PredictedRiseDate == day
                        && i.RiseEvaluatedAt != null
                        && i.RiseActualClose != null
                        && i.RiseReturnPct != null)
            .Select(i => new { i.Id, i.Symbol })
            .ToListAsync(ct);

        foreach (var p in poolItems)
        {
            if (coveredSet.Contains(p.Symbol)) continue;
            total++;
            var (ok, _, qid) = await EnqueueFromPoolItemAsync(p.Id, force, ct);
            if (ok && qid.HasValue) queued++;
            else if (ok) skippedOrDone++;
        }

        return (queued, skippedOrDone, total);
    }

    /// <summary>用池内 Rise* 回填同日尚未对账的 Goal 入选股。</summary>
    private async Task TryBackfillGoalEvalFromPoolAsync(DateTime day, CancellationToken ct)
    {
        var pending = await _db.InvestmentGoalStocks
            .Where(s => s.TargetTradeDate == day
                        && (s.EvaluatedAt == null || s.ActualClose == null || s.NextDayReturnPct == null))
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var symbols = pending.Select(s => s.Symbol).Distinct().ToList();
        var poolMap = await _db.StockPoolItems.AsNoTracking()
            .Where(i => i.TradeStyle == StockTradeStyle.NextDayRise
                        && i.PredictedRiseDate == day
                        && symbols.Contains(i.Symbol)
                        && i.RiseEvaluatedAt != null
                        && i.RiseActualClose != null
                        && i.RiseReturnPct != null
                        && i.RiseBaselinePrice != null)
            .ToDictionaryAsync(i => i.Symbol, StringComparer.OrdinalIgnoreCase, ct);
        if (poolMap.Count == 0) return;

        var goalIds = pending.Select(s => s.InvestmentGoalId).Distinct().ToList();
        var goals = await _db.InvestmentGoals.AsNoTracking()
            .Where(g => goalIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        var changed = false;
        foreach (var gs in pending)
        {
            if (!poolMap.TryGetValue(gs.Symbol, out var item)) continue;
            var expected = goals.TryGetValue(gs.InvestmentGoalId, out var g) ? g.ExpectedReturnPct : 0m;
            var baseline = gs.BaselinePrice is > 0 ? gs.BaselinePrice.Value : item.RiseBaselinePrice!.Value;
            var (ret, dir, tgt) = NextDayPickMetrics.Evaluate(baseline, item.RiseActualClose!.Value, expected);
            gs.BaselinePrice ??= baseline;
            gs.ActualClose = item.RiseActualClose;
            gs.NextDayReturnPct = ret;
            gs.DirectionHit = dir;
            gs.TargetHit = tgt;
            gs.EvaluatedAt = item.RiseEvaluatedAt;
            changed = true;
        }

        if (changed) await _db.SaveChangesAsync(ct);
    }

    public async Task ProcessQueuedAsync(AiQueueItem queueItem, CancellationToken ct = default)
    {
        var day = DateTime.Today;
        string? symbol = queueItem.Symbol;
        Guid? reviewId = null;

        if (!string.IsNullOrWhiteSpace(queueItem.InputPayload))
        {
            try
            {
                using var doc = JsonDocument.Parse(queueItem.InputPayload);
                if (doc.RootElement.TryGetProperty("targetTradeDate", out var td)
                    && DateTime.TryParse(td.GetString(), out var parsed))
                    day = parsed.Date;
                if (doc.RootElement.TryGetProperty("symbol", out var sy))
                    symbol = sy.GetString() ?? symbol;
                if (doc.RootElement.TryGetProperty("reviewId", out var rid)
                    && Guid.TryParse(rid.GetString(), out var parsedRid))
                    reviewId = parsedRid;
            }
            catch { /* ignore */ }
        }

        if (string.IsNullOrWhiteSpace(symbol))
            throw new InvalidOperationException("NextDayPickReview 缺少 Symbol");

        symbol = symbol.Trim().ToUpperInvariant();
        var review = reviewId.HasValue
            ? await _db.NextDayPickReviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct)
            : null;
        review ??= await _db.NextDayPickReviews.FirstOrDefaultAsync(
            r => r.AiQueueItemId == queueItem.Id
                 || (r.Symbol == symbol && r.TargetTradeDate == day), ct);

        if (review is null)
            throw new InvalidOperationException($"找不到复盘记录 {symbol} {day:yyyy-MM-dd}");

        review.Status = NextDayPickReviewStatus.Running;
        review.AiQueueItemId = queueItem.Id;
        await _db.SaveChangesAsync(ct);

        var detail = await _detailProvider.GetDetailAsync(review.Symbol, review.Name, ct);
        if (detail is null || detail.IsSimulated)
        {
            review.Status = NextDayPickReviewStatus.Skipped;
            review.Notes = detail is null ? "无行情详情，跳过" : "模拟/回退行情，跳过 AI 复盘";
            review.Summary = review.Notes;
            review.CompletedAt = DateTime.Now;
            await _db.SaveChangesAsync(ct);
            queueItem.OutputPayload = review.Notes;
            return;
        }

        await EnrichMetricsAsync(review, detail, day, ct);

        var (prompt, inputPayload) = await BuildPromptAndContextAsync(review, detail, ct);
        review.Prompt = prompt;
        review.InputPayload = inputPayload;

        var job = new AiJob
        {
            JobType = AiJobType.NextDayPickReview,
            Status = AiJobStatus.Running,
            Prompt = prompt,
            InputPayload = inputPayload
        };
        _db.AiJobs.Add(job);
        await _db.SaveChangesAsync(ct);
        review.AiJobId = job.Id;
        queueItem.RelatedAiJobId = job.Id;

        try
        {
            var output = await _ai.RunStrategyUpdateAsync(prompt, inputPayload, ct);
            job.OutputPayload = output;
            queueItem.OutputPayload = output;
            ApplyAiOutput(review, job, output);
            job.Status = AiJobStatus.SucceededPendingReview;
            job.CompletedAt = DateTime.UtcNow;
            review.Status = NextDayPickReviewStatus.Succeeded;
            review.CompletedAt = DateTime.Now;
            review.ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NextDayPickReview AI failed for {Symbol} {Date}", review.Symbol, day);
            job.Status = AiJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            review.Status = NextDayPickReviewStatus.Failed;
            review.ErrorMessage = ex.Message;
            review.CompletedAt = DateTime.Now;
            throw;
        }
        finally
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>近 N 条成功复盘摘要，供选股提示词注入。</summary>
    public async Task<object?> LoadRecentReviewsForPromptAsync(
        int take = 15,
        CancellationToken ct = default)
    {
        var rows = await _db.NextDayPickReviews.AsNoTracking()
            .Where(r => r.Status == NextDayPickReviewStatus.Succeeded)
            .OrderByDescending(r => r.TargetTradeDate)
            .ThenByDescending(r => r.CompletedAt)
            .Take(Math.Clamp(take, 1, 40))
            .ToListAsync(ct);
        if (rows.Count == 0) return null;

        var scored = rows.Where(r => r.DirectionHit.HasValue).ToList();
        var dirRate = scored.Count == 0
            ? (decimal?)null
            : Math.Round(100m * scored.Count(r => r.DirectionHit == true) / scored.Count, 1);
        var tgtScored = rows.Where(r => r.TargetHit.HasValue).ToList();
        var tgtRate = tgtScored.Count == 0
            ? (decimal?)null
            : Math.Round(100m * tgtScored.Count(r => r.TargetHit == true) / tgtScored.Count, 1);

        var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            foreach (var tag in ParseTags(r.FailureTagsJson))
                tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
        }

        var byTheme = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.ThemePreference) && r.DirectionHit.HasValue)
            .GroupBy(r => r.ThemePreference!.Trim())
            .Select(g => new
            {
                theme = g.Key,
                n = g.Count(),
                directionHitRatePct = Math.Round(100m * g.Count(x => x.DirectionHit == true) / g.Count(), 1)
            })
            .OrderByDescending(x => x.n)
            .Take(8)
            .ToList();

        return new
        {
            sampleSize = rows.Count,
            directionHitRatePct = dirRate,
            targetHitRatePct = tgtRate,
            topFailureTags = tagCounts.OrderByDescending(kv => kv.Value)
                .Take(8)
                .Select(kv => new { tag = kv.Key, count = kv.Value })
                .ToList(),
            themeHitRates = byTheme,
            recent = rows.Select(r => new
            {
                symbol = r.Symbol,
                name = r.Name,
                targetTradeDate = r.TargetTradeDate.ToString("yyyy-MM-dd"),
                grade = r.Grade,
                summary = r.Summary,
                returnPct = r.ReturnPct,
                directionHit = r.DirectionHit,
                targetHit = r.TargetHit,
                relativeStrengthPct = r.RelativeStrengthPct,
                theme = r.ThemePreference,
                failureTags = ParseTags(r.FailureTagsJson),
                pickHints = TryParseJson(r.PickHintsJson)
            }).ToList(),
            guidance =
                "须消化 recentNextDayPickReviews：高发失败标签题材/模式降权；" +
                "禁止纯叙事无量能追高；须给出相对强弱与失效情景（invalidation）；" +
                "ThemeFade/ChaseHigh/WeakRelative 代表模式勿重复。"
        };
    }

    public async Task<IReadOnlyList<NextDayPickReviewDto>> ListForGoalAsync(
        Guid goalId,
        int take = 30,
        CancellationToken ct = default)
    {
        var rows = await _db.NextDayPickReviews.AsNoTracking()
            .Where(r => r.InvestmentGoalId == goalId)
            .OrderByDescending(r => r.TargetTradeDate)
            .ThenByDescending(r => r.CreationTime)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<NextDayPickReviewDto>> ListRecentAsync(
        int take = 40,
        CancellationToken ct = default)
    {
        var rows = await _db.NextDayPickReviews.AsNoTracking()
            .OrderByDescending(r => r.TargetTradeDate)
            .ThenByDescending(r => r.CreationTime)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    private async Task<(bool Ok, string Message, Guid? QueueId)> EnqueueCoreAsync(
        string symbol,
        string name,
        DateTime target,
        decimal baseline,
        decimal actualClose,
        decimal returnPct,
        bool directionHit,
        bool targetHit,
        decimal expectedReturnPct,
        string? themePreference,
        Guid? investmentGoalId,
        Guid? goalStockId,
        Guid? stockPoolItemId,
        bool force,
        CancellationToken ct)
    {
        if (target.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return (false, "周末不复盘", null);

        symbol = symbol.Trim().ToUpperInvariant();
        var existing = await _db.NextDayPickReviews.FirstOrDefaultAsync(
            r => r.Symbol == symbol && r.TargetTradeDate == target, ct);

        if (existing is not null && !force)
        {
            if (existing.Status is NextDayPickReviewStatus.Pending or NextDayPickReviewStatus.Running
                or NextDayPickReviewStatus.Succeeded)
                return (true, $"已有复盘（{existing.Status}）：{name} {target:yyyy-MM-dd}", existing.AiQueueItemId);
            if (existing.Status == NextDayPickReviewStatus.Skipped)
                return (true, existing.Notes ?? "已跳过", null);
        }

        var dayKey = target.ToString("yyyy-MM-dd");
        var pendingQ = await _db.AiQueueItems.AnyAsync(q =>
            q.TaskType == AiQueueTaskType.NextDayPickReview
            && (q.Status == AiQueueItemStatus.Pending || q.Status == AiQueueItemStatus.Running)
            && q.Symbol == symbol
            && q.InputPayload != null
            && q.InputPayload.Contains(dayKey), ct);
        if (pendingQ && !force)
            return (false, $"已有进行中的次日预涨复盘：{name}({symbol})", null);

        var indexRet = await TryGetIndexReturnPctAsync(target, ct);
        var rs = NextDayPickMetrics.RelativeStrengthPct(returnPct, indexRet);
        var tags = NextDayPickMetrics.InferFailureTags(directionHit, returnPct, rs);

        var payload = JsonSerializer.Serialize(new
        {
            symbol,
            name,
            targetTradeDate = dayKey,
            investmentGoalId,
            goalStockId,
            poolItemId = stockPoolItemId,
            force
        }, JsonOpts);

        var queue = new AiQueueItem
        {
            TaskType = AiQueueTaskType.NextDayPickReview,
            Status = AiQueueItemStatus.Pending,
            Title = $"次日预涨复盘 · {name}({symbol}) {target:MM-dd}",
            CorrelationId = Guid.NewGuid(),
            RelatedGoalId = investmentGoalId,
            RelatedPoolItemId = stockPoolItemId,
            Symbol = symbol,
            InputPayload = payload
        };
        _db.AiQueueItems.Add(queue);

        if (existing is null)
        {
            existing = new NextDayPickReview
            {
                Symbol = symbol,
                Name = name,
                TargetTradeDate = target
            };
            _db.NextDayPickReviews.Add(existing);
        }

        existing.InvestmentGoalId = investmentGoalId;
        existing.GoalStockId = goalStockId;
        existing.StockPoolItemId = stockPoolItemId;
        existing.Name = name;
        existing.BaselinePrice = baseline;
        existing.ActualClose = actualClose;
        existing.ReturnPct = returnPct;
        existing.DirectionHit = directionHit;
        existing.TargetHit = targetHit;
        existing.RelativeStrengthPct = rs;
        existing.ThemePreference = themePreference;
        existing.ExpectedReturnPct = expectedReturnPct;
        existing.FailureTagsJson = tags.Count > 0
            ? JsonSerializer.Serialize(tags, JsonOpts)
            : null;
        existing.Status = NextDayPickReviewStatus.Pending;
        existing.AiQueueItemId = queue.Id;
        existing.ErrorMessage = null;
        existing.Notes = force ? "强制重跑" : null;
        existing.CompletedAt = null;
        await _db.SaveChangesAsync(ct);

        // 回写 reviewId 便于消费定位
        queue.InputPayload = JsonSerializer.Serialize(new
        {
            reviewId = existing.Id,
            symbol,
            name,
            targetTradeDate = dayKey,
            investmentGoalId,
            goalStockId,
            poolItemId = stockPoolItemId,
            force
        }, JsonOpts);
        await _db.SaveChangesAsync(ct);

        return (true, $"已入队次日预涨复盘：{name}({symbol}) {target:yyyy-MM-dd}", queue.Id);
    }

    private async Task EnrichMetricsAsync(
        NextDayPickReview review,
        StockDetailBundle detail,
        DateTime target,
        CancellationToken ct)
    {
        if (review.BaselinePrice is > 0 && review.ActualClose is > 0 && review.ReturnPct is null)
        {
            var expected = review.ExpectedReturnPct ?? 0m;
            var (ret, dir, tgt) = NextDayPickMetrics.Evaluate(
                review.BaselinePrice.Value, review.ActualClose.Value, expected);
            review.ReturnPct = ret;
            review.DirectionHit = dir;
            review.TargetHit = tgt;
        }

        if (review.RelativeStrengthPct is null && review.ReturnPct.HasValue)
        {
            var indexRet = await TryGetIndexReturnPctAsync(target, ct);
            review.RelativeStrengthPct = NextDayPickMetrics.RelativeStrengthPct(
                review.ReturnPct.Value, indexRet);
        }

        if (string.IsNullOrWhiteSpace(review.FailureTagsJson) && review.DirectionHit == false)
        {
            decimal? openGap = null;
            var bar = detail.DailyBars?.FirstOrDefault(b => b.Date.Date == target);
            if (bar is not null && review.BaselinePrice is > 0 && bar.Open > 0)
                openGap = Math.Round((bar.Open - review.BaselinePrice.Value) / review.BaselinePrice.Value * 100m, 2);

            bool? nearHigh = null;
            var recent = detail.DailyBars?
                .Where(b => b.Date.Date <= target && b.High > 0)
                .OrderByDescending(b => b.Date)
                .Take(21)
                .ToList();
            if (recent is { Count: >= 5 } && review.BaselinePrice is > 0)
            {
                var hi20 = recent.Skip(1).Take(20).Select(b => b.High).DefaultIfEmpty(0).Max();
                if (hi20 > 0)
                    nearHigh = review.BaselinePrice.Value >= hi20 * 0.97m;
            }

            var tags = NextDayPickMetrics.InferFailureTags(
                false, review.ReturnPct ?? 0m, review.RelativeStrengthPct, openGap, nearHigh);
            review.FailureTagsJson = tags.Count > 0
                ? JsonSerializer.Serialize(tags, JsonOpts)
                : null;
        }
    }

    private async Task<(string Prompt, string InputPayload)> BuildPromptAndContextAsync(
        NextDayPickReview review,
        StockDetailBundle detail,
        CancellationToken ct)
    {
        var day = review.TargetTradeDate.Date;
        var bar = detail.DailyBars?.FirstOrDefault(b => b.Date.Date == day);
        var board = detail.Board;
        var ohlc = bar is not null
            ? new
            {
                open = bar.Open,
                high = bar.High,
                low = bar.Low,
                close = bar.Close,
                amount = bar.Amount,
                openGapPct = review.BaselinePrice is > 0
                    ? Math.Round((bar.Open - review.BaselinePrice.Value) / review.BaselinePrice.Value * 100m, 2)
                    : (decimal?)null,
                pathLabel = ClassifyPath(bar.Open, bar.High, bar.Low, bar.Close)
            }
            : new
            {
                open = board.Open,
                high = board.High,
                low = board.Low,
                close = board.Last,
                amount = board.Amount,
                openGapPct = (decimal?)null,
                pathLabel = (string?)null
            };

        AiSuggestion? suggestion = null;
        if (review.GoalStockId.HasValue)
        {
            var gs = await _db.InvestmentGoalStocks.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == review.GoalStockId, ct);
            if (gs?.SuggestionId is Guid sid)
                suggestion = await _db.AiSuggestions.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == sid, ct);
        }

        var indexRet = await TryGetIndexReturnPctAsync(day, ct);

        var context = new
        {
            schema = "NextDayPickReview/v1",
            symbol = review.Symbol,
            name = review.Name,
            targetTradeDate = day.ToString("yyyy-MM-dd"),
            themePreference = review.ThemePreference,
            expectedReturnPct = review.ExpectedReturnPct,
            baselinePrice = review.BaselinePrice,
            actualClose = review.ActualClose,
            returnPct = review.ReturnPct,
            directionHit = review.DirectionHit,
            targetHit = review.TargetHit,
            relativeStrengthPct = review.RelativeStrengthPct,
            indexSymbol = IndexSymbol,
            indexReturnPct = indexRet,
            inferredFailureTags = ParseTags(review.FailureTagsJson),
            ohlc,
            selection = suggestion is null ? null : new
            {
                reason = suggestion.Reason,
                selectionLogic = suggestion.SelectionLogic,
                risk = suggestion.Risk
            }
        };

        var inputPayload = JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true });

        var sb = new StringBuilder();
        sb.AppendLine($"请对 A 股模拟「次日上涨预选」标的 {review.Symbol} {review.Name} 在目标日 {day:yyyy-MM-dd} 做选股复盘。");
        sb.AppendLine("目标：判断隔夜逻辑是否兑现，归类错因，并给出下一批选股可执行的 pickHints。");
        sb.AppendLine("硬约束：");
        sb.AppendLine("1) 不要输出下单指令；不要承诺收益。");
        sb.AppendLine("2) 主 KPI 是方向命中与相对强弱（相对创业板指），不要只看绝对涨跌。");
        sb.AppendLine("3) failureTags 优先使用：ThemeFade / ChaseHigh / WeakRelative / NewsNoise / GapDownOpen / LiquidityTrap / CorrectThesisWrongTiming / MarketDrag。");
        sb.AppendLine("必须只输出一个 JSON 对象（可包在 markdown 代码块中），字段：");
        sb.AppendLine("""
            {
              "summary": "一句话结论",
              "grade": "A|B|C|D",
              "directionOk": true,
              "relativeOk": true,
              "failureTags": ["ChaseHigh"],
              "good": ["..."],
              "bad": ["..."],
              "pickHints": {
                "downweightThemes": ["..."],
                "avoidPatterns": ["追高放量无催化"],
                "requireRelativeStrength": true,
                "notes": "给下一批 GoalToPrompt/StockPick 的自然语言约束"
              }
            }
            """);
        sb.AppendLine("上下文 JSON 已附在 input payload；请结合 selection、ohlc、relativeStrengthPct、inferredFailureTags 分析。");

        return (sb.ToString(), inputPayload);
    }

    private async Task<decimal?> TryGetIndexReturnPctAsync(DateTime target, CancellationToken ct)
    {
        try
        {
            var idx = await _detailProvider.GetDetailAsync(IndexSymbol, "创业板指", ct);
            if (idx?.DailyBars is null || idx.IsSimulated) return null;
            var bars = idx.DailyBars
                .Where(b => b.Date.Date <= target && b.Close > 0)
                .OrderBy(b => b.Date)
                .ToList();
            var dayBar = bars.LastOrDefault(b => b.Date.Date == target);
            if (dayBar is null) return null;
            var prev = bars.LastOrDefault(b => b.Date.Date < target);
            if (prev is null || prev.Close <= 0) return null;
            return Math.Round((dayBar.Close - prev.Close) / prev.Close * 100m, 4);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Index return for {Symbol} {Date} unavailable", IndexSymbol, target);
            return null;
        }
    }

    private static void ApplyAiOutput(NextDayPickReview review, AiJob job, string output)
    {
        review.OutputPayload = output;
        var json = ExtractJsonObject(output);
        if (json is null)
        {
            review.Summary = Truncate(output, 240);
            review.Grade = null;
            review.FindingsJson = null;
            review.PickHintsJson = null;
            job.AnalysisSummary = review.Summary;
            return;
        }

        string? GetStr(string name) =>
            json.TryGetPropertyValue(name, out var n) ? n?.ToString()?.Trim('"') : null;

        review.Summary = GetStr("summary") ?? Truncate(output, 240);
        review.Grade = GetStr("grade");
        if (json["pickHints"] is JsonNode hints)
            review.PickHintsJson = hints.ToJsonString(JsonOpts);

        if (json["failureTags"] is JsonArray arr)
        {
            var tags = arr.Select(n => n?.ToString()?.Trim().Trim('"'))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (tags.Count > 0)
                review.FailureTagsJson = JsonSerializer.Serialize(tags, JsonOpts);
        }

        var findings = new JsonObject();
        foreach (var key in new[] { "good", "bad", "directionOk", "relativeOk", "failureTags" })
        {
            if (json[key] is JsonNode n)
                findings[key] = n.DeepClone();
        }

        review.FindingsJson = findings.Count > 0 ? findings.ToJsonString(JsonOpts) : null;
        job.AnalysisSummary = review.Summary;
        job.AnalysisStepsJson = review.FindingsJson;
    }

    private static string? ClassifyPath(decimal open, decimal high, decimal low, decimal close)
    {
        if (open <= 0 || close <= 0) return null;
        var upOpen = close >= open;
        var range = high - low;
        if (range <= 0) return upOpen ? "FlatUp" : "FlatDown";
        var fromHigh = (high - close) / range;
        var fromLow = (close - low) / range;
        if (open >= high - range * 0.25m && fromHigh >= 0.55m) return "HighOpenLowGo";
        if (open <= low + range * 0.25m && fromLow >= 0.55m) return "LowOpenHighGo";
        return upOpen ? "MidRangeUp" : "MidRangeDown";
    }

    private static JsonObject? ExtractJsonObject(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
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

        try { return JsonNode.Parse(text) as JsonObject; }
        catch { return null; }
    }

    private static object? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch { return json; }
    }

    private static IReadOnlyList<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(json);
            return arr?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                   ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static NextDayPickReviewDto ToDto(NextDayPickReview r) => new(
        r.Id,
        r.InvestmentGoalId,
        r.Symbol,
        r.Name,
        r.TargetTradeDate,
        r.Status.ToString(),
        r.Grade,
        r.Summary,
        r.ReturnPct,
        r.DirectionHit,
        r.TargetHit,
        r.RelativeStrengthPct,
        r.ThemePreference,
        r.ExpectedReturnPct,
        r.FailureTagsJson,
        r.FindingsJson,
        r.PickHintsJson,
        r.Prompt,
        r.OutputPayload,
        r.CompletedAt,
        r.Notes,
        r.ErrorMessage);
}
