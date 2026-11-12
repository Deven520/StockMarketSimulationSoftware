using StockSim.Domain.Common;

namespace StockSim.Domain.Entities;

/// <summary>每股日内做T运行时状态（可跨进程恢复）。</summary>
public class StrategyRuntimeStateRow : AuditedEntity
{
    public string Symbol { get; set; } = string.Empty;
    public DateTime TradeDate { get; set; }
    public string State { get; set; } = "Idle";
    public int ConsecutiveLosses { get; set; }
    public decimal DayRealizedPnL { get; set; }
    public decimal? LastSellPrice { get; set; }
    public decimal? LastBuyPrice { get; set; }
    /// <summary>未平正 T 卖出腿股数。</summary>
    public int OpenTQty { get; set; }
    public string PathRegime { get; set; } = "Unknown";
    public string PathPhase { get; set; } = "Hypothesis";
    public string? OverridesJson { get; set; }
}

/// <summary>策略决策日志（可解释复盘 / AI 夜更输入）。</summary>
public class StrategyDecisionLog : EntityBase
{
    public string Symbol { get; set; } = string.Empty;
    public DateTime TradeDate { get; set; }
    public DateTime DecidedAt { get; set; } = DateTime.UtcNow;
    public string State { get; set; } = string.Empty;
    public string Action { get; set; } = "None";
    public string Reason { get; set; } = string.Empty;
    public decimal? LastPrice { get; set; }
    public decimal? AnchorPrice { get; set; }
    public decimal? DeviationPct { get; set; }
    public string? EffectiveParamsJson { get; set; }
    public Guid? StrategyDefinitionId { get; set; }
    public string? BlockedBy { get; set; }
}
