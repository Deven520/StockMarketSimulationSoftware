using StockSim.Domain.Common;
using StockSim.Domain.Enums;

namespace StockSim.Domain.Entities;

/// <summary>
/// 次日预涨选股复盘：对账后 AI 分析错因与下批选股 hints，供 GoalToPrompt/StockPick 注入。
/// </summary>
public class NextDayPickReview : EntityBase
{
    public Guid? InvestmentGoalId { get; set; }
    public Guid? GoalStockId { get; set; }
    public Guid? StockPoolItemId { get; set; }

    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>预涨目标交易日。</summary>
    public DateTime TargetTradeDate { get; set; }

    public NextDayPickReviewStatus Status { get; set; } = NextDayPickReviewStatus.Pending;

    public decimal? BaselinePrice { get; set; }
    public decimal? ActualClose { get; set; }
    public decimal? ReturnPct { get; set; }
    public bool? DirectionHit { get; set; }
    public bool? TargetHit { get; set; }

    /// <summary>相对指数（如创业板指）超额收益%。</summary>
    public decimal? RelativeStrengthPct { get; set; }

    public string? ThemePreference { get; set; }
    public decimal? ExpectedReturnPct { get; set; }

    /// <summary>错因标签 JSON 数组，如 ["ChaseHigh","ThemeFade"]。</summary>
    public string? FailureTagsJson { get; set; }

    public string? Prompt { get; set; }
    public string? InputPayload { get; set; }
    public string? OutputPayload { get; set; }
    public string? Summary { get; set; }
    public string? FindingsJson { get; set; }

    /// <summary>给下一批选股的可执行建议 JSON。</summary>
    public string? PickHintsJson { get; set; }

    public string? Grade { get; set; }
    public Guid? AiJobId { get; set; }
    public Guid? AiQueueItemId { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Notes { get; set; }
}
