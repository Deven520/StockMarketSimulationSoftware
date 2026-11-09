namespace StockSim.Domain.MarketData;

public sealed class DailyBar
{
    public DateTime Date { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public long Volume { get; init; }
    public decimal Amount { get; init; }
}

public sealed class MinuteBar
{
    public DateTime Time { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public long Volume { get; init; }
}

public sealed class FundFlowSnapshot
{
    public decimal MainNetInflow { get; init; }
    public decimal MainInflow { get; init; }
    public decimal MainOutflow { get; init; }
    public decimal RetailNetInflow { get; init; }
    public decimal SuperLargeNet { get; init; }
    public decimal LargeNet { get; init; }
    public decimal MediumNet { get; init; }
    public decimal SmallNet { get; init; }
    public DateTime AsOf { get; init; }
}

public sealed class StockFundamentals
{
    public decimal? PeTtm { get; init; }
    public decimal? Pb { get; init; }
    public decimal? PsTtm { get; init; }
    public decimal? TotalMarketCapYi { get; init; }
    public decimal? CircMarketCapYi { get; init; }
    public decimal? Roe { get; init; }
    public string? Industry { get; init; }
}

public sealed class StockQuoteBoard
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public decimal Last { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal PreClose { get; init; }
    public decimal Change { get; init; }
    public decimal ChangePercent { get; init; }
    public long Volume { get; init; }
    public decimal Amount { get; init; }
    public decimal TurnoverRate { get; init; }
    public DateTime AsOf { get; init; }
}

public sealed class StockDetailBundle
{
    public required StockQuoteBoard Board { get; init; }
    public required StockFundamentals Fundamentals { get; init; }
    public required FundFlowSnapshot FundFlow { get; init; }
    public required IReadOnlyList<DailyBar> DailyBars { get; init; }
    public required IReadOnlyList<MinuteBar> MinuteBars { get; init; }
    public string DataSourceNote { get; init; } = string.Empty;

    /// <summary>
    /// 模拟/回退行情。准确率对账不得使用，避免把演示价写入 ActualHigh/Low。
    /// </summary>
    public bool IsSimulated { get; init; }
}

/// <summary>列表用轻量报价（不含日K/分时/资金流）。</summary>
public sealed class LightStockQuote
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public decimal Last { get; init; }
    public decimal ChangePercent { get; init; }
    public decimal? PeTtm { get; init; }
    public decimal? Pb { get; init; }
    public string? Industry { get; init; }
    public DateTime AsOf { get; init; }

    /// <summary>模拟/演示报价。Worker 交易路径必须忽略，避免按假价成交。</summary>
    public bool IsSimulated { get; init; }
}

/// <summary>个股详情行情接口（可替换为真实行情源）。</summary>
public interface IStockDetailProvider
{
    Task<StockDetailBundle?> GetDetailAsync(string symbol, string? name = null, CancellationToken ct = default);

    /// <summary>指定交易日的分时/分钟K（用于日K点击下钻）。</summary>
    Task<IReadOnlyList<MinuteBar>> GetIntradayAsync(string symbol, DateTime tradeDate, CancellationToken ct = default);

    /// <summary>单只轻量报价（仅 push 报价字段）。</summary>
    Task<LightStockQuote?> GetLightQuoteAsync(string symbol, string? name = null, CancellationToken ct = default);

    /// <summary>批量轻量报价（实现方可走批量接口或内部并行）。</summary>
    Task<IReadOnlyList<LightStockQuote>> GetLightQuotesAsync(
        IReadOnlyList<string> symbols,
        CancellationToken ct = default);
}
