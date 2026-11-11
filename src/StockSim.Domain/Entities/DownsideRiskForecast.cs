using StockSim.Domain.Common;
using StockSim.Domain.Enums;

namespace StockSim.Domain.Entities;

/// <summary>
/// 池内单股「次日下跌风险」预测及对账。开市时段触发，目标日为下一交易日。
/// </summary>
public class DownsideRiskForecast : EntityBase
{
    public Guid? StockPoolItemId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>被预测的下一交易日。</summary>
    public DateTime PredictedForDate { get; set; }

    /// <summary>评估时间（本地）。</summary>
    public DateTime AssessedAt { get; set; } = DateTime.Now;

    /// <summary>上午/下午等盘中阶段说明。</summary>
    public string? SessionPhase { get; set; }

    public DownsideRiskLevel RiskLevel { get; set; } = DownsideRiskLevel.Low;
    public DownsideRiskStatus Status { get; set; } = DownsideRiskStatus.Pending;

    /// <summary>模型估计的下跌概率 0–100（可选）。</summary>
    public decimal? DownsideProbPct { get; set; }

    /// <summary>预期跌幅%（通常为负，如 -4.5）。</summary>
    public decimal? ExpectedDropPct { get; set; }

    /// <summary>建议动作：SellToday / Reduce / Hold。</summary>
    public string? SuggestAction { get; set; }

    public string? Reason { get; set; }

    /// <summary>规则快检与证据 JSON。</summary>
    public string? EvidenceJson { get; set; }

    /// <summary>触发时最新价（对账基准）。</summary>
    public decimal? BaselinePrice { get; set; }

    public int PositionQty { get; set; }
    public int SellableQuantity { get; set; }
    public bool HasSellableQty { get; set; }

    public Guid? AiQueueItemId { get; set; }

    /// <summary>来源：ai / rule / hybrid。</summary>
    public string Source { get; set; } = "hybrid";

    public decimal? ActualClose { get; set; }
    public decimal? NextDayReturnPct { get; set; }
    public bool? DirectionHit { get; set; }
    public bool? StrongHit { get; set; }
    public DateTime? EvaluatedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
