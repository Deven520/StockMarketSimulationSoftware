namespace StockSim.Domain.Enums;

public enum OrderSide
{
    Buy = 1,
    Sell = 2
}

public enum OrderStatus
{
    Pending = 0,
    Filled = 1,
    Rejected = 2,
    Cancelled = 3
}

public enum PoolItemSource
{
    Manual = 1,
    Ai = 2
}

/// <summary>股票交易/选股风格：做T vs 趋势波段 vs 次日上涨预选。</summary>
public enum StockTradeStyle
{
    /// <summary>做T股：日内高抛低吸（默认，兼容旧数据）。</summary>
    IntradayT = 1,
    /// <summary>趋势股：底部反转/中期趋势观察，不做自动做T。</summary>
    TrendSwing = 2,
    /// <summary>次日预涨：结合当日/历史/新闻预选下一交易日偏多标的；池内单独分组，执行同趋势（不做自动做T）。</summary>
    NextDayRise = 3
}

/// <summary><see cref="StockTradeStyle"/> 展示与池内映射。</summary>
public static class StockTradeStyleHelper
{
    public static string ToLabel(StockTradeStyle style) => style switch
    {
        StockTradeStyle.TrendSwing => "趋势股",
        StockTradeStyle.NextDayRise => "次日预涨",
        _ => "做T股"
    };

    /// <summary>池内执行：次日预涨与趋势股一样不做自动做T（可低位建仓）。</summary>
    public static bool IsTrendLike(StockTradeStyle style) =>
        style is StockTradeStyle.TrendSwing or StockTradeStyle.NextDayRise;
}

public enum AiJobType
{
    StockPick = 1,
    /// <summary>工作台策略建议（已废弃，保留枚举兼容历史数据）。</summary>
    StrategyUpdate = 2,
    /// <summary>由投资目标生成选股提示词。</summary>
    GoalPrompt = 3,
    /// <summary>做T股每日收盘复盘。</summary>
    DailyTReview = 4,
    /// <summary>次日预涨选股复盘。</summary>
    NextDayPickReview = 5
}

public enum InvestmentGoalStatus
{
    Draft = 0,
    /// <summary>已生成候选，待用户勾选入库。</summary>
    PendingSelection = 1,
    Active = 2,
    Completed = 3,
    Cancelled = 4
}

public enum AiJobStatus
{
    Created = 0,
    Running = 1,
    SucceededPendingReview = 2,
    Accepted = 3,
    Rejected = 4,
    Failed = 5
}

/// <summary>统一 AI 队列任务类型。</summary>
public enum AiQueueTaskType
{
    GoalToPrompt = 1,
    StockPick = 2,
    StrategyRefresh = 3,
    /// <summary>池内单股：开市时段预测下一交易日下跌风险。</summary>
    DownsideRiskPredict = 4,
    /// <summary>做T股：收盘后每日复盘（提示词+结果入库，供夜更优化）。</summary>
    DailyTReview = 5,
    /// <summary>次日预涨：结算后复盘（供下一批选股注入）。</summary>
    NextDayPickReview = 6
}

/// <summary>次日预涨选股复盘状态。</summary>
public enum NextDayPickReviewStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4
}

/// <summary>做T每日复盘状态。</summary>
public enum DailyTReviewStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4
}

/// <summary>次日下跌风险等级。</summary>
public enum DownsideRiskLevel
{
    Low = 1,
    Medium = 2,
    High = 3
}

/// <summary>风险预测记录状态。</summary>
public enum DownsideRiskStatus
{
    Pending = 0,
    Ready = 1,
    Failed = 2,
    Evaluated = 3
}

/// <summary>统一 AI 队列项状态。</summary>
public enum AiQueueItemStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4
}

public enum TradingMode
{
    /// <summary>模拟允许当日买入后卖出，便于做T演示。</summary>
    SimIntradaySellAllowed = 1,
    /// <summary>严格 T+1：当日买入不可卖。</summary>
    TPlusOneStrict = 2
}

/// <summary>次日高低价预测对账状态。</summary>
public enum ForecastStatus
{
    Pending = 0,
    Evaluated = 1,
    Skipped = 2
}

/// <summary>高低价预测来源。</summary>
public enum ForecastSource
{
    /// <summary>模型/AI 直接输出。</summary>
    Ai = 1,
    /// <summary>近 N 日 ATR 高低带基线（用于对照）。</summary>
    AtrBaseline = 2,
    /// <summary>占位/兜底预测，不得计入准确率榜。</summary>
    Placeholder = 3,
    /// <summary>AI 输出经历史偏差校准后。</summary>
    CalibratedAi = 4
}
