using Microsoft.EntityFrameworkCore;
using StockSim.Application.Dtos;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;

namespace StockSim.Application.Services;

public class PnLAppService
{
    private readonly IStockSimDataStore _db;

    public PnLAppService(IStockSimDataStore db) => _db = db;

    public async Task SettleAsync(DateTime tradeDate, CancellationToken ct = default)
    {
        var date = tradeDate.Date;
        var existing = await _db.DailyPnLs.Where(x => x.TradeDate == date).ToListAsync(ct);
        _db.DailyPnLs.RemoveRange(existing);

        var trades = await _db.Trades.Where(t => t.TradeDate == date).ToListAsync(ct);
        var accountIds = trades.Select(t => t.SimAccountId).Distinct().ToList();
        var positions = await _db.Positions
            .Where(p => accountIds.Contains(p.SimAccountId))
            .ToListAsync(ct);

        var groups = trades.GroupBy(t => new { t.SimAccountId, t.StockPoolId, t.StrategyDefinitionId, t.Symbol });

        foreach (var g in groups)
        {
            var pos = positions.FirstOrDefault(p =>
                p.SimAccountId == g.Key.SimAccountId
                && string.Equals(p.Symbol, g.Key.Symbol, StringComparison.OrdinalIgnoreCase));
            var dayTrades = g.OrderBy(t => t.TradedAt).ThenBy(t => t.CreationTime).ToList();
            var (realized, winRate) = ComputeRealizedPnL(
                dayTrades,
                pos?.Quantity ?? 0,
                pos?.AvgCost ?? 0);
            var fees = g.Sum(x => x.TotalFee);

            _db.DailyPnLs.Add(new DailyPnL
            {
                TradeDate = date,
                SimAccountId = g.Key.SimAccountId,
                StockPoolId = g.Key.StockPoolId,
                StrategyDefinitionId = g.Key.StrategyDefinitionId,
                Symbol = g.Key.Symbol,
                TradeCount = g.Count(),
                RealizedPnL = realized,
                Fees = fees,
                WinRate = winRate
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 已实现盈亏：卖出按成本结算；(卖价-成本)×数量−费用；买入仅计手续费。
    /// 禁止用「卖出额−买入额」（会把建仓当成巨亏）。
    /// </summary>
    internal static (decimal Realized, decimal WinRate) ComputeRealizedPnL(
        IReadOnlyList<Trade> dayTradesChronological,
        int endOfDayQty,
        decimal endOfDayAvgCost)
    {
        // 由日终持仓逆推日初成本，再正序回放，避免用「现价均成本」误伤当日先买后卖。
        var qty = endOfDayQty;
        var costTotal = endOfDayAvgCost * endOfDayQty;

        foreach (var t in dayTradesChronological.Reverse())
        {
            if (t.Side == OrderSide.Sell)
            {
                if (qty <= 0)
                {
                    qty = t.Quantity;
                    costTotal = t.Price * t.Quantity;
                }
                else
                {
                    var avg = costTotal / qty;
                    qty += t.Quantity;
                    costTotal = avg * qty;
                }
            }
            else
            {
                if (qty <= t.Quantity)
                {
                    qty = 0;
                    costTotal = 0;
                }
                else
                {
                    costTotal -= t.Amount;
                    qty -= t.Quantity;
                    if (qty <= 0 || costTotal < 0)
                    {
                        qty = 0;
                        costTotal = 0;
                    }
                }
            }
        }

        decimal realized = 0;
        var sellCount = 0;
        var winCount = 0;

        foreach (var t in dayTradesChronological)
        {
            if (t.Side == OrderSide.Buy)
            {
                realized -= t.TotalFee;
                costTotal += t.Amount;
                qty += t.Quantity;
                continue;
            }

            sellCount++;
            var avg = qty > 0 ? costTotal / qty : t.Price;
            var tradePnl = (t.Price - avg) * t.Quantity - t.TotalFee;
            realized += tradePnl;
            if (tradePnl > 0) winCount++;

            if (qty <= t.Quantity)
            {
                qty = 0;
                costTotal = 0;
            }
            else
            {
                costTotal = avg * (qty - t.Quantity);
                qty -= t.Quantity;
            }
        }

        var winRate = sellCount == 0 ? 0m : (decimal)winCount / sellCount;
        return (Math.Round(realized, 2), Math.Round(winRate, 4));
    }

    public async Task<IReadOnlyList<DailyPnLDto>> QueryAsync(
        DateTime? from,
        DateTime? to,
        CancellationToken ct = default,
        bool poolOnly = true)
    {
        var q = _db.DailyPnLs.AsQueryable();
        if (from.HasValue) q = q.Where(x => x.TradeDate >= from.Value.Date);
        if (to.HasValue) q = q.Where(x => x.TradeDate <= to.Value.Date);

        if (poolOnly)
        {
            var pool = await GetDefaultPoolAsync(ct);
            if (pool is not null)
            {
                var symbols = await _db.StockPoolItems
                    .Where(i => i.StockPoolId == pool.Id)
                    .Select(i => i.Symbol)
                    .ToListAsync(ct);
                q = q.Where(x =>
                    (x.StockPoolId != null && x.StockPoolId == pool.Id)
                    || (x.Symbol != null && symbols.Contains(x.Symbol)));
            }
        }

        return await q.OrderByDescending(x => x.TradeDate)
            .Select(x => new DailyPnLDto(
                x.Id, x.TradeDate, x.Symbol, x.StockPoolId, x.StrategyDefinitionId,
                x.TradeCount, x.RealizedPnL, x.Fees, x.WinRate))
            .Take(500)
            .ToListAsync(ct);
    }

    public async Task<DashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        // 打开总览时重算当日盈亏，纠正历史错误口径（卖出额−买入额）
        await SettleAsync(DateTime.Today, ct);

        var account = await _db.Accounts.Include(a => a.Positions).FirstAsync(ct);
        var pool = await GetDefaultPoolAsync(ct);

        if (pool is null)
        {
            return new DashboardDto(
                0, 0, 0, 0, 0,
                account.TradingMode.ToString(),
                "模拟交易，非实盘投资建议（当前无股票池）");
        }

        var items = await _db.StockPoolItems
            .Where(i => i.StockPoolId == pool.Id)
            .ToListAsync(ct);
        var symbols = items.Select(i => i.Symbol).ToList();
        var symbolSet = symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 池内可用现金（每股 AvailableCash 合计）
        var cash = items.Sum(i => i.AvailableCash);
        var initialCash = items.Sum(i => i.InitialCash);

        // 池内持仓成本市值
        var positions = account.Positions
            .Where(p => symbolSet.Contains(p.Symbol))
            .ToList();
        var mv = positions.Sum(p => p.AvgCost * p.Quantity);

        var todayPnLRows = await _db.DailyPnLs
            .Where(x => x.TradeDate == DateTime.Today
                        && ((x.StockPoolId != null && x.StockPoolId == pool.Id)
                            || (x.Symbol != null && symbols.Contains(x.Symbol))))
            .Select(x => x.RealizedPnL)
            .ToListAsync(ct);
        var todayPnL = todayPnLRows.Sum();

        var todayTrades = await _db.Trades.CountAsync(
            t => t.TradeDate == DateTime.Today
                 && ((t.StockPoolId != null && t.StockPoolId == pool.Id)
                     || symbols.Contains(t.Symbol)),
            ct);

        return new DashboardDto(
            Math.Round(cash, 2),
            Math.Round(mv, 2),
            Math.Round(cash + mv, 2),
            todayPnL,
            todayTrades,
            account.TradingMode.ToString(),
            "模拟交易，非实盘投资建议（按当前股票池统计）",
            pool.Id,
            pool.Name,
            items.Count,
            Math.Round(initialCash, 2));
    }

    /// <summary>
    /// 池内个股收益序列：累计已实现盈亏 / 初始资金 × 100；无成交日沿用前值。
    /// </summary>
    public async Task<PoolReturnSeriesDto> GetPoolReturnSeriesAsync(
        Guid? poolId = null,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default)
    {
        var pool = poolId.HasValue
            ? await _db.StockPools.FirstOrDefaultAsync(p => p.Id == poolId.Value, ct)
            : await GetDefaultPoolAsync(ct);
        if (pool is null)
            return new PoolReturnSeriesDto(Array.Empty<string>(), Array.Empty<PoolReturnSeriesLineDto>());

        var fromDate = (from ?? DateTime.Today.AddDays(-30)).Date;
        var toDate = (to ?? DateTime.Today).Date;

        var items = await _db.StockPoolItems
            .Where(i => i.StockPoolId == pool.Id)
            .OrderBy(i => i.Symbol)
            .ToListAsync(ct);
        if (items.Count == 0)
            return new PoolReturnSeriesDto(Array.Empty<string>(), Array.Empty<PoolReturnSeriesLineDto>());

        var symbols = items.Select(i => i.Symbol).ToList();
        var account = await _db.Accounts.Include(a => a.Positions).FirstOrDefaultAsync(ct);
        var posBySymbol = account?.Positions
            .Where(p => symbols.Contains(p.Symbol))
            .ToDictionary(p => p.Symbol, p => p, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, Position>(StringComparer.OrdinalIgnoreCase);

        // 含区间前累计，保证区间内起点正确
        var allPnls = await _db.DailyPnLs
            .Where(x => x.Symbol != null
                        && symbols.Contains(x.Symbol)
                        && x.TradeDate <= toDate
                        && ((x.StockPoolId != null && x.StockPoolId == pool.Id)
                            || symbols.Contains(x.Symbol)))
            .OrderBy(x => x.TradeDate)
            .ToListAsync(ct);

        var dates = allPnls
            .Where(x => x.TradeDate >= fromDate)
            .Select(x => x.TradeDate.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        // 若区间内无结算记录，仍给出今日点（便于空池预览）
        if (dates.Count == 0 && toDate >= DateTime.Today && fromDate <= DateTime.Today)
            dates.Add(DateTime.Today);

        var dateLabels = dates.Select(d => d.ToString("yyyy-MM-dd")).ToList();
        var lines = new List<PoolReturnSeriesLineDto>(items.Count);

        foreach (var item in items)
        {
            var symbolPnls = allPnls
                .Where(x => string.Equals(x.Symbol, item.Symbol, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.TradeDate.Date)
                .Select(g => new
                {
                    Date = g.Key,
                    Realized = g.Sum(x => x.RealizedPnL),
                    Fees = g.Sum(x => x.Fees),
                    TradeCount = g.Sum(x => x.TradeCount),
                    WinRate = g.Average(x => x.WinRate)
                })
                .OrderBy(x => x.Date)
                .ToList();

            var byDate = symbolPnls.ToDictionary(x => x.Date, x => x);
            var initial = item.InitialCash > 0 ? item.InitialCash : StockPoolAppService.DefaultPerStockCash;

            // 区间前累计
            var cum = symbolPnls.Where(x => x.Date < fromDate).Sum(x => x.Realized);
            decimal? lastReturn = initial == 0 ? null : Math.Round(cum / initial * 100m, 4);
            var points = new List<PoolReturnPointDto>(dates.Count);

            posBySymbol.TryGetValue(item.Symbol, out var pos);
            var positionCost = pos is null ? 0m : Math.Round(pos.AvgCost * pos.Quantity, 2);

            foreach (var day in dates)
            {
                decimal dayRealized = 0, dayFees = 0, winRate = 0;
                var tradeCount = 0;
                if (byDate.TryGetValue(day, out var row))
                {
                    dayRealized = row.Realized;
                    dayFees = row.Fees;
                    tradeCount = row.TradeCount;
                    winRate = row.WinRate;
                    cum += dayRealized;
                    lastReturn = initial == 0 ? null : Math.Round(cum / initial * 100m, 4);
                }

                var isToday = day == DateTime.Today;
                points.Add(new PoolReturnPointDto(
                    day.ToString("yyyy-MM-dd"),
                    lastReturn,
                    Math.Round(dayRealized, 2),
                    Math.Round(cum, 2),
                    Math.Round(dayFees, 2),
                    tradeCount,
                    Math.Round(winRate, 4),
                    isToday ? Math.Round(item.AvailableCash, 2) : null,
                    isToday ? positionCost : null));
            }

            lines.Add(new PoolReturnSeriesLineDto(
                item.Symbol,
                item.Name,
                item.TradeStyle,
                initial,
                points));
        }

        return new PoolReturnSeriesDto(dateLabels, lines);
    }

    public async Task<(bool Ok, string Message)> DeleteDailyPnLAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _db.DailyPnLs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return (false, "记录不存在");
        _db.DailyPnLs.Remove(row);
        await _db.SaveChangesAsync(ct);
        return (true, "已删除盈亏记录");
    }

    private async Task<StockPool?> GetDefaultPoolAsync(CancellationToken ct)
    {
        return await _db.StockPools
            .Where(p => p.IsActive)
            .OrderBy(p => p.CreationTime)
            .FirstOrDefaultAsync(ct);
    }
}
