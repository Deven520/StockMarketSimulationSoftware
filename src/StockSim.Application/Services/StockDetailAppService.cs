using Microsoft.EntityFrameworkCore;
using StockSim.Application.Dtos;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;
using StockSim.Domain.Trading;

namespace StockSim.Application.Services;

public class StockDetailAppService
{
    private readonly IStockSimDataStore _db;
    private readonly IStockDetailProvider _detailProvider;
    private readonly CachedLightQuoteService _quotes;

    public StockDetailAppService(
        IStockSimDataStore db,
        IStockDetailProvider detailProvider,
        CachedLightQuoteService quotes)
    {
        _db = db;
        _detailProvider = detailProvider;
        _quotes = quotes;
    }

    /// <summary>首屏：仅库内持仓/策略/今日盈亏，行情字段留空由前端异步刷。</summary>
    public async Task<IReadOnlyList<PoolStockRowDto>> GetPoolStockRowsAsync(Guid poolId, CancellationToken ct = default)
    {
        var items = await _db.StockPoolItems.Where(i => i.StockPoolId == poolId).ToListAsync(ct);
        var now = DateTime.Now;
        var marketOpen = AShareMarketSession.IsContinuousAuction(now);
        var sessionLabel = AShareMarketSession.Describe(now);

        var activeStrategyIds = await _db.Strategies
            .Where(s => s.IsEnabled)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var bindings = await _db.StrategyBindings
            .Where(b => b.IsEnabled && activeStrategyIds.Contains(b.StrategyDefinitionId))
            .ToListAsync(ct);

        var boundIds = items.Where(i => i.BoundStrategyId.HasValue).Select(i => i.BoundStrategyId!.Value).Distinct().ToList();
        var strategyNames = boundIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Strategies.Where(s => boundIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var symbols = items.Select(i => i.Symbol).ToList();
        var positions = await _db.Positions
            .Where(p => symbols.Contains(p.Symbol))
            .ToListAsync(ct);
        var today = DateTime.Today;
        var todayTrades = await _db.Trades
            .Where(t => symbols.Contains(t.Symbol) && t.TradeDate == today)
            .ToListAsync(ct);

        // 收盘前展示/刷新当日预测；收盘后切下一交易日。兼容旧数据：当日缺失时回退到下一交易日预测。
        var forecastTarget = PriceForecastMetrics.ResolveForecastTargetDate(now);
        var fallbackTarget = PriceForecastMetrics.IsSameDayForecastWindow(now)
            ? PriceForecastMetrics.NextTradingDay(now.Date)
            : (DateTime?)null;
        var forecastDates = fallbackTarget is null
            ? new[] { forecastTarget.Date }
            : new[] { forecastTarget.Date, fallbackTarget.Value.Date };
        var forecastRows = symbols.Count == 0
            ? new List<PriceRangeForecast>()
            : await _db.PriceRangeForecasts
                .Where(f => symbols.Contains(f.Symbol) && forecastDates.Contains(f.ForecastForDate))
                .ToListAsync(ct);
        // 列表优先展示 AI/校准预测，其次 ATR 基线；占位不展示
        var forecastBySymbol = forecastRows
            .Where(f => f.Source != ForecastSource.Placeholder)
            .GroupBy(f => f.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(x => x.ForecastForDate.Date == forecastTarget.Date ? 0 : 1)
                    .ThenBy(x => SourceDisplayRank(x.Source))
                    .ThenByDescending(x => x.PredictedAt)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        var lookbackFrom = now.Date.AddDays(-PriceForecastMetrics.EvaluationLookbackDays);
        var accuracyRows = symbols.Count == 0
            ? new List<PriceRangeForecast>()
            : await _db.PriceRangeForecasts
                .Where(f => symbols.Contains(f.Symbol)
                            && f.Status == ForecastStatus.Evaluated
                            && f.CountsTowardAccuracy
                            && f.CombinedAccuracyPct != null
                            && f.ForecastForDate >= lookbackFrom)
                .ToListAsync(ct);
        var aiAvgBySymbol = accuracyRows
            .Where(f => f.Source is ForecastSource.Ai or ForecastSource.CalibratedAi)
            .GroupBy(f => f.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => Math.Round(g.Average(ScoredCombined), 1),
                StringComparer.OrdinalIgnoreCase);
        var atrAvgBySymbol = accuracyRows
            .Where(f => f.Source == ForecastSource.AtrBaseline)
            .GroupBy(f => f.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => Math.Round(g.Average(ScoredCombined), 1),
                StringComparer.OrdinalIgnoreCase);

        var boundSymbolSet = items
            .Where(i => i.BoundStrategyId.HasValue)
            .Select(i => i.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = new List<PoolStockRowDto>(items.Count);
        foreach (var item in items)
        {
            var pos = positions.FirstOrDefault(p =>
                string.Equals(p.Symbol, item.Symbol, StringComparison.OrdinalIgnoreCase));
            var qty = pos?.Quantity ?? 0;
            var avg = pos?.AvgCost ?? 0;
            var dayTrades = todayTrades
                .Where(t => string.Equals(t.Symbol, item.Symbol, StringComparison.OrdinalIgnoreCase))
                .ToList();
            decimal todayPnl = 0;
            foreach (var t in dayTrades)
            {
                if (t.Side == OrderSide.Sell)
                    todayPnl += (t.Price - avg) * t.Quantity - t.TotalFee;
                else
                    todayPnl -= t.TotalFee;
            }

            var hasBound = item.BoundStrategyId.HasValue
                          && activeStrategyIds.Contains(item.BoundStrategyId.Value);
            var symbolHasStrategy = bindings.Any(b =>
                string.Equals(b.Symbol, item.Symbol, StringComparison.OrdinalIgnoreCase)
                && activeStrategyIds.Contains(b.StrategyDefinitionId));
            var poolHasStrategy = !boundSymbolSet.Contains(item.Symbol)
                                  && bindings.Any(b => b.StockPoolId == poolId && string.IsNullOrEmpty(b.Symbol));
            var isTrend = item.TradeStyle == StockTradeStyle.TrendSwing;
            var hasActiveStrategy = !isTrend && (hasBound || symbolHasStrategy || poolHasStrategy);
            var isRunning = hasActiveStrategy && marketOpen;
            string? strategyName = null;
            if (item.BoundStrategyId.HasValue)
            {
                strategyNames.TryGetValue(item.BoundStrategyId.Value, out strategyName);
            }

            forecastBySymbol.TryGetValue(item.Symbol, out var fc);
            aiAvgBySymbol.TryGetValue(item.Symbol, out var aiAvg);
            atrAvgBySymbol.TryGetValue(item.Symbol, out var atrAvg);
            string statusText;
            if (isTrend)
            {
                statusText = qty >= 100
                    ? "趋势持仓"
                    : marketOpen ? "待低位建仓" : "趋势观察";
            }
            else
            {
                statusText = isRunning ? "运行中" : "未运行";
            }
            rows.Add(new PoolStockRowDto(
                item.Id,
                item.StockPoolId,
                item.Symbol,
                item.Name,
                item.Tags,
                item.Source,
                LastPrice: null,
                ChangePercent: null,
                PeTtm: null,
                Pb: null,
                Industry: null,
                isRunning,
                hasActiveStrategy,
                marketOpen,
                statusText,
                sessionLabel,
                item.BoundStrategyId,
                strategyName,
                item.StrategyUpdatedAt,
                item.InitialCash,
                item.AvailableCash,
                qty,
                avg,
                MarketValue: null,
                UnrealizedPnL: null,
                todayPnl,
                item.NotifyOnTrade,
                fc?.ForecastForDate,
                fc?.PredictedHigh,
                fc?.PredictedLow,
                fc?.Status.ToString(),
                fc?.Source.ToString(),
                aiAvgBySymbol.ContainsKey(item.Symbol) ? aiAvg : null,
                atrAvgBySymbol.ContainsKey(item.Symbol) ? atrAvg : null,
                item.TradeStyle));
        }

        return rows;
    }

    private static int SourceDisplayRank(ForecastSource source) => source switch
    {
        ForecastSource.CalibratedAi => 0,
        ForecastSource.Ai => 1,
        ForecastSource.AtrBaseline => 2,
        _ => 9
    };

    /// <summary>异步刷行情：轻量报价（批量+缓存）并回填市值/浮动盈亏。</summary>
    public async Task<IReadOnlyList<PoolQuoteSnapshotDto>> GetPoolQuotesAsync(Guid poolId, CancellationToken ct = default)
    {
        var items = await _db.StockPoolItems
            .Where(i => i.StockPoolId == poolId)
            .Select(i => new { i.Symbol })
            .ToListAsync(ct);
        if (items.Count == 0) return Array.Empty<PoolQuoteSnapshotDto>();

        var symbols = items.Select(i => i.Symbol).ToList();
        var positions = await _db.Positions
            .Where(p => symbols.Contains(p.Symbol))
            .ToListAsync(ct);

        var quotes = await _quotes.GetQuotesAsync(symbols, ct);
        var snaps = new List<PoolQuoteSnapshotDto>(symbols.Count);
        foreach (var symbol in symbols)
        {
            quotes.TryGetValue(symbol, out var q);
            var pos = positions.FirstOrDefault(p =>
                string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            var qty = pos?.Quantity ?? 0;
            var avg = pos?.AvgCost ?? 0;
            decimal? last = q is { Last: > 0 } ? q.Last : null;
            decimal? mv = last.HasValue ? last.Value * qty : null;
            decimal? upnl = last.HasValue && qty > 0 ? (last.Value - avg) * qty : qty == 0 ? 0 : null;

            snaps.Add(new PoolQuoteSnapshotDto(
                symbol,
                last,
                q?.ChangePercent,
                q?.PeTtm,
                q?.Pb,
                mv,
                upnl));
        }

        return snaps;
    }

    public async Task<StockOperationsPageDto?> GetStockOperationsAsync(
        string symbol,
        Guid? poolId,
        CancellationToken ct = default)
    {
        symbol = symbol.Trim().ToUpperInvariant();
        var item = poolId.HasValue
            ? await _db.StockPoolItems.FirstOrDefaultAsync(i => i.StockPoolId == poolId && i.Symbol == symbol, ct)
            : await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Symbol == symbol, ct);
        if (item is null) return null;

        var pos = await _db.Positions.FirstOrDefaultAsync(p => p.Symbol == symbol, ct);
        var quote = await _quotes.GetQuoteAsync(symbol, item.Name, ct);
        var last = quote?.Last;
        var qty = pos?.Quantity ?? 0;
        var avg = pos?.AvgCost ?? 0;
        decimal? upnl = last.HasValue && qty > 0 ? (last.Value - avg) * qty : 0;

        var trades = await _db.Trades
            .Where(t => t.Symbol == symbol)
            .OrderByDescending(t => t.TradedAt)
            .Take(200)
            .ToListAsync(ct);
        var orders = await _db.Orders
            .Where(o => o.Symbol == symbol && o.Status == OrderStatus.Rejected)
            .OrderByDescending(o => o.CreationTime)
            .Take(100)
            .ToListAsync(ct);

        var ops = new List<StockOperationRowDto>();
        foreach (var t in trades)
        {
            ops.Add(new StockOperationRowDto(
                t.TradedAt.ToLocalTime(),
                "成交",
                t.Side == OrderSide.Buy ? "买入" : "卖出",
                t.Quantity,
                t.Price,
                t.TotalFee,
                "已成交",
                null));
        }

        foreach (var o in orders)
        {
            ops.Add(new StockOperationRowDto(
                o.CreationTime.ToLocalTime(),
                "拒单",
                o.Side == OrderSide.Buy ? "买入" : "卖出",
                o.Quantity,
                o.Price,
                null,
                "拒绝",
                o.RejectReason));
        }

        return new StockOperationsPageDto(
            item.Symbol,
            item.Name,
            item.StockPoolId,
            item.InitialCash,
            item.AvailableCash,
            qty,
            avg,
            last,
            upnl,
            ops.OrderByDescending(x => x.Time).ToList());
    }

    public async Task<StockDetailPageDto?> GetStockDetailPageAsync(
        string symbol,
        Guid? poolId,
        string? name,
        CancellationToken ct = default)
    {
        Guid? poolItemId = null;
        StockPoolItem? poolItem = null;
        if (poolId.HasValue)
        {
            poolItem = await _db.StockPoolItems.FirstOrDefaultAsync(
                i => i.StockPoolId == poolId && i.Symbol == symbol, ct);
            poolItemId = poolItem?.Id;
            if (string.IsNullOrWhiteSpace(name))
                name = poolItem?.Name;
        }
        else
        {
            poolItem = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Symbol == symbol, ct);
            poolItemId = poolItem?.Id;
            poolId ??= poolItem?.StockPoolId;
            if (string.IsNullOrWhiteSpace(name))
                name = poolItem?.Name;
        }

        var bundle = await _detailProvider.GetDetailAsync(symbol, name, ct);
        if (bundle is null) return null;

        var now = DateTime.Now;
        var marketOpen = AShareMarketSession.IsContinuousAuction(now);

        var activeStrategyIds = await _db.Strategies
            .Where(s => s.IsEnabled)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var hasStrategy = await _db.StrategyBindings.AnyAsync(
            b => b.IsEnabled
                 && activeStrategyIds.Contains(b.StrategyDefinitionId)
                 && ((poolId.HasValue && b.StockPoolId == poolId) || b.Symbol == symbol),
            ct);

        var running = hasStrategy && marketOpen;

        var forecasts = await _db.PriceRangeForecasts
            .Where(f => f.Symbol == symbol)
            .OrderByDescending(f => f.ForecastForDate)
            .ThenByDescending(f => f.PredictedAt)
            .Take(20)
            .ToListAsync(ct);

        var history = forecasts.Select(ToForecastDto).ToList();

        var latest = history
            .Where(h => !string.Equals(h.Source, nameof(ForecastSource.Placeholder), StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => SourceDisplayRank(Enum.TryParse<ForecastSource>(h.Source, out var s) ? s : ForecastSource.Ai))
            .ThenByDescending(h => h.ForecastForDate)
            .ThenByDescending(h => h.PredictedAt)
            .FirstOrDefault()
            ?? history.FirstOrDefault();

        var scored = history.Where(h => h.CountsTowardAccuracy && h.CombinedAccuracyPct.HasValue).ToList();
        decimal? avgCombined = scored.Count == 0
            ? null
            : Math.Round(scored.Average(h => h.CombinedAccuracyPct!.Value), 2);
        var aiScored = scored.Where(h =>
            h.Source is nameof(ForecastSource.Ai) or nameof(ForecastSource.CalibratedAi)).ToList();
        var atrScored = scored.Where(h => h.Source == nameof(ForecastSource.AtrBaseline)).ToList();
        decimal? avgAi = aiScored.Count == 0
            ? null
            : Math.Round(aiScored.Average(h => h.CombinedAccuracyPct!.Value), 2);
        decimal? avgAtr = atrScored.Count == 0
            ? null
            : Math.Round(atrScored.Average(h => h.CombinedAccuracyPct!.Value), 2);
        var hitEligible = scored.Where(h => h.RangeHit.HasValue).ToList();
        decimal? hitRate = hitEligible.Count == 0
            ? null
            : Math.Round(100m * hitEligible.Count(h => h.RangeHit == true) / hitEligible.Count, 1);
        decimal? avgMae = scored.Count == 0
            ? null
            : Math.Round(
                scored.Average(h => h.MeanAbsDeviationPct
                    ?? PriceForecastMetrics.MeanAbsDeviationPct(
                        h.HighDeviationPct ?? 0m, h.LowDeviationPct ?? 0m)),
                2);

        return new StockDetailPageDto(
            bundle,
            running,
            hasStrategy,
            marketOpen,
            running ? "运行中" : "未运行",
            AShareMarketSession.Describe(now),
            poolId,
            latest,
            history,
            avgCombined,
            poolItemId,
            avgAi,
            avgAtr,
            hitRate,
            avgMae);
    }

    private static decimal ScoredCombined(PriceRangeForecast f)
        => PriceForecastMetrics.RescoreCombinedAccuracy(f.HighDeviationPct, f.LowDeviationPct)
           ?? f.CombinedAccuracyPct
           ?? 0m;

    private static PriceRangeForecastDto ToForecastDto(PriceRangeForecast f)
    {
        decimal? highAbs = f.ActualHigh is { } ah
            ? PriceForecastMetrics.AbsoluteError(f.PredictedHigh, ah)
            : null;
        decimal? lowAbs = f.ActualLow is { } al
            ? PriceForecastMetrics.AbsoluteError(f.PredictedLow, al)
            : null;
        decimal? meanAbs = f.HighDeviationPct is { } hd && f.LowDeviationPct is { } ld
            ? PriceForecastMetrics.MeanAbsDeviationPct(hd, ld)
            : null;
        var combined = PriceForecastMetrics.RescoreCombinedAccuracy(f.HighDeviationPct, f.LowDeviationPct)
                       ?? f.CombinedAccuracyPct;
        var highAcc = f.HighDeviationPct is { } hDev
            ? PriceForecastMetrics.AccuracyPct(hDev)
            : f.HighAccuracyPct;
        var lowAcc = f.LowDeviationPct is { } lDev
            ? PriceForecastMetrics.AccuracyPct(lDev)
            : f.LowAccuracyPct;

        return new PriceRangeForecastDto(
            f.Id,
            f.Symbol,
            f.Name,
            f.ForecastForDate,
            f.PredictedAt,
            f.PredictedHigh,
            f.PredictedLow,
            f.ActualHigh,
            f.ActualLow,
            f.HighDeviationPct,
            f.LowDeviationPct,
            highAcc,
            lowAcc,
            combined,
            f.RangeHit,
            f.Status.ToString(),
            f.EvaluatedAt,
            f.Notes,
            f.Prompt,
            f.Source.ToString(),
            f.CountsTowardAccuracy,
            highAbs,
            lowAbs,
            meanAbs,
            PriceForecastMetrics.AccuracyGrade(combined));
    }

    public async Task<IntradayChartDto?> GetIntradayChartAsync(string symbol, DateTime tradeDate, CancellationToken ct = default)
    {
        var date = tradeDate.Date;
        var bars = await _detailProvider.GetIntradayAsync(symbol, date, ct);
        if (bars.Count == 0) return null;

        // 用日K找当日 OHLC 与昨收
        var detail = await _detailProvider.GetDetailAsync(symbol, null, ct);
        var dayBar = detail?.DailyBars.FirstOrDefault(d => d.Date.Date == date);
        var prev = detail?.DailyBars
            .Where(d => d.Date.Date < date)
            .OrderByDescending(d => d.Date)
            .FirstOrDefault();

        return new IntradayChartDto(
            symbol.Trim().ToUpperInvariant(),
            date.ToString("yyyy-MM-dd"),
            dayBar?.Open ?? bars[0].Open,
            dayBar?.High ?? bars.Max(b => b.High),
            dayBar?.Low ?? bars.Min(b => b.Low),
            dayBar?.Close ?? bars[^1].Close,
            prev?.Close ?? dayBar?.Open ?? bars[0].Open,
            bars.Select(b => new IntradayBarDto(
                b.Time.ToString("HH:mm"),
                b.Open,
                b.Close,
                b.Low,
                b.High,
                b.Volume)).ToList());
    }
}
