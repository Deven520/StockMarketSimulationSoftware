using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;

namespace StockSim.Application.Dtos;

public record StockPoolDto(Guid Id, string Name, string? Description, bool IsActive, int ItemCount);
public record StockPoolItemDto(
    Guid Id,
    Guid StockPoolId,
    string Symbol,
    string Name,
    string? Tags,
    PoolItemSource Source,
    Guid? BoundStrategyId = null,
    DateTime? StrategyUpdatedAt = null,
    decimal InitialCash = 200_000m,
    decimal AvailableCash = 200_000m,
    bool NotifyOnTrade = false);
public record CreateStockPoolRequest(string Name, string? Description);
public record AddPoolItemRequest(string Symbol, string Name, string? Tags, string? Remark);

/// <summary>股票池列表行：报价 + 持仓盈亏 + 一股一策 + 下一开盘日高低预测。</summary>
public record PoolStockRowDto(
    Guid Id,
    Guid StockPoolId,
    string Symbol,
    string Name,
    string? Tags,
    PoolItemSource Source,
    decimal? LastPrice,
    decimal? ChangePercent,
    decimal? PeTtm,
    decimal? Pb,
    string? Industry,
    bool IsStrategyRunning,
    bool HasActiveStrategy,
    bool IsMarketOpen,
    string RuntimeStatusText,
    string MarketSessionText,
    Guid? BoundStrategyId = null,
    string? StrategyName = null,
    DateTime? StrategyUpdatedAt = null,
    decimal InitialCash = 200_000m,
    decimal AvailableCash = 200_000m,
    int PositionQty = 0,
    decimal AvgCost = 0,
    decimal? MarketValue = null,
    decimal? UnrealizedPnL = null,
    decimal? TodayRealizedPnL = null,
    bool NotifyOnTrade = false,
    DateTime? ForecastForDate = null,
    decimal? PredictedHigh = null,
    decimal? PredictedLow = null,
    string? ForecastStatus = null,
    string? ForecastSource = null,
    decimal? AvgAiAccuracyPct = null,
    decimal? AvgAtrAccuracyPct = null);

/// <summary>一股一策 AI 刷新结果（供队列回写 Input/Output / Job 关联）。</summary>
public record StrategyAiRunResult(
    bool Ok,
    string Message,
    Guid? AiJobId = null,
    string? Prompt = null,
    string? InputPayload = null,
    string? OutputPayload = null);

/// <summary>股票池异步刷行情快照。</summary>
public record PoolQuoteSnapshotDto(
    string Symbol,
    decimal? LastPrice,
    decimal? ChangePercent,
    decimal? PeTtm,
    decimal? Pb,
    decimal? MarketValue,
    decimal? UnrealizedPnL);

public record StockOperationRowDto(
    DateTime Time,
    string Kind,
    string Side,
    int Quantity,
    decimal Price,
    decimal? Fee,
    string? Status,
    string? Remark);

public record StockOperationsPageDto(
    string Symbol,
    string Name,
    Guid? PoolId,
    decimal InitialCash,
    decimal AvailableCash,
    int PositionQty,
    decimal AvgCost,
    decimal? LastPrice,
    decimal? UnrealizedPnL,
    IReadOnlyList<StockOperationRowDto> Operations);

public record StockDetailPageDto(
    StockDetailBundle Bundle,
    bool IsStrategyRunning,
    bool HasActiveStrategy,
    bool IsMarketOpen,
    string RuntimeStatusText,
    string MarketSessionText,
    Guid? PoolId,
    PriceRangeForecastDto? LatestForecast = null,
    IReadOnlyList<PriceRangeForecastDto>? ForecastHistory = null,
    decimal? AvgCombinedAccuracyPct = null,
    Guid? PoolItemId = null,
    decimal? AvgAiAccuracyPct = null,
    decimal? AvgAtrAccuracyPct = null);

/// <summary>目标日高低价预测与对账准确率（含预测股票与来源）。</summary>
public record PriceRangeForecastDto(
    Guid Id,
    string Symbol,
    string Name,
    DateTime ForecastForDate,
    DateTime PredictedAt,
    decimal PredictedHigh,
    decimal PredictedLow,
    decimal? ActualHigh,
    decimal? ActualLow,
    decimal? HighDeviationPct,
    decimal? LowDeviationPct,
    decimal? HighAccuracyPct,
    decimal? LowAccuracyPct,
    decimal? CombinedAccuracyPct,
    bool? RangeHit,
    string Status,
    DateTime? EvaluatedAt,
    string? Notes,
    string? Prompt = null,
    string Source = "Ai",
    bool CountsTowardAccuracy = true);

public record IntradayBarDto(string Time, decimal Open, decimal Close, decimal Low, decimal High, long Volume);

public record IntradayChartDto(
    string Symbol,
    string TradeDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal PreClose,
    IReadOnlyList<IntradayBarDto> Bars);

