using StockSim.Domain.Entities;
using StockSim.Domain.MarketData;

namespace StockSim.Domain.Strategies;

public sealed class EvaluateContext
{
    public required StrategyParameters Policy { get; init; }
    public required EffectiveStrategyParams Effective { get; init; }
    public required QuoteTick Quote { get; init; }
    public Position? Position { get; init; }
    public required SymbolRuntimeState Runtime { get; init; }
    public decimal AnchorPrice { get; init; }
    public decimal DeviationPct { get; init; }
    public decimal VolumeRatio { get; init; } = 1m;
    public decimal RealizedVolPct { get; init; }
    public DateTime Now { get; init; } = DateTime.Now;
    public Guid StrategyDefinitionId { get; init; }
    public Guid? StockPoolId { get; init; }
    public int TradesToday { get; init; }

    /// <summary>该股可用资金（建底仓预算）。</summary>
    public decimal AvailableCash { get; init; }

    /// <summary>是否已到允许做 T 的日期（含当日）。默认 true 兼容旧调用。</summary>
    public bool CanDoT { get; init; } = true;

    /// <summary>是否仍需建底仓（持仓不足 100）。</summary>
    public bool NeedsBaseBuild { get; init; }

    /// <summary>相对今日高点的回撤 %（冲高回落卖用）。</summary>
    public decimal PullbackFromHighPct { get; init; }

    /// <summary>软边界钳制参考价（昨收或开盘）。</summary>
    public decimal ReferencePrice { get; init; }
}
