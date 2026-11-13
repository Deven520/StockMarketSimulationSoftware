using StockSim.Domain.Common;
using StockSim.Domain.Enums;

namespace StockSim.Domain.Entities;

/// <summary>
/// 做T股每日收盘复盘：提示词与 AI 结论入库，供夜更策略优化与个股页查阅。
/// </summary>
public class DailyTReview : EntityBase
{
    public Guid? StockPoolItemId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>复盘交易日（本地日历日）。</summary>
    public DateTime TradeDate { get; set; }

    public DailyTReviewStatus Status { get; set; } = DailyTReviewStatus.Pending;

    /// <summary>完整复盘提示词。</summary>
    public string? Prompt { get; set; }

    /// <summary>结构化行情/成交/决策摘要 JSON。</summary>
    public string? InputPayload { get; set; }

    /// <summary>AI 原文输出。</summary>
    public string? OutputPayload { get; set; }

    /// <summary>一句话结论（列表展示）。</summary>
    public string? Summary { get; set; }

    /// <summary>结构化发现：good/bad/path/forecast 等。</summary>
    public string? FindingsJson { get; set; }

    /// <summary>给夜更的可执行建议 JSON。</summary>
    public string? StrategyHintsJson { get; set; }

    /// <summary>等级 A/B/C 或数值字符串。</summary>
    public string? Grade { get; set; }

    /// <summary>按买卖配对估算的 T 价差（元，未含费）。</summary>
    public decimal? CyclePnlApprox { get; set; }

    public decimal? Fees { get; set; }
    public int TradeCount { get; set; }

    public string? PathRegime { get; set; }
    public string? PathPhase { get; set; }

    public Guid? StrategyDefinitionId { get; set; }
    public Guid? StrategyVersionId { get; set; }
    public Guid? AiJobId { get; set; }
    public Guid? AiQueueItemId { get; set; }

    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Notes { get; set; }
}
