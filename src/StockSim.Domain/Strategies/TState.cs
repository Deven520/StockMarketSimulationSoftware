namespace StockSim.Domain.Strategies;

/// <summary>日内做T状态机。</summary>
public enum TState
{
    /// <summary>观望，偏好先高抛。</summary>
    Idle = 0,
    /// <summary>已高抛，等待低吸买回。</summary>
    SoldHigh = 1,
    /// <summary>已低吸（补仓），等待高抛。</summary>
    BoughtLow = 2,
    /// <summary>日亏/连亏/涨跌停附近暂停。</summary>
    RiskHalt = 3,
    /// <summary>落在 avoid 时段。</summary>
    SessionAvoid = 4,
    /// <summary>临近收盘只平不建。</summary>
    ForceFlat = 5,
    /// <summary>入池当日（做T日之前）：观察行情并伺机建底仓。</summary>
    BuildingBase = 6
}

public sealed class SymbolRuntimeState
{
    public string Symbol { get; set; } = string.Empty;
    public DateTime TradeDate { get; set; } = DateTime.Today;
    public TState State { get; set; } = TState.Idle;
    public int ConsecutiveLosses { get; set; }
    public decimal DayRealizedPnL { get; set; }
    public decimal? LastSellPrice { get; set; }
    public decimal? LastBuyPrice { get; set; }
    /// <summary>未平正 T 卖出腿股数；买回后递减，至 0 清空 LastSellPrice。</summary>
    public int OpenTQty { get; set; }
    public IntradayPathRegime PathRegime { get; set; } = IntradayPathRegime.Unknown;
    public PathPhase PathPhase { get; set; } = PathPhase.Hypothesis;
    public string? PathReason { get; set; }
    public RuntimeOverrides Overrides { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public void ResetIfNewDay(DateTime today)
    {
        if (TradeDate.Date == today.Date) return;
        TradeDate = today.Date;
        State = TState.Idle;
        ConsecutiveLosses = 0;
        DayRealizedPnL = 0;
        LastSellPrice = null;
        LastBuyPrice = null;
        OpenTQty = 0;
        PathRegime = IntradayPathRegime.Unknown;
        PathPhase = PathPhase.Hypothesis;
        PathReason = null;
        Overrides = new RuntimeOverrides();
        UpdatedAt = DateTime.UtcNow;
    }
}