public record StrategyDto(Guid Id, string Name, string? Description, bool IsEnabled, int CurrentVersion, string ParametersJson);
public record CreateStrategyRequest(string Name, string? Description, string? ParametersJson, string? ChangeNote = null);
public record UpdateStrategyParamsRequest(string ParametersJson, string? ChangeNote);

public record AccountDto(Guid Id, string Name, decimal Cash, decimal InitialCash, TradingMode TradingMode);
public record PositionDto(Guid Id, string Symbol, string Name, int BaseQuantity, int Quantity, int SellableQuantity, decimal AvgCost);
public record ManualOrderRequest(string Symbol, OrderSide Side, int Quantity, decimal Price, Guid? StockPoolId);
public record TradeDto(
    Guid Id,
    string Symbol,
    OrderSide Side,
    int Quantity,
    decimal Price,
    decimal TotalFee,
    decimal Amount,
    DateTime TradeDate,
    DateTime TradedAt,
    Guid? StrategyDefinitionId);

public record DailyPnLDto(
    Guid Id,
    DateTime TradeDate,
    string? Symbol,
    Guid? StockPoolId,
    Guid? StrategyDefinitionId,
    int TradeCount,
    decimal RealizedPnL,
    decimal Fees,
    decimal WinRate);

public record DashboardDto(
    decimal Cash,
    decimal PositionMarketValue,
    decimal TotalAssets,
    decimal TodayPnL,
    int TodayTradeCount,
    string TradingMode,
    string Disclaimer,
    Guid? PoolId = null,
    string? PoolName = null,
    int PoolItemCount = 0,
    decimal PoolInitialCash = 0);

public record AiJobDto(
    Guid Id,
    AiJobType JobType,
    AiJobStatus Status,
    string Prompt,
    DateTime CreationTime,
    string? ErrorMessage,
    string? AnalysisSummary = null,
    int SuggestionCount = 0,
    DateTime? CompletedAt = null);

public record AiSuggestionDto(
    Guid Id,
    Guid AiJobId,
    string? Symbol,
    string? Name,
    string? Reason,
    string? Risk,
    string PayloadJson,
    bool IsAccepted,
    DateTime? ReviewedAt,
    string? SelectionLogic = null);

public record AiJobDetailDto(
    Guid Id,
    AiJobType JobType,
    AiJobStatus Status,
    string Prompt,
    string? AnalysisSummary,
    IReadOnlyList<string> AnalysisSteps,
    string? InputPayload,
    string? OutputPayload,
    string? ErrorMessage,
    DateTime CreationTime,
    DateTime? CompletedAt,
    IReadOnlyList<AiSuggestionDto> Suggestions);

public record CreateAiStockPickRequest(string Prompt);
public record ReviewAiSuggestionRequest(Guid SuggestionId, bool Accept);

public record AiQueueItemDto(
    Guid Id,
    AiQueueTaskType TaskType,
    AiQueueItemStatus Status,
    string Title,
    Guid? CorrelationId,
    Guid? RelatedGoalId,
    Guid? RelatedAiJobId,
    Guid? RelatedPoolItemId,
    string? Symbol,
    DateTime CreationTime,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorMessage,
    bool HasOutput);

public record AiQueueItemDetailDto(
    Guid Id,
    AiQueueTaskType TaskType,
    AiQueueItemStatus Status,
    string Title,
    Guid? CorrelationId,
    Guid? RelatedGoalId,
    Guid? RelatedAiJobId,
    Guid? RelatedPoolItemId,
    string? Symbol,
    string? InputPayload,
    string? OutputPayload,
    string? ErrorMessage,
    DateTime CreationTime,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? WorkerId);

public record CreateInvestmentGoalRequest(
    decimal ExpectedReturnPct,
    int HorizonDays,
    string ThemePreference);

public record InvestmentGoalDto(
    Guid Id,
    decimal ExpectedReturnPct,
    int HorizonDays,
    string ThemePreference,
    string? GeneratedPrompt,
    string? PromptRationale,
    InvestmentGoalStatus Status,
    DateTime? StartDate,
    DateTime? TargetEndDate,
    Guid? StockPickJobId,
    int StockCount,
    decimal? LatestAchievementRate,
    decimal? LatestPortfolioReturnPct,
    DateTime CreationTime);

public record InvestmentGoalStockDto(
    Guid Id,
    string Symbol,
    string Name,
    Guid? PoolItemId,
    decimal BaselineCash,
    string? SelectionLogic = null,
    string? Reason = null,
    string? Risk = null);

public record GoalProgressDailyDto(
    DateTime TradeDate,
    decimal PortfolioReturnPct,
    decimal AchievementRate,
    decimal TotalMarketValue,
    decimal TotalBaseline,
    decimal RealizedPnL,
    decimal UnrealizedPnL,
    int DayIndex,
    string? Notes);

public record InvestmentGoalDetailDto(
    InvestmentGoalDto Goal,
    IReadOnlyList<InvestmentGoalStockDto> Stocks,
    IReadOnlyList<GoalProgressDailyDto> Progress);
