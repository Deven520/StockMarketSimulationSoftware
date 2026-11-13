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
using StockSim.Domain.Trading;

namespace StockSim.Application.Services;

/// <summary>
/// 做T股每日收盘复盘：入队 → 组上下文/提示词 → AI → 落库；供夜更注入与个股页查阅。
/// </summary>
public sealed class DailyTReviewAppService
{
    private readonly IStockSimDataStore _db;
    private readonly ICursorAiClient _ai;
    private readonly IStockDetailProvider _detailProvider;
    private readonly ILogger<DailyTReviewAppService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public DailyTReviewAppService(
        IStockSimDataStore db,
        ICursorAiClient ai,
        IStockDetailProvider detailProvider,
        ILogger<DailyTReviewAppService> logger)
    {
        _db = db;
        _ai = ai;
        _detailProvider = detailProvider;
        _logger = logger;
    }

    /// <summary>为单个做T成分入队当日复盘；force 可覆盖 Failed/Succeeded。</summary>
    public async Task<(bool Ok, string Message, Guid? QueueId)> EnqueueAsync(
        Guid poolItemId,
        DateTime? tradeDate = null,
        bool force = false,
        CancellationToken ct = default)
    {
        var day = (tradeDate ?? DateTime.Today).Date;
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return (false, "周末不复盘", null);

        var item = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Id == poolItemId, ct);
        if (item is null) return (false, "成分不存在", null);
        if (item.TradeStyle != StockTradeStyle.IntradayT)
            return (false, $"仅做T股复盘（当前 {StockTradeStyleHelper.ToLabel(item.TradeStyle)}）", null);
        if (!item.BoundStrategyId.HasValue)
            return (false, "未绑定策略，跳过复盘", null);

        var existing = await _db.DailyTReviews.FirstOrDefaultAsync(
            r => r.Symbol == item.Symbol && r.TradeDate == day, ct);

        if (existing is not null && !force)
        {
            if (existing.Status is DailyTReviewStatus.Pending or DailyTReviewStatus.Running
                or DailyTReviewStatus.Succeeded)
                return (true, $"已有复盘（{existing.Status}）：{item.Name} {day:yyyy-MM-dd}", existing.AiQueueItemId);
            if (existing.Status == DailyTReviewStatus.Skipped)
                return (true, existing.Notes ?? "已跳过（无成交/无买卖决策）", null);
        }

        // SQLite/EF 无法翻译 Contains(..., StringComparison)；用无比较器的 Contains
        var dayKey = day.ToString("yyyy-MM-dd");
        var pendingQ = await _db.AiQueueItems.AnyAsync(q =>
            q.RelatedPoolItemId == poolItemId
            && q.TaskType == AiQueueTaskType.DailyTReview
            && (q.Status == AiQueueItemStatus.Pending || q.Status == AiQueueItemStatus.Running)
            && q.InputPayload != null
            && q.InputPayload.Contains(dayKey), ct);
        if (pendingQ && !force)
            return (false, $"已有进行中的复盘任务：{item.Name}({item.Symbol})", null);

        var hasActivity = await HasReviewWorthyActivityAsync(item.Symbol, day, ct);
        if (!hasActivity)
        {
            if (existing is null)
            {
                existing = new DailyTReview
                {
                    StockPoolItemId = item.Id,
                    Symbol = item.Symbol,
                    Name = item.Name,
                    TradeDate = day,
                    StrategyDefinitionId = item.BoundStrategyId
                };
                _db.DailyTReviews.Add(existing);
            }

            existing.Status = DailyTReviewStatus.Skipped;
            existing.CompletedAt = DateTime.Now;
            existing.Notes = "无成交且无买卖决策，跳过 AI 复盘";
            existing.Summary = existing.Notes;
            await _db.SaveChangesAsync(ct);
            return (true, existing.Notes, null);
        }

        var payload = JsonSerializer.Serialize(new
        {
            poolItemId = item.Id,
            symbol = item.Symbol,
            name = item.Name,
            tradeDate = day.ToString("yyyy-MM-dd"),
            force
        }, JsonOpts);

        var queue = new AiQueueItem
        {
            TaskType = AiQueueTaskType.DailyTReview,
            Status = AiQueueItemStatus.Pending,
            Title = $"每日复盘 · {item.Name}({item.Symbol}) {day:MM-dd}",
            CorrelationId = Guid.NewGuid(),
            RelatedPoolItemId = item.Id,
            Symbol = item.Symbol,
            InputPayload = payload
        };
        _db.AiQueueItems.Add(queue);

