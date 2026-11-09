using Microsoft.EntityFrameworkCore;
using StockSim.Application.Dtos;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.Strategies;
using StockSim.Domain.Trading;

namespace StockSim.Application.Services;

public interface IStockSimDataStore
{
    DbSet<StockPool> StockPools { get; }
    DbSet<StockPoolItem> StockPoolItems { get; }
    DbSet<StrategyDefinition> Strategies { get; }
    DbSet<StrategyVersion> StrategyVersions { get; }
    DbSet<StrategyBinding> StrategyBindings { get; }
    DbSet<SimAccount> Accounts { get; }
    DbSet<Position> Positions { get; }
    DbSet<Order> Orders { get; }
    DbSet<Trade> Trades { get; }
    DbSet<DailyPnL> DailyPnLs { get; }
    DbSet<AiJob> AiJobs { get; }
    DbSet<AiSuggestion> AiSuggestions { get; }
    DbSet<AiQueueItem> AiQueueItems { get; }
    DbSet<StrategyRuntimeStateRow> StrategyRuntimeStates { get; }
    DbSet<StrategyDecisionLog> StrategyDecisionLogs { get; }
    DbSet<InvestmentGoal> InvestmentGoals { get; }
    DbSet<InvestmentGoalStock> InvestmentGoalStocks { get; }
    DbSet<GoalProgressDaily> GoalProgressDailies { get; }
    DbSet<PriceRangeForecast> PriceRangeForecasts { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public class TradingAppService
{
    private readonly IStockSimDataStore _db;
    private readonly IMatchingEngine _matchingEngine;

    public TradingAppService(IStockSimDataStore db, IMatchingEngine matchingEngine)
    {
        _db = db;
        _matchingEngine = matchingEngine;
    }

    public async Task<AccountDto> GetOrCreateDefaultAccountAsync(CancellationToken ct = default)
    {
        var account = await _db.Accounts.Include(a => a.Positions).FirstOrDefaultAsync(ct);
        if (account is null)
        {
            account = new SimAccount
            {
                Name = "默认模拟账户",
                Cash = 1_000_000m,
                InitialCash = 1_000_000m,
                TradingMode = TradingMode.SimIntradaySellAllowed
            };
            _db.Accounts.Add(account);
            await _db.SaveChangesAsync(ct);
        }

        return new AccountDto(account.Id, account.Name, account.Cash, account.InitialCash, account.TradingMode);
    }

    public async Task<IReadOnlyList<PositionDto>> GetPositionsAsync(CancellationToken ct = default)
    {
        var account = await _db.Accounts.Include(a => a.Positions).FirstAsync(ct);
        return account.Positions
            .Select(p => new PositionDto(p.Id, p.Symbol, p.Name, p.BaseQuantity, p.Quantity, p.SellableQuantity, p.AvgCost))
            .ToList();
    }

    public async Task<(bool Ok, string Message)> PlaceManualOrderAsync(ManualOrderRequest request, CancellationToken ct = default)
    {
        var account = await _db.Accounts.Include(a => a.Positions).FirstAsync(ct);
        var position = account.Positions.FirstOrDefault(p => p.Symbol == request.Symbol);
        var tradesToday = await _db.Trades.CountAsync(t => t.SimAccountId == account.Id && t.TradeDate == DateTime.Today, ct);

        var signal = new TradeSignal
        {
            Symbol = request.Symbol,
            Action = request.Side == OrderSide.Buy ? SignalAction.Buy : SignalAction.Sell,
            Quantity = request.Quantity,
            Price = request.Price,
            Reason = "手工下单",
            StockPoolId = request.StockPoolId
        };

        var result = _matchingEngine.PlaceAndFill(account, position, signal, DateTime.Today, isManual: true, null, tradesToday);
        _db.Orders.Add(result.Order);
        if (result.Trade is not null)
        {
            _db.Trades.Add(result.Trade);
            if (position is null)
            {
                position = account.Positions.FirstOrDefault(p => p.Symbol == request.Symbol);
                if (position is not null && position.BaseQuantity == 0)
                {
                    position.BaseQuantity = position.Quantity;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
        return result.Order.Status == OrderStatus.Filled
            ? (true, "成交成功")
            : (false, result.Order.RejectReason ?? "拒单");
    }

    public async Task<IReadOnlyList<TradeDto>> GetTradesAsync(DateTime? from, DateTime? to, string? symbol, CancellationToken ct = default)
    {
        var q = _db.Trades.AsQueryable();
        if (from.HasValue) q = q.Where(t => t.TradeDate >= from.Value.Date);
        if (to.HasValue) q = q.Where(t => t.TradeDate <= to.Value.Date);
        if (!string.IsNullOrWhiteSpace(symbol)) q = q.Where(t => t.Symbol == symbol);

        return await q.OrderByDescending(t => t.TradedAt)
            .Select(t => new TradeDto(
                t.Id, t.Symbol, t.Side, t.Quantity, t.Price, t.TotalFee, t.Amount,
                t.TradeDate, t.TradedAt, t.StrategyDefinitionId))
            .Take(500)
            .ToListAsync(ct);
    }

    /// <summary>删除成交记录（不回滚持仓/资金，仅清理明细）。</summary>
    public async Task<(bool Ok, string Message)> DeleteTradeAsync(Guid id, CancellationToken ct = default)
    {
        var trade = await _db.Trades.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (trade is null) return (false, "成交不存在");
        _db.Trades.Remove(trade);
        await _db.SaveChangesAsync(ct);
        return (true, $"已删除成交 {trade.Symbol}");
    }

    /// <summary>清空持仓：数量归零；若池内有该成分则 AvailableCash 重置为 InitialCash。</summary>
    public async Task<(bool Ok, string Message)> ClearPositionAsync(Guid id, CancellationToken ct = default)
    {
        var position = await _db.Positions.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (position is null) return (false, "持仓不存在");

        var symbol = position.Symbol;
        position.Quantity = 0;
        position.BaseQuantity = 0;
        position.SellableQuantity = 0;
        position.AvgCost = 0;

        var item = await _db.StockPoolItems.FirstOrDefaultAsync(i => i.Symbol == symbol, ct);
        if (item is not null)
        {
            item.AvailableCash = item.InitialCash > 0 ? item.InitialCash : StockPoolAppService.DefaultPerStockCash;
            item.LastModificationTime = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return (true, $"已清空持仓 {symbol}");
    }
}
