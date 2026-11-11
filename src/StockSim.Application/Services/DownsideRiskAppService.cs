using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockSim.Application.Dtos;
using StockSim.Application.Services.Alerts;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;
using StockSim.Domain.Trading;

namespace StockSim.Application.Services;

/// <summary>
/// 池内「预测风险」：入队 → 规则快检 + AI → 落库/推送；日终对账命中率（开市/非开市均可）。
/// </summary>
public sealed class DownsideRiskAppService
{
    private readonly IStockSimDataStore _db;
    private readonly ICursorAiClient _ai;
    private readonly CachedLightQuoteService _quotes;
    private readonly IMarketNewsProvider _news;
    private readonly IStockDetailProvider _detailProvider;
    private readonly ITradeAlertNotifier _alerts;
    private readonly ILogger<DownsideRiskAppService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public DownsideRiskAppService(
        IStockSimDataStore db,
        ICursorAiClient ai,
        CachedLightQuoteService quotes,
        IMarketNewsProvider news,
        IStockDetailProvider detailProvider,
        ITradeAlertNotifier alerts,
        ILogger<DownsideRiskAppService> logger)
    {
        _db = db;
        _ai = ai;
        _quotes = quotes;
        _news = news;
        _detailProvider = detailProvider;
        _alerts = alerts;
        _logger = logger;
    }

    /// <summary>入队预测下一交易日下跌风险；同股同目标日未完成任务去重。开市/非开市均可。</summary>
    public async Task<(bool Ok, string Message, Guid? QueueId)> EnqueuePredictAsync(
        Guid poolItemId,
        CancellationToken ct = default)
    {
        var now = DateTime.Now;

        var item = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Id == poolItemId, ct);
        if (item is null) return (false, "成分不存在", null);

        var target = PriceForecastMetrics.NextTradingDay(now.Date);
        var pending = await _db.AiQueueItems.AnyAsync(q =>
            q.RelatedPoolItemId == poolItemId
            && q.TaskType == AiQueueTaskType.DownsideRiskPredict
            && (q.Status == AiQueueItemStatus.Pending || q.Status == AiQueueItemStatus.Running), ct);
        if (pending)
            return (false, $"已有进行中的风险预测任务：{item.Name}({item.Symbol})", null);

        var recentReady = await _db.DownsideRiskForecasts
            .Where(f => f.StockPoolItemId == poolItemId
                        && f.PredictedForDate == target
                        && f.Status == DownsideRiskStatus.Ready
                        && f.AssessedAt >= now.AddMinutes(-30))
            .OrderByDescending(f => f.AssessedAt)
            .FirstOrDefaultAsync(ct);
        if (recentReady is not null)
        {
            return (true,
                $"30 分钟内已有结果：{NextDayRiskMetrics.ToLabel(recentReady.RiskLevel)}（目标日 {target:yyyy-MM-dd}）",
                recentReady.AiQueueItemId);
        }

        var payload = JsonSerializer.Serialize(new
        {
            poolItemId = item.Id,
            symbol = item.Symbol,
            name = item.Name,
            predictedForDate = target.ToString("yyyy-MM-dd")
        }, JsonOpts);

        var queue = new AiQueueItem
        {
            TaskType = AiQueueTaskType.DownsideRiskPredict,
            Status = AiQueueItemStatus.Pending,
            Title = $"预测风险 · {item.Name}({item.Symbol})",
            CorrelationId = Guid.NewGuid(),
            RelatedPoolItemId = item.Id,
            Symbol = item.Symbol,
            InputPayload = payload
        };
        _db.AiQueueItems.Add(queue);

        var draft = new DownsideRiskForecast
        {
            StockPoolItemId = item.Id,
            Symbol = item.Symbol,
            Name = item.Name,
            PredictedForDate = target,
            AssessedAt = now,
            SessionPhase = AShareMarketSession.Describe(now),
            Status = DownsideRiskStatus.Pending,
            AiQueueItemId = queue.Id,
            Source = "pending"
        };
        // queue.Id 在 Add 后已有 Guid
        draft.AiQueueItemId = queue.Id;
        _db.DownsideRiskForecasts.Add(draft);
        await _db.SaveChangesAsync(ct);

