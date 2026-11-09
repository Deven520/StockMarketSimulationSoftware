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
        var groups = trades.GroupBy(t => new { t.SimAccountId, t.StockPoolId, t.StrategyDefinitionId, t.Symbol });

        foreach (var g in groups)
        {
            var buy = g.Where(x => x.Side == OrderSide.Buy).Sum(x => x.Amount + x.TotalFee);
            var sell = g.Where(x => x.Side == OrderSide.Sell).Sum(x => x.Amount - x.TotalFee);
            var fees = g.Sum(x => x.TotalFee);
            var realized = sell - buy;
            var sellTrades = g.Where(x => x.Side == OrderSide.Sell).ToList();
            var winRate = sellTrades.Count == 0
                ? 0
                : (decimal)sellTrades.Count(t => t.Amount > t.TotalFee) / sellTrades.Count;

            _db.DailyPnLs.Add(new DailyPnL
            {
                TradeDate = date,
                SimAccountId = g.Key.SimAccountId,
                StockPoolId = g.Key.StockPoolId,
                StrategyDefinitionId = g.Key.StrategyDefinitionId,
                Symbol = g.Key.Symbol,
                TradeCount = g.Count(),
                RealizedPnL = Math.Round(realized, 2),
                Fees = fees,
                WinRate = Math.Round(winRate, 4)
            });
        }

        await _db.SaveChangesAsync(ct);
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
