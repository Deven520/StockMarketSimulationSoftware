using StockSim.Domain.Common;
using StockSim.Domain.Enums;

namespace StockSim.Domain.Entities;

public class StockPool : AuditedEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<StockPoolItem> Items { get; set; } = new List<StockPoolItem>();
}

public class StockPoolItem : AuditedEntity
{
    public Guid StockPoolId { get; set; }
    public StockPool? StockPool { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Tags { get; set; }
    public string? Remark { get; set; }
    public PoolItemSource Source { get; set; } = PoolItemSource.Manual;

    /// <summary>交易风格：做T股走日内状态机；趋势股/次日预涨仅观察/跟踪，不做自动做T。</summary>
    public StockTradeStyle TradeStyle { get; set; } = StockTradeStyle.IntradayT;

    /// <summary>次日预涨目标交易日（仅 NextDayRise 有值）。</summary>
    public DateTime? PredictedRiseDate { get; set; }

    /// <summary>预涨日结算：基准价（预涨日前一交易日收盘）。</summary>
    public decimal? RiseBaselinePrice { get; set; }

    /// <summary>预涨日结算：预涨日收盘价。</summary>
    public decimal? RiseActualClose { get; set; }

    /// <summary>预涨日结算：相对基准收益%。</summary>
    public decimal? RiseReturnPct { get; set; }

    /// <summary>预涨日结算：方向成功（收益&gt;0）。</summary>
    public bool? RiseSuccess { get; set; }

    /// <summary>预涨日结算完成时间（本地）。</summary>
    public DateTime? RiseEvaluatedAt { get; set; }

    /// <summary>一股一策：本成分绑定的专属策略。</summary>
    public Guid? BoundStrategyId { get; set; }
    public StrategyDefinition? BoundStrategy { get; set; }

    /// <summary>入池时 AI 首次生成策略时间。</summary>
    public DateTime? StrategyGeneratedAt { get; set; }

    /// <summary>最近一次夜间/手动 AI 更新策略时间。</summary>
    public DateTime? StrategyUpdatedAt { get; set; }

    /// <summary>该股专属初始资金（默认 20 万）。</summary>
    public decimal InitialCash { get; set; } = 200_000m;

    /// <summary>该股专属可用资金（做T买卖从此账户划转）。</summary>
    public decimal AvailableCash { get; set; } = 200_000m;

    /// <summary>
    /// 允许自动做 T 的起始交易日（含当日）。入池时设为下一交易日；此前仅可按行情建底仓。
    /// 旧数据为 null 时：有底仓即可做 T（兼容）。
    /// </summary>
    public DateTime? AllowTFromDate { get; set; }

    /// <summary>策略成交时是否推送通知（微信 Webhook / PushPlus 等）。</summary>
    public bool NotifyOnTrade { get; set; }
}
