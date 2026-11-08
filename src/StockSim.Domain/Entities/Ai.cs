using StockSim.Domain.Common;
using StockSim.Domain.Enums;

namespace StockSim.Domain.Entities;

public class AiJob : AuditedEntity
{
    public AiJobType JobType { get; set; }
    public AiJobStatus Status { get; set; } = AiJobStatus.Created;
    /// <summary>用户输入的提示词。</summary>
    public string Prompt { get; set; } = string.Empty;
    public string? InputPayload { get; set; }
    public string? OutputPayload { get; set; }
    /// <summary>AI 选股/策略分析过程说明（可读文本，供查阅）。</summary>
    public string? AnalysisSummary { get; set; }
    /// <summary>分析步骤 JSON，如 ["理解提示词", "筛选行业", ...]。</summary>
    public string? AnalysisStepsJson { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? CompletedAt { get; set; }
    public ICollection<AiSuggestion> Suggestions { get; set; } = new List<AiSuggestion>();
}

public class AiSuggestion : EntityBase
{
    public Guid AiJobId { get; set; }
    public AiJob? AiJob { get; set; }
    public string PayloadJson { get; set; } = "{}";
    /// <summary>结构化展示字段（从 Payload 解析）。</summary>
    public string? Symbol { get; set; }
    public string? Name { get; set; }
    /// <summary>简短入选理由。</summary>
    public string? Reason { get; set; }
    /// <summary>AI 返回的该股选取逻辑（可较长，供历史查阅）。</summary>
    public string? SelectionLogic { get; set; }
    public string? Risk { get; set; }
    public bool IsAccepted { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
}

/// <summary>统一 AI 异步队列项（选股/策略刷新等）。</summary>
public class AiQueueItem : AuditedEntity
{
    public AiQueueTaskType TaskType { get; set; }
    public AiQueueItemStatus Status { get; set; } = AiQueueItemStatus.Pending;
    /// <summary>列表展示标题，如「选股提示词 · 新能源」。</summary>
    public string Title { get; set; } = string.Empty;
    public Guid? CorrelationId { get; set; }
    public Guid? RelatedGoalId { get; set; }
    public Guid? RelatedAiJobId { get; set; }
    public Guid? RelatedPoolItemId { get; set; }
    public string? Symbol { get; set; }
    /// <summary>入参 JSON 或提示词文本。</summary>
    public string? InputPayload { get; set; }
    /// <summary>Agent 原始输出。</summary>
    public string? OutputPayload { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>抢占执行的进程标识，便于排查双实例。</summary>
    public string? WorkerId { get; set; }
}