        return (true, $"已入队风险预测：{item.Name}({item.Symbol})，目标日 {target:yyyy-MM-dd}", queue.Id);
    }

    /// <summary>队列执行：规则快检 + AI，写 Ready/Failed，必要时推送。</summary>
    public async Task ProcessQueuedAsync(AiQueueItem queueItem, CancellationToken ct = default)
    {
        if (!queueItem.RelatedPoolItemId.HasValue)
            throw new InvalidOperationException("DownsideRiskPredict 缺少 RelatedPoolItemId");

        var poolItemId = queueItem.RelatedPoolItemId.Value;
        var item = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Id == poolItemId, ct)
                   ?? throw new InvalidOperationException("成分不存在");

        var now = DateTime.Now;
        var target = PriceForecastMetrics.NextTradingDay(now.Date);

        var forecast = await _db.DownsideRiskForecasts
            .Where(f => f.AiQueueItemId == queueItem.Id)
            .OrderByDescending(f => f.CreationTime)
            .FirstOrDefaultAsync(ct);

        if (forecast is null)
        {
            forecast = new DownsideRiskForecast
            {
                StockPoolItemId = item.Id,
                Symbol = item.Symbol,
                Name = item.Name,
                PredictedForDate = target,
                AiQueueItemId = queueItem.Id
            };
            _db.DownsideRiskForecasts.Add(forecast);
        }

        forecast.AssessedAt = now;
        forecast.SessionPhase = AShareMarketSession.Describe(now);
        forecast.PredictedForDate = target;
        forecast.Symbol = item.Symbol;
        forecast.Name = item.Name;
        forecast.StockPoolItemId = item.Id;

        var position = await _db.Positions.FirstOrDefaultAsync(p => p.Symbol == item.Symbol, ct);
        var qty = position?.Quantity ?? 0;
        var sellable = position?.SellableQuantity ?? 0;
        forecast.PositionQty = qty;
        forecast.SellableQuantity = sellable;
        forecast.HasSellableQty = sellable >= 100;

        var quote = await _quotes.GetQuoteAsync(item.Symbol, item.Name, ct);
        var last = quote?.Last ?? 0m;
        forecast.BaselinePrice = last > 0 ? last : null;

        var priceFc = await _db.PriceRangeForecasts
            .Where(f => f.Symbol == item.Symbol
                        && f.ForecastForDate == target
                        && f.Source != ForecastSource.Placeholder)
            .OrderBy(f => f.Source == ForecastSource.CalibratedAi ? 0
                : f.Source == ForecastSource.Ai ? 1
                : f.Source == ForecastSource.AtrBaseline ? 2 : 9)
            .ThenByDescending(f => f.PredictedAt)
            .FirstOrDefaultAsync(ct);

        var (ruleLevel, ruleDropPct, ruleNote) = EvaluateRule(
            last,
            position?.AvgCost ?? 0,
            priceFc?.PredictedLow,
            priceFc?.PredictedHigh);

        object? hotNews = null;
        try
        {
            var news = await _news.GetHotNewsAsync(10, ct);
            hotNews = news.Select(n => new { n.Title, n.Source, n.PublishedAt }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DownsideRisk news failed for {Symbol}", item.Symbol);
        }

        var prompt =
            $"你是A股风控助手。请评估 {item.Symbol} {item.Name} 在下一交易日 {target:yyyy-MM-dd} 是否有明显下跌风险，" +
            "帮助投资者决定是否在今日（可卖仓）提前减仓/清仓。只输出 JSON，字段：" +
            "riskLevel(High|Medium|Low)、expectedDropPct(负数)、downsideProbPct(0-100)、" +
            "suggestAction(SellToday|Reduce|Hold)、reason(中文简述)、evidence(字符串数组可选)。" +
            "勿承诺收益；若数据不足标 Low 并说明。";

        var payload = JsonSerializer.Serialize(new
        {
            symbol = item.Symbol,
            name = item.Name,
            predictedForDate = target.ToString("yyyy-MM-dd"),
            assessedAt = now.ToString("yyyy-MM-dd HH:mm:ss"),
            session = forecast.SessionPhase,
            lastPrice = last > 0 ? last : (decimal?)null,
            avgCost = position?.AvgCost,
            positionQty = qty,
            sellableQuantity = sellable,
            predictedHigh = priceFc?.PredictedHigh,
            predictedLow = priceFc?.PredictedLow,
            forecastSource = priceFc?.Source.ToString(),
            ruleLevel = ruleLevel.ToString(),
            ruleDropPct,
            ruleNote,
            hotNews
        }, JsonOpts);

        queueItem.InputPayload = payload;
        DownsideRiskLevel aiLevel = ruleLevel;
        decimal? expectedDrop = ruleDropPct;
        decimal? prob = null;
        string? suggest = NextDayRiskMetrics.DefaultSuggestAction(ruleLevel, forecast.HasSellableQty);
        string? reason = ruleNote;
        var source = "rule";
        string? aiRaw = null;

        try
        {
            aiRaw = await _ai.RunStrategyUpdateAsync(prompt, payload, ct);
            queueItem.OutputPayload = aiRaw;
            if (TryParseAi(aiRaw, out var parsed))
            {
                aiLevel = parsed.Level;
                expectedDrop = parsed.ExpectedDropPct ?? expectedDrop;
                prob = parsed.DownsideProbPct;
                if (!string.IsNullOrWhiteSpace(parsed.SuggestAction))
                    suggest = parsed.SuggestAction;
                if (!string.IsNullOrWhiteSpace(parsed.Reason))
                    reason = parsed.Reason;
                source = "hybrid";
            }
            else
            {
                _logger.LogWarning("DownsideRisk AI parse failed for {Symbol}, keep rule result", item.Symbol);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DownsideRisk AI failed for {Symbol}, keep rule result", item.Symbol);
            queueItem.OutputPayload = aiRaw;
            forecast.ErrorMessage = ex.Message;
        }

        var finalLevel = NextDayRiskMetrics.Max(ruleLevel, aiLevel);
        if (string.IsNullOrWhiteSpace(suggest))
            suggest = NextDayRiskMetrics.DefaultSuggestAction(finalLevel, forecast.HasSellableQty);

        forecast.RiskLevel = finalLevel;
        forecast.ExpectedDropPct = expectedDrop;
        forecast.DownsideProbPct = prob;
        forecast.SuggestAction = suggest;
        forecast.Reason = reason;
        forecast.Source = source;
        forecast.Status = DownsideRiskStatus.Ready;
        forecast.EvidenceJson = JsonSerializer.Serialize(new
        {
            ruleLevel = ruleLevel.ToString(),
            ruleDropPct,
            ruleNote,
            aiLevel = aiLevel.ToString(),
            finalLevel = finalLevel.ToString(),
            predictedLow = priceFc?.PredictedLow,
            predictedHigh = priceFc?.PredictedHigh
        }, JsonOpts);

        await _db.SaveChangesAsync(ct);

        if (item.NotifyOnTrade && finalLevel >= DownsideRiskLevel.Medium)
        {
            try
            {
                var side = finalLevel == DownsideRiskLevel.High ? "风险预警高" : "风险预警中";
                await _alerts.NotifyAsync(new TradeAlertMessage(
                    item.Symbol,
                    item.Name,
                    side,
                    Math.Max(sellable, qty),
                    last > 0 ? last : (position?.AvgCost ?? 0),
                    now,
                    $"次日风险 {NextDayRiskMetrics.ToLabel(finalLevel)} · {suggest} · {target:MM-dd}"), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DownsideRisk alert failed for {Symbol}", item.Symbol);
            }
        }
    }

    /// <summary>日终：对已到期且未对账的 Ready 记录写命中。</summary>
    public async Task<int> EvaluatePendingAsync(DateTime localNow, CancellationToken ct = default)
    {
        var rows = await _db.DownsideRiskForecasts
            .Where(f => f.Status == DownsideRiskStatus.Ready
                        && f.BaselinePrice != null
                        && f.BaselinePrice > 0
                        && f.EvaluatedAt == null)
            .ToListAsync(ct);

        var n = 0;
        foreach (var f in rows)
        {
            if (!PriceForecastMetrics.CanFinalizeForecast(f.PredictedForDate, localNow))
                continue;

            StockDetailBundle? detail;
            try
            {
                detail = await _detailProvider.GetDetailAsync(f.Symbol, f.Name, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DownsideRisk evaluate detail failed {Symbol}", f.Symbol);
                continue;
            }

            if (detail is null || detail.IsSimulated) continue;
            var bar = detail.DailyBars.FirstOrDefault(d => d.Date.Date == f.PredictedForDate.Date);
            if (bar is null || bar.Close <= 0) continue;

            var (ret, dirHit, strongHit) = NextDayRiskMetrics.Evaluate(
                f.BaselinePrice!.Value, bar.Close);
            f.ActualClose = bar.Close;
            f.NextDayReturnPct = ret;
            f.DirectionHit = dirHit;
            f.StrongHit = strongHit;
            f.EvaluatedAt = localNow;
            f.Status = DownsideRiskStatus.Evaluated;
            n++;
        }

        if (n > 0) await _db.SaveChangesAsync(ct);
        return n;
    }

    /// <summary>池列表：每股最新 Ready/Evaluated 结果 + 近史命中率。</summary>
    public async Task<IReadOnlyDictionary<string, DownsideRiskSummaryDto>> GetLatestBySymbolsAsync(
        IEnumerable<string> symbols,
        CancellationToken ct = default)
    {
        var list = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();
        if (list.Count == 0)
            return new Dictionary<string, DownsideRiskSummaryDto>(StringComparer.OrdinalIgnoreCase);

        var lookback = DateTime.Today.AddDays(-60);
        var rows = await _db.DownsideRiskForecasts
            .Where(f => list.Contains(f.Symbol)
                        && f.Status != DownsideRiskStatus.Failed
                        && f.AssessedAt >= lookback)
            .OrderByDescending(f => f.AssessedAt)
            .ToListAsync(ct);

        var map = new Dictionary<string, DownsideRiskSummaryDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in rows.GroupBy(r => r.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            var latest = g.First();
            var evaluated = g.Where(x => x.Status == DownsideRiskStatus.Evaluated && x.DirectionHit != null).ToList();
            decimal? dirRate = null;
            decimal? strongRate = null;
            if (evaluated.Count > 0)
            {
                dirRate = Math.Round(100m * evaluated.Count(x => x.DirectionHit == true) / evaluated.Count, 1);
                strongRate = Math.Round(100m * evaluated.Count(x => x.StrongHit == true) / evaluated.Count, 1);
            }

            map[g.Key] = new DownsideRiskSummaryDto(
                latest.RiskLevel,
                latest.Status,
                latest.PredictedForDate,
                latest.AssessedAt,
                latest.ExpectedDropPct,
                latest.DownsideProbPct,
                latest.SuggestAction,
                latest.Reason,
                latest.HasSellableQty,
                latest.Source,
                dirRate,
                strongRate,
                evaluated.Count);
        }

        return map;
    }

    private static (DownsideRiskLevel Level, decimal? DropPct, string Note) EvaluateRule(
        decimal last,
        decimal avgCost,
        decimal? predictedLow,
        decimal? predictedHigh)
    {
        if (last <= 0 && avgCost <= 0)
            return (DownsideRiskLevel.Low, null, "缺少现价/成本，规则快检跳过");

        var refPrice = last > 0 ? last : avgCost;
        if (predictedLow is null or <= 0)
            return (DownsideRiskLevel.Low, null, "暂无目标日预测低价，规则维持低风险");

        var dropPct = Math.Round((predictedLow.Value - refPrice) / refPrice * 100m, 2);
        var level = NextDayRiskMetrics.FromPredictedDropPct(dropPct);
        var hi = predictedHigh is > 0 ? predictedHigh.Value.ToString("F2") : "-";
        var note =
            $"规则快检：预测低 {predictedLow:F2} / 高 {hi}，相对参考价 {refPrice:F2} 回撤 {dropPct:F2}% → {NextDayRiskMetrics.ToLabel(level)}";
        return (level, dropPct, note);
    }

    private static bool TryParseAi(string raw, out (DownsideRiskLevel Level, decimal? ExpectedDropPct, decimal? DownsideProbPct, string? SuggestAction, string? Reason) parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        try
        {
            var text = raw.Trim();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
                text = text[start..(end + 1)];

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var levelStr = root.TryGetProperty("riskLevel", out var lv) ? lv.GetString()
                : root.TryGetProperty("RiskLevel", out var lv2) ? lv2.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(levelStr)) return false;

            var level = levelStr.Trim().ToLowerInvariant() switch
            {
                "high" or "高" or "高风险" => DownsideRiskLevel.High,
                "medium" or "mid" or "中" or "中风险" => DownsideRiskLevel.Medium,
                _ => DownsideRiskLevel.Low
            };

            decimal? drop = null;
            if (root.TryGetProperty("expectedDropPct", out var d) && d.ValueKind == JsonValueKind.Number)
                drop = d.GetDecimal();
            decimal? prob = null;
            if (root.TryGetProperty("downsideProbPct", out var p) && p.ValueKind == JsonValueKind.Number)
                prob = p.GetDecimal();

            string? action = null;
            if (root.TryGetProperty("suggestAction", out var a))
                action = a.GetString();
            string? reason = null;
            if (root.TryGetProperty("reason", out var r))
                reason = r.GetString();

            parsed = (level, drop, prob, action, reason);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
