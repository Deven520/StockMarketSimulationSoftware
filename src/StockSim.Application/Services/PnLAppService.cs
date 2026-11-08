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
            // 简化胜率：卖出笔数中金额覆盖成本的比例近似
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

    public async Task<IReadOnlyList<DailyPnLDto>> QueryAsync(DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var q = _db.DailyPnLs.AsQueryable();
        if (from.HasValue) q = q.Where(x => x.TradeDate >= from.Value.Date);
        if (to.HasValue) q = q.Where(x => x.TradeDate <= to.Value.Date);

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
        var mv = account.Positions.Sum(p => p.AvgCost * p.Quantity);
        // SQLite 不支持对 decimal 做服务端 Sum，先取回再在内存聚合
        var todayPnLRows = await _db.DailyPnLs
            .Where(x => x.TradeDate == DateTime.Today)
            .Select(x => x.RealizedPnL)
            .ToListAsync(ct);
        var todayPnL = todayPnLRows.Sum();
        var todayTrades = await _db.Trades.CountAsync(t => t.TradeDate == DateTime.Today, ct);

        return new DashboardDto(
            account.Cash,
            Math.Round(mv, 2),
            Math.Round(account.Cash + mv, 2),
            todayPnL,
            todayTrades,
            account.TradingMode.ToString(),
            "模拟交易，非实盘投资建议");
    }
}
