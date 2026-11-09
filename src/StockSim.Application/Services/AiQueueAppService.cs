using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockSim.Application.Dtos;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;

namespace StockSim.Application.Services;

/// <summary>统一 AI 队列：入队、查询、抢占执行。</summary>
public sealed class AiQueueAppService
{
    private readonly IStockSimDataStore _db;
    private readonly InvestmentGoalAppService _goals;
    private readonly PerStockStrategyService _perStock;
    private readonly ILogger<AiQueueAppService> _logger;
    private static readonly string WorkerId =
        $"{Environment.MachineName}:{Environment.ProcessId}";

    public AiQueueAppService(
        IStockSimDataStore db,
        InvestmentGoalAppService goals,
        PerStockStrategyService perStock,
        ILogger<AiQueueAppService> logger)
    {
        _db = db;
        _goals = goals;
        _perStock = perStock;
        _logger = logger;
    }

    public async Task<AiQueueItem> EnqueueAsync(
        AiQueueTaskType taskType,
        string title,
        string? inputPayload,
        Guid? correlationId = null,
        Guid? relatedGoalId = null,
        Guid? relatedAiJobId = null,
        Guid? relatedPoolItemId = null,
        string? symbol = null,
        CancellationToken ct = default)
    {
        var item = new AiQueueItem
        {
            TaskType = taskType,
            Status = AiQueueItemStatus.Pending,
            Title = title,
            CorrelationId = correlationId ?? Guid.NewGuid(),
            RelatedGoalId = relatedGoalId,
            RelatedAiJobId = relatedAiJobId,
            RelatedPoolItemId = relatedPoolItemId,
            Symbol = symbol,
            InputPayload = inputPayload
        };
        _db.AiQueueItems.Add(item);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("AI queue enqueued {Id} {Type} {Title}", item.Id, taskType, title);
        return item;
    }