        if (existing is null)
        {
            existing = new DailyTReview
            {
                StockPoolItemId = item.Id,
                Symbol = item.Symbol,
                Name = item.Name,
                TradeDate = day,
                StrategyDefinitionId = item.BoundStrategyId
            };
            _db.DailyTReviews.Add(existing);
        }

        existing.Status = DailyTReviewStatus.Pending;
        existing.AiQueueItemId = queue.Id;
        existing.ErrorMessage = null;
        existing.Notes = force ? "强制重跑" : null;
        existing.CompletedAt = null;
        await _db.SaveChangesAsync(ct);

        return (true, $"已入队每日复盘：{item.Name}({item.Symbol}) {day:yyyy-MM-dd}", queue.Id);
    }

    /// <summary>全池做T股入队指定日复盘（幂等；force 可重跑已成功项）。</summary>
    public async Task<(int Queued, int SkippedOrDone, int Total)> EnqueueAllIntradayTAsync(
        DateTime? tradeDate = null,
        bool force = false,
        CancellationToken ct = default)
    {
        var day = (tradeDate ?? DateTime.Today).Date;
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return (0, 0, 0);

        var items = await _db.StockPoolItems
            .Where(i => i.TradeStyle == StockTradeStyle.IntradayT && i.BoundStrategyId != null)
            .Select(i => i.Id)
            .ToListAsync(ct);

        var queued = 0;
        var skippedOrDone = 0;
        foreach (var id in items)
        {
            var (ok, _, qid) = await EnqueueAsync(id, day, force, ct);
            if (ok && qid.HasValue) queued++;
            else if (ok) skippedOrDone++;
        }

        return (queued, skippedOrDone, items.Count);
    }

    public async Task ProcessQueuedAsync(AiQueueItem queueItem, CancellationToken ct = default)
    {
        if (!queueItem.RelatedPoolItemId.HasValue)
            throw new InvalidOperationException("DailyTReview 缺少 RelatedPoolItemId");

        var poolItemId = queueItem.RelatedPoolItemId.Value;
        var item = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Id == poolItemId, ct)
                   ?? throw new InvalidOperationException("成分不存在");

        var day = DateTime.Today;
        if (!string.IsNullOrWhiteSpace(queueItem.InputPayload))
        {
            try
            {
                using var doc = JsonDocument.Parse(queueItem.InputPayload);
                if (doc.RootElement.TryGetProperty("tradeDate", out var td)
                    && DateTime.TryParse(td.GetString(), out var parsed))
                    day = parsed.Date;
            }
            catch { /* ignore */ }
        }

        var review = await _db.DailyTReviews.FirstOrDefaultAsync(
            r => r.AiQueueItemId == queueItem.Id
                 || (r.Symbol == item.Symbol && r.TradeDate == day), ct);
        if (review is null)
        {
            review = new DailyTReview
            {
                StockPoolItemId = item.Id,
                Symbol = item.Symbol,
                Name = item.Name,
                TradeDate = day,
                StrategyDefinitionId = item.BoundStrategyId,
                AiQueueItemId = queueItem.Id
            };
            _db.DailyTReviews.Add(review);
        }

        review.Status = DailyTReviewStatus.Running;
        review.AiQueueItemId = queueItem.Id;
        await _db.SaveChangesAsync(ct);

        var detail = await _detailProvider.GetDetailAsync(item.Symbol, item.Name, ct);
        if (detail is null || detail.IsSimulated)
        {
            review.Status = DailyTReviewStatus.Skipped;
            review.Notes = detail is null ? "无行情详情，跳过" : "模拟/回退行情，跳过 AI 复盘";
            review.Summary = review.Notes;
            review.CompletedAt = DateTime.Now;
            await _db.SaveChangesAsync(ct);
            queueItem.OutputPayload = review.Notes;
            return;
        }

        var (prompt, inputPayload, metrics) = await BuildPromptAndContextAsync(item, day, detail, ct);
        review.Prompt = prompt;
        review.InputPayload = inputPayload;
        review.CyclePnlApprox = metrics.CyclePnlApprox;
        review.Fees = metrics.Fees;
        review.TradeCount = metrics.TradeCount;
        review.PathRegime = metrics.PathRegime;
        review.PathPhase = metrics.PathPhase;
        review.StrategyDefinitionId = item.BoundStrategyId;
        review.StrategyVersionId = metrics.StrategyVersionId;

        var job = new AiJob
        {
            JobType = AiJobType.DailyTReview,
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
            review.Status = DailyTReviewStatus.Succeeded;
            review.CompletedAt = DateTime.Now;
            review.ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DailyTReview AI failed for {Symbol} {Date}", item.Symbol, day);
            job.Status = AiJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            review.Status = DailyTReviewStatus.Failed;
            review.ErrorMessage = ex.Message;
            review.CompletedAt = DateTime.Now;
            throw;
        }
        finally
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>近 N 日成功复盘摘要，供夜更提示词注入。</summary>
    public async Task<object?> LoadRecentReviewsForPromptAsync(
        string symbol,
        int take = 5,
        CancellationToken ct = default)
    {
        var rows = await _db.DailyTReviews.AsNoTracking()
            .Where(r => r.Symbol == symbol && r.Status == DailyTReviewStatus.Succeeded)
            .OrderByDescending(r => r.TradeDate)
            .Take(take)
            .Select(r => new
            {
                tradeDate = r.TradeDate,
                grade = r.Grade,
                summary = r.Summary,
                pathRegime = r.PathRegime,
                cyclePnlApprox = r.CyclePnlApprox,
                fees = r.Fees,
                tradeCount = r.TradeCount,
                strategyHints = r.StrategyHintsJson,
                findings = r.FindingsJson
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return null;

        return rows.Select(r => new
        {
            r.tradeDate,
            r.grade,
            r.summary,
            r.pathRegime,
            r.cyclePnlApprox,
            r.fees,
            r.tradeCount,
            strategyHints = TryParseJson(r.strategyHints),
            findings = TryParseJson(r.findings)
        }).ToList();
    }

    public async Task<IReadOnlyList<DailyTReviewDto>> ListForSymbolAsync(
        string symbol,
        int take = 20,
        CancellationToken ct = default)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var rows = await _db.DailyTReviews.AsNoTracking()
            .Where(r => r.Symbol == symbol)
            .OrderByDescending(r => r.TradeDate)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    private async Task<bool> HasReviewWorthyActivityAsync(string symbol, DateTime day, CancellationToken ct)
    {
        var hasTrade = await _db.Trades.AnyAsync(t => t.Symbol == symbol && t.TradeDate == day, ct);
        if (hasTrade) return true;
        return await _db.StrategyDecisionLogs.AnyAsync(
            d => d.Symbol == symbol
                 && d.TradeDate == day
                 && (d.Action == "Buy" || d.Action == "Sell"), ct);
    }

    private async Task<(string Prompt, string InputPayload, ReviewMetrics Metrics)> BuildPromptAndContextAsync(
        StockPoolItem item,
        DateTime day,
        StockDetailBundle detail,
        CancellationToken ct)
    {
        var trades = await _db.Trades.AsNoTracking()
            .Where(t => t.Symbol == item.Symbol && t.TradeDate == day)
            .OrderBy(t => t.TradedAt)
            .ToListAsync(ct);

        var tradeRows = trades.Select(t =>
        {
            var utc = t.TradedAt.Kind == DateTimeKind.Utc
                ? t.TradedAt
                : DateTime.SpecifyKind(t.TradedAt, DateTimeKind.Utc);
            var local = utc.ToLocalTime();
            return new
            {
                time = local.ToString("HH:mm:ss"),
                side = t.Side.ToString(),
                price = t.Price,
                qty = t.Quantity,
                fee = t.TotalFee,
                isT = t.StrategyDefinitionId.HasValue
            };
        }).ToList();

        var cyclePnl = EstimateCyclePnl(trades);
        var fees = trades.Sum(t => t.TotalFee);

        var fillDecisions = await _db.StrategyDecisionLogs.AsNoTracking()
            .Where(d => d.Symbol == item.Symbol
                        && d.TradeDate == day
                        && (d.Action == "Buy" || d.Action == "Sell"))
            .OrderBy(d => d.DecidedAt)
            .Select(d => new
            {
                d.DecidedAt,
                d.State,
                d.Action,
                d.Reason,
                d.LastPrice,
                d.AnchorPrice,
                d.DeviationPct,
                d.BlockedBy
            })
            .ToListAsync(ct);

        var blockedAgg = await _db.StrategyDecisionLogs.AsNoTracking()
            .Where(d => d.Symbol == item.Symbol && d.TradeDate == day
                        && d.BlockedBy != null && d.BlockedBy != "")
            .GroupBy(d => d.BlockedBy!)
            .Select(g => new { reason = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .Take(12)
            .ToListAsync(ct);

        var runtime = await _db.StrategyRuntimeStates.AsNoTracking()
            .Where(r => r.Symbol == item.Symbol && r.TradeDate == day)
            .OrderByDescending(r => r.LastModificationTime)
            .FirstOrDefaultAsync(ct);

        var position = await _db.Positions.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Symbol == item.Symbol, ct);

        var pnl = await _db.DailyPnLs.AsNoTracking()
            .Where(p => p.Symbol == item.Symbol && p.TradeDate == day)
            .OrderByDescending(p => p.CreationTime)
            .FirstOrDefaultAsync(ct);

        var forecasts = await _db.PriceRangeForecasts.AsNoTracking()
            .Where(f => f.Symbol == item.Symbol && f.ForecastForDate == day)
            .OrderBy(f => f.Source)
            .Select(f => new
            {
                source = f.Source.ToString(),
                predictedHigh = f.PredictedHigh,
                predictedLow = f.PredictedLow,
                actualHigh = f.ActualHigh,
                actualLow = f.ActualLow,
                combinedAccuracyPct = f.CombinedAccuracyPct,
                countsTowardAccuracy = f.CountsTowardAccuracy,
                status = f.Status.ToString(),
                notes = f.Notes
            })
            .ToListAsync(ct);

        Guid? versionId = null;
        string? policyJson = null;
        if (item.BoundStrategyId.HasValue)
        {
            var strategy = await _db.Strategies.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == item.BoundStrategyId, ct);
            versionId = strategy?.CurrentVersionId;
            if (versionId.HasValue)
            {
                var ver = await _db.StrategyVersions.AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == versionId, ct);
                policyJson = ver?.ParametersJson;
            }
        }

        var bar = detail.DailyBars?.FirstOrDefault(b => b.Date.Date == day);
        var board = detail.Board;
        var ohlc = bar is not null
            ? new
            {
                open = bar.Open,
                high = bar.High,
                low = bar.Low,
                close = bar.Close,
                preClose = board.PreClose,
                changePct = board.PreClose > 0
                    ? Math.Round((bar.Close - board.PreClose) / board.PreClose * 100m, 2)
                    : board.ChangePercent,
                amount = bar.Amount
            }
            : new
            {
                open = board.Open,
                high = board.High,
                low = board.Low,
                close = board.Last,
                preClose = board.PreClose,
                changePct = board.ChangePercent,
                amount = board.Amount
            };

        var context = new
        {
            schema = "DailyTReview/v1",
            symbol = item.Symbol,
            name = item.Name,
            tradeDate = day.ToString("yyyy-MM-dd"),
            ohlc,
            trades = tradeRows,
            cyclePnlApprox = cyclePnl,
            fees,
            tradeCount = trades.Count,
            fillDecisions,
            blockedTop = blockedAgg,
            runtime = runtime is null ? null : new
            {
                runtime.State,
                runtime.PathRegime,
                runtime.PathPhase,
                runtime.OpenTQty,
                runtime.DayRealizedPnL,
                runtime.LastSellPrice,
                runtime.LastBuyPrice,
                runtime.OverridesJson
            },
            position = position is null ? null : new
            {
                position.BaseQuantity,
                position.Quantity,
                position.AvgCost,
                excess = position.Quantity - position.BaseQuantity
            },
            dailyPnl = pnl is null ? null : new
            {
                pnl.RealizedPnL,
                pnl.Fees,
                pnl.TradeCount,
                pnl.WinRate,
                note = "账面 RealizedPnL 按持仓成本口径，不等于纯 T 买卖价差；请以 cyclePnlApprox 评估做T质量"
            },
            forecasts,
            currentPolicy = policyJson
        };

        var inputPayload = JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true });

        var sb = new StringBuilder();
        sb.AppendLine($"请对 A 股模拟做T标的 {item.Symbol} {item.Name} 在 {day:yyyy-MM-dd} 做「每日复盘」。");
        sb.AppendLine("目标：评估路径分型/状态机/预测软边界/ForceFlat 是否执行得当，并给出次日可落地的策略边界优化建议。");
        sb.AppendLine("硬约束：");
        sb.AppendLine("1) 不要输出下单指令；不要承诺收益。");
        sb.AppendLine("2) 账面 DailyPnL.RealizedPnL 是成本口径，评估做T质量请优先用 cyclePnlApprox（买卖配对价差）与 fees。");
        sb.AppendLine("3) 指出 ForceFlat 超配减仓是否与 OpenT 买回空转、预测区间是否失效、是否错过更好正T等。");
        sb.AppendLine("必须只输出一个 JSON 对象（可包在 markdown 代码块中），字段：");
        sb.AppendLine("""
            {
              "summary": "一句话结论",
              "grade": "A|B|C|D",
              "pathOk": true,
              "pathLabel": "HighOpenLowGo 等",
              "good": ["..."],
              "bad": ["..."],
              "forecastNote": "...",
              "tCyclePnlApprox": 0,
              "strategyHints": {
                "forceFlatExcessShouldNotOpenT": true,
                "notes": "给夜更的自然语言建议（triggers/path/session/forecast）"
              }
            }
            """);
        sb.AppendLine("上下文 JSON 已附在 input payload；请结合 trades、fillDecisions、blockedTop、runtime、forecasts、position 分析。");

        var metrics = new ReviewMetrics(
            cyclePnl,
            fees,
            trades.Count,
            runtime?.PathRegime,
            runtime?.PathPhase,
            versionId);
        return (sb.ToString(), inputPayload, metrics);
    }

    private static void ApplyAiOutput(DailyTReview review, AiJob job, string output)
    {
        review.OutputPayload = output;
        var json = ExtractJsonObject(output);
        if (json is null)
        {
            review.Summary = Truncate(output, 240);
            review.Grade = null;
            review.FindingsJson = null;
            review.StrategyHintsJson = null;
            job.AnalysisSummary = review.Summary;
            return;
        }

        string? GetStr(string name) =>
            json.TryGetPropertyValue(name, out var n) ? n?.ToString()?.Trim('"') : null;

        review.Summary = GetStr("summary") ?? Truncate(output, 240);
        review.Grade = GetStr("grade");
        if (json["strategyHints"] is JsonNode hints)
            review.StrategyHintsJson = hints.ToJsonString(JsonOpts);
        var findings = new JsonObject();
        foreach (var key in new[] { "good", "bad", "pathOk", "pathLabel", "forecastNote", "tCyclePnlApprox" })
        {
            if (json[key] is JsonNode n)
                findings[key] = n.DeepClone();
        }

        review.FindingsJson = findings.Count > 0 ? findings.ToJsonString(JsonOpts) : null;
        if (json["tCyclePnlApprox"] is JsonValue jv
            && jv.TryGetValue<decimal>(out var aiCycle)
            && !review.CyclePnlApprox.HasValue)
            review.CyclePnlApprox = aiCycle;

        job.AnalysisSummary = review.Summary;
        job.AnalysisStepsJson = review.FindingsJson;
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

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static object? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch { return json; }
    }

    /// <summary>按时间序粗配对：Sell 后找下一笔 Buy，价差×股数累加。</summary>
    private static decimal EstimateCyclePnl(IReadOnlyList<Trade> trades)
    {
        decimal sum = 0;
        for (var i = 0; i < trades.Count; i++)
        {
            if (trades[i].Side != OrderSide.Sell) continue;
            for (var j = i + 1; j < trades.Count; j++)
            {
                if (trades[j].Side != OrderSide.Buy) continue;
                var qty = Math.Min(trades[i].Quantity, trades[j].Quantity);
                sum += (trades[i].Price - trades[j].Price) * qty;
                break;
            }
        }

        return Math.Round(sum, 2);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static DailyTReviewDto ToDto(DailyTReview r) => new(
        r.Id,
        r.Symbol,
        r.Name,
        r.TradeDate,
        r.Status.ToString(),
        r.Grade,
        r.Summary,
        r.PathRegime,
        r.PathPhase,
        r.CyclePnlApprox,
        r.Fees,
        r.TradeCount,
        r.FindingsJson,
        r.StrategyHintsJson,
        r.Prompt,
        r.OutputPayload,
        r.CompletedAt,
        r.Notes,
        r.ErrorMessage);

    private sealed record ReviewMetrics(
        decimal CyclePnlApprox,
        decimal Fees,
        int TradeCount,
        string? PathRegime,
        string? PathPhase,
        Guid? StrategyVersionId);
}
