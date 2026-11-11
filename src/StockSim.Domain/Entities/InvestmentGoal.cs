using StockSim.Domain.Common;
using StockSim.Domain.Enums;

namespace StockSim.Domain.Entities;

/// <summary>用户投资目标（驱动 AI 选股与达成率跟踪）。</summary>
public class InvestmentGoal : AuditedEntity
{
    /// <summary>预期收益（百分比，如 3 表示 +3%）。</summary>
    public decimal ExpectedReturnPct { get; set; }

    /// <summary>预期周期（天）。</summary>
    public int HorizonDays { get; set; }

    /// <summary>题材/风格偏好。</summary>
    public string ThemePreference { get; set; } = string.Empty;

    /// <summary>选股类型：做T股 / 趋势股。</summary>
    public StockTradeStyle TradeStyle { get; set; } = StockTradeStyle.IntradayT;

    /// <summary>由目标经 AI 生成的选股提示词。</summary>
    public string? GeneratedPrompt { get; set; }

    /// <summary>生成提示词时的说明。</summary>
    public string? PromptRationale { get; set; }

    public InvestmentGoalStatus Status { get; set; } = InvestmentGoalStatus.Draft;

    public DateTime? StartDate { get; set; }
    public DateTime? TargetEndDate { get; set; }

    /// <summary>关联的选股 AiJob。</summary>
    public Guid? StockPickJobId { get; set; }

    public ICollection<InvestmentGoalStock> Stocks { get; set; } = new List<InvestmentGoalStock>();
    public ICollection<GoalProgressDaily> ProgressDays { get; set; } = new List<GoalProgressDaily>();
}

public class InvestmentGoalStock : AuditedEntity
{
    public Guid InvestmentGoalId { get; set; }
    public InvestmentGoal? InvestmentGoal { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? PoolItemId { get; set; }
    public Guid? SuggestionId { get; set; }

    /// <summary>入选时计入的基准资金（默认 20 万）。</summary>
    public decimal BaselineCash { get; set; } = 200_000m;

    /// <summary>入选时参考价（可选）。</summary>
    public decimal? BaselinePrice { get; set; }

    /// <summary>次日预选对账目标日（下一交易日）。</summary>
    public DateTime? TargetTradeDate { get; set; }

    /// <summary>目标日收盘价（真源日K）。</summary>
    public decimal? ActualClose { get; set; }

    /// <summary>次日收益%：(ActualClose−BaselinePrice)/BaselinePrice×100。</summary>
    public decimal? NextDayReturnPct { get; set; }

    /// <summary>方向命中：次日收益 &gt; 0。</summary>
    public bool? DirectionHit { get; set; }

    /// <summary>目标命中：次日收益 ≥ 目标预期收益%。</summary>
    public bool? TargetHit { get; set; }

    public DateTime? EvaluatedAt { get; set; }
}

public class GoalProgressDaily : EntityBase
{
    public Guid InvestmentGoalId { get; set; }
    public InvestmentGoal? InvestmentGoal { get; set; }
    public DateTime TradeDate { get; set; }

    /// <summary>相对起点的组合收益 %。</summary>
    public decimal PortfolioReturnPct { get; set; }

    /// <summary>相对预期收益的达成率 %（可 &gt;100）。</summary>
    public decimal AchievementRate { get; set; }

    public decimal TotalMarketValue { get; set; }
    public decimal TotalBaseline { get; set; }
    public decimal RealizedPnL { get; set; }
    public decimal UnrealizedPnL { get; set; }
    public int DayIndex { get; set; }
    public string? Notes { get; set; }
}
