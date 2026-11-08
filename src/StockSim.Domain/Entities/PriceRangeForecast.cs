using StockSim.Domain.Common;
using StockSim.Domain.Enums;

namespace StockSim.Domain.Entities;

/// <summary>
/// AI 预测的次日最高/最低价及对账准确率历史。每条记录绑定「预测股票」。
/// </summary>
public class PriceRangeForecast : EntityBase
{
    /// <summary>预测股票代码。</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>预测股票名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>被预测的交易日（通常为下一交易日）。</summary>
    public DateTime ForecastForDate { get; set; }

    /// <summary>预测生成时间（本地）。</summary>
    public DateTime PredictedAt { get; set; } = DateTime.Now;

    public decimal PredictedHigh { get; set; }
    public decimal PredictedLow { get; set; }

    public decimal? ActualHigh { get; set; }
    public decimal? ActualLow { get; set; }

    /// <summary>(预测-实际)/实际 × 100。</summary>
    public decimal? HighDeviationPct { get; set; }
    public decimal? LowDeviationPct { get; set; }

    /// <summary>0–100，容差内得分。</summary>
    public decimal? HighAccuracyPct { get; set; }
    public decimal? LowAccuracyPct { get; set; }
    public decimal? CombinedAccuracyPct { get; set; }

    /// <summary>实际高低是否落在预测区间内（含边界）。</summary>
    public bool? RangeHit { get; set; }

    public DateTime? EvaluatedAt { get; set; }
    public ForecastStatus Status { get; set; } = ForecastStatus.Pending;

    public Guid? StrategyDefinitionId { get; set; }
    public Guid? StrategyVersionId { get; set; }
    public Guid? AiJobId { get; set; }

    /// <summary>生成该策略/预测时使用的 AI 提示词，便于复盘。</summary>
    public string? Prompt { get; set; }

    public string? Notes { get; set; }
}
