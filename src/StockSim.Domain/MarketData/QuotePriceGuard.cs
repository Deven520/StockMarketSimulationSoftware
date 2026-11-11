namespace StockSim.Domain.MarketData;

/// <summary>
/// 行情买卖价一致性校验：过滤 Bid/Ask 相对 Last 严重偏离的脏报价，避免错价成交。
/// </summary>
public static class QuotePriceGuard
{
    /// <summary>买卖价相对最新价允许偏离（百分点）。创业板/科创板日内极限约 20%，留一点余量。</summary>
    public const decimal MaxBookVsLastPct = 22m;

    /// <summary>成交价相对最新价允许偏离（百分点）。</summary>
    public const decimal MaxTradeVsLastPct = 22m;

    public static decimal PctDiff(decimal a, decimal b)
    {
        if (a <= 0 || b <= 0) return 0m;
        return Math.Abs(a - b) / b * 100m;
    }

    /// <summary>Bid/Ask 与 Last 是否自洽；交叉盘口也不通过。</summary>
    public static bool IsBookConsistent(QuoteTick quote, decimal maxPct = MaxBookVsLastPct)
    {
        if (quote.LastPrice <= 0) return false;

        if (quote.Bid > 0 && PctDiff(quote.Bid, quote.LastPrice) > maxPct)
            return false;
        if (quote.Ask > 0 && PctDiff(quote.Ask, quote.LastPrice) > maxPct)
            return false;
        if (quote.Bid > 0 && quote.Ask > 0 && quote.Bid > quote.Ask)
            return false;

        return true;
    }

    public static bool IsTradePriceSane(decimal price, decimal lastPrice, decimal maxPct = MaxTradeVsLastPct)
    {
        if (price <= 0 || lastPrice <= 0) return false;
        return PctDiff(price, lastPrice) <= maxPct;
    }

    /// <summary>卖出用价：Bid 异常则回退 Last。</summary>
    public static decimal SafeBid(QuoteTick quote)
    {
        if (quote.LastPrice <= 0) return quote.Bid > 0 ? quote.Bid : 0m;
        if (quote.Bid > 0 && PctDiff(quote.Bid, quote.LastPrice) <= MaxBookVsLastPct)
            return quote.Bid;
        return quote.LastPrice;
    }

    /// <summary>买入用价：Ask 异常则回退 Last。</summary>
    public static decimal SafeAsk(QuoteTick quote)
    {
        if (quote.LastPrice <= 0) return quote.Ask > 0 ? quote.Ask : 0m;
        if (quote.Ask > 0 && PctDiff(quote.Ask, quote.LastPrice) <= MaxBookVsLastPct)
            return quote.Ask;
        return quote.LastPrice;
    }
}