    public Task<AiQueueItem> EnqueueStrategyRefreshAsync(
        Guid poolItemId,
        string symbol,
        string name,
        bool nightly,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { poolItemId, symbol, name, nightly });
        return EnqueueAsync(
            AiQueueTaskType.StrategyRefresh,
            nightly ? $"夜更策略 · {name}({symbol})" : $"策略刷新 · {name}({symbol})",
            payload,
            relatedPoolItemId: poolItemId,
            symbol: symbol,
            ct: ct);
    }

    public async Task<int> EnqueueStrategyRefreshAllAsync(bool nightly, CancellationToken ct = default)
    {
        // 含趋势股：夜更刷新高低预测，供下一交易日低位建仓（引擎仍跳过做T）
        var items = await _db.StockPoolItems
            .Where(i => i.BoundStrategyId != null)
            .Select(i => new { i.Id, i.Symbol, i.Name })
            .ToListAsync(ct);
        foreach (var i in items)
            await EnqueueStrategyRefreshAsync(i.Id, i.Symbol, i.Name, nightly, ct);
        return items.Count;
    }

    public async Task<IReadOnlyList<AiQueueItemDto>> ListAsync(
        AiQueueItemStatus? status = null,
        int take = 100,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 500);
        var q = _db.AiQueueItems.AsQueryable();
        if (status.HasValue)
            q = q.Where(x => x.Status == status.Value);

        var rows = await q
            .OrderByDescending(x => x.CreationTime)
            .Take(take)
            .ToListAsync(ct);

        return rows.Select(MapDto).ToList();
    }

    public async Task<(int Pending, int Running, int Succeeded, int Failed)> CountByStatusAsync(
        CancellationToken ct = default)
    {
        var groups = await _db.AiQueueItems
            .GroupBy(x => x.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int Get(AiQueueItemStatus s) => groups.FirstOrDefault(x => x.Key == s)?.Count ?? 0;
        return (Get(AiQueueItemStatus.Pending), Get(AiQueueItemStatus.Running),
            Get(AiQueueItemStatus.Succeeded), Get(AiQueueItemStatus.Failed));
    }

    public async Task<AiQueueItemDetailDto?> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        var x = await _db.AiQueueItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (x is null) return null;
        return new AiQueueItemDetailDto(
            x.Id, x.TaskType, x.Status, x.Title, x.CorrelationId,
            x.RelatedGoalId, x.RelatedAiJobId, x.RelatedPoolItemId, x.Symbol,
            x.InputPayload, x.OutputPayload, x.ErrorMessage,
            x.CreationTime, x.StartedAt, x.CompletedAt, x.WorkerId);
    }

    /// <summary>删除队列项；Running 中不可删，Pending 标记取消后删除。</summary>
    public async Task<(bool Ok, string Message)> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var item = await _db.AiQueueItems.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return (false, "队列项不存在");
        if (item.Status == AiQueueItemStatus.Running)
            return (false, "执行中任务不可删除，请稍后再试");

        _db.AiQueueItems.Remove(item);
        await _db.SaveChangesAsync(ct);
        return (true, "已删除队列项");
    }

    /// <summary>抢占并执行下一条 Pending；无任务返回 false。</summary>
    public async Task<bool> TryProcessNextAsync(CancellationToken ct = default)
    {
        var next = await _db.AiQueueItems
            .Where(x => x.Status == AiQueueItemStatus.Pending)
            .OrderBy(x => x.CreationTime)
            .FirstOrDefaultAsync(ct);
        if (next is null) return false;

        // 乐观抢占
        next.Status = AiQueueItemStatus.Running;
        next.StartedAt = DateTime.UtcNow;
        next.WorkerId = WorkerId;
        next.LastModificationTime = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        try
        {
            await ExecuteAsync(next, ct);
            next.Status = AiQueueItemStatus.Succeeded;
            next.CompletedAt = DateTime.UtcNow;
            next.ErrorMessage = null;
            next.LastModificationTime = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("AI queue item {Id} succeeded ({Type})", next.Id, next.TaskType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI queue item {Id} failed ({Type})", next.Id, next.TaskType);
            next.Status = AiQueueItemStatus.Failed;
            next.CompletedAt = DateTime.UtcNow;
            next.ErrorMessage = ex.Message;
            next.LastModificationTime = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return true;
    }

    private async Task ExecuteAsync(AiQueueItem item, CancellationToken ct)
    {
        switch (item.TaskType)
        {
            case AiQueueTaskType.GoalToPrompt:
                await _goals.ProcessQueuedGoalToPromptAsync(item, ct);
                break;
            case AiQueueTaskType.StockPick:
                await _goals.ProcessQueuedStockPickAsync(item, ct);
                break;
            case AiQueueTaskType.StrategyRefresh:
                await ProcessStrategyRefreshAsync(item, ct);
                break;
            default:
                throw new InvalidOperationException($"未知队列任务类型 {item.TaskType}");
        }
    }

    private async Task ProcessStrategyRefreshAsync(AiQueueItem item, CancellationToken ct)
    {
        if (!item.RelatedPoolItemId.HasValue)
            throw new InvalidOperationException("StrategyRefresh 缺少 RelatedPoolItemId");

        var nightly = false;
        JsonNode? enqueueNode = null;
        if (!string.IsNullOrWhiteSpace(item.InputPayload))
        {
            try
            {
                enqueueNode = JsonNode.Parse(item.InputPayload);
                if (enqueueNode is JsonObject eo
                    && eo.TryGetPropertyValue("nightly", out var n)
                    && n is JsonValue jv
                    && jv.TryGetValue<bool>(out var nb))
                    nightly = nb;
            }
            catch { /* ignore */ }
        }

        var result = nightly
            ? await _perStock.RefreshItemAsync(item.RelatedPoolItemId.Value, ct)
            : await _perStock.GenerateForNewItemAsync(item.RelatedPoolItemId.Value, ct);

        if (result.AiJobId.HasValue)
            item.RelatedAiJobId = result.AiJobId;

        // 回写完整提示词 + 入参上下文，便于队列详情复盘
        JsonNode? contextNode = null;
        if (!string.IsNullOrWhiteSpace(result.InputPayload))
        {
            try { contextNode = JsonNode.Parse(result.InputPayload); }
            catch { contextNode = JsonValue.Create(result.InputPayload); }
        }

        var inputRoot = new JsonObject
        {
            ["enqueue"] = enqueueNode ?? new JsonObject
            {
                ["poolItemId"] = item.RelatedPoolItemId?.ToString(),
                ["symbol"] = item.Symbol,
                ["nightly"] = nightly
            },
            ["prompt"] = result.Prompt,
            ["context"] = contextNode
        };
        item.InputPayload = inputRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        item.OutputPayload = !string.IsNullOrWhiteSpace(result.OutputPayload)
            ? result.OutputPayload
            : result.Message;

        if (!result.Ok)
            throw new InvalidOperationException(result.Message);
    }

    private static AiQueueItemDto MapDto(AiQueueItem x) => new(
        x.Id, x.TaskType, x.Status, x.Title, x.CorrelationId,
        x.RelatedGoalId, x.RelatedAiJobId, x.RelatedPoolItemId, x.Symbol,
        x.CreationTime, x.StartedAt, x.CompletedAt, x.ErrorMessage,
        !string.IsNullOrWhiteSpace(x.OutputPayload));
}
