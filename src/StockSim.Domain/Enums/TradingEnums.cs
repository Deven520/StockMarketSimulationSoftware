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

public enum AiJobType
{
    StockPick = 1,
    /// <summary>工作台策略建议（已废弃，保留枚举兼容历史数据）。</summary>
    StrategyUpdate = 2,
    /// <summary>由投资目标生成选股提示词。</summary>
    GoalPrompt = 3
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
    StrategyRefresh = 3
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
