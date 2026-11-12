namespace StockSim.Domain.Strategies;

/// <summary>盘中开盘路径分型（个股）。</summary>
public enum IntradayPathRegime
{
    Unknown = 0,
    HighOpenHighGo = 1,
    HighOpenLowGo = 2,
    LowOpenHighGo = 3,
    LowOpenLowGo = 4
}

public enum PathPhase
{
    Hypothesis = 0,
    Confirmed = 1,
    Locked = 2
}

public enum PathFilterAction
{
    None = 0,
    Confirm = 1,
    Weaken = 2,
    BlockNewT = 3
}

/// <summary>软边界判定：放行 / 缩仓偏好 / 硬拦截。</summary>
public enum SoftBandVerdict
{
    Pass = 0,
    PreferShrink = 1,
    Block = 2
}
