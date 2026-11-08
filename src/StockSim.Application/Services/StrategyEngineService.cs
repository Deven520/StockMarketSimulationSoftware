using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockSim.Application.Services.Alerts;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.MarketData;
using StockSim.Domain.Strategies;
using StockSim.Domain.Trading;

namespace StockSim.Application.Services;

/// <summary>
/// Worker：开市连续竞价下的自适应做T（状态机 + VWAP锚点 + 盘中 Overrides + 决策日志）。
/// </summary>
public class StrategyEngineService
{
    private readonly IStockSimDataStore _db;
    private readonly IMarketDataProvider _marketData;
    private readonly IStrategyEvaluator _evaluator;
    private readonly IMatchingEngine _matchingEngine;
    private readonly IRiskChecker _riskChecker;
    private readonly IntradaySessionTracker _sessionTracker;
    private readonly AdaptiveOverrideService _adaptive;
    private readonly ITradeAlertNotifier _alerts;
    private readonly ILogger<StrategyEngineService> _logger;
    private readonly ConcurrentDictionary<string, SymbolRuntimeState> _runtime = new(StringComparer.OrdinalIgnoreCase);

    public StrategyEngineService(
        IStockSimDataStore db,
        IMarketDataProvider marketData,
        IStrategyEvaluator evaluator,
        IMatchingEngine matchingEngine,
        IRiskChecker riskChecker,
        IntradaySessionTracker sessionTracker,
        AdaptiveOverrideService adaptive,
        ITradeAlertNotifier alerts,
        ILogger<StrategyEngineService> logger)
    {
        _db = db;
        _marketData = marketData;
        _evaluator = evaluator;
        _matchingEngine = matchingEngine;
        _riskChecker = riskChecker;
        _sessionTracker = sessionTracker;
        _adaptive = adaptive;
        _alerts = alerts;
        _logger = logger;
    }

    public async Task<int> EvaluateOnceAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;
        if (!AShareMarketSession.IsContinuousAuction(now))
        {
            return 0;
        }

        var account = await _db.Accounts.Include(a => a.Positions).FirstOrDefaultAsync(ct);
        if (account is null) return 0;

        var bindings = await _db.StrategyBindings
            .Where(b => b.IsEnabled)
            .OrderByDescending(b => b.Priority)
            .ToListAsync(ct);

        var strategies = await _db.Strategies
            .Include(s => s.Versions)
            .Where(s => s.IsEnabled)
            .ToListAsync(ct);

        var poolItems = await _db.StockPoolItems.ToListAsync(ct);
        var filled = 0;

        foreach (var binding in bindings)
        {
            var strategy = strategies.FirstOrDefault(s => s.Id == binding.StrategyDefinitionId);
            if (strategy is null) continue;

            var version = strategy.Versions.FirstOrDefault(v => v.Id == strategy.CurrentVersionId)
                          ?? strategy.Versions.MaxBy(v => v.VersionNumber);
            if (version is null) continue;

            var policy = StrategyParameters.FromJson(version.ParametersJson);
            var symbols = new List<string>();
            if (!string.IsNullOrWhiteSpace(binding.Symbol))
            {
                symbols.Add(binding.Symbol);
            }
            else if (binding.StockPoolId.HasValue)
            {
                symbols.AddRange(poolItems
                    .Where(i => i.StockPoolId == binding.StockPoolId && i.BoundStrategyId == null)
                    .Select(i => i.Symbol));
            }

            foreach (var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var quote = await _marketData.GetLastAsync(symbol, ct);
                if (quote is null) continue;

                var poolItem = poolItems.FirstOrDefault(i =>
                    string.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                    && (binding.StockPoolId == null || i.StockPoolId == binding.StockPoolId));

                var position = account.Positions.FirstOrDefault(p => p.Symbol == symbol);
                if (position is not null && string.IsNullOrWhiteSpace(position.Name) && poolItem is not null)
                    position.Name = poolItem.Name;

                var runtime = await GetOrLoadRuntimeAsync(symbol, now, ct);
                var snap = _sessionTracker.Update(quote, now);
                var overrides = _adaptive.Adjust(policy, runtime, snap, now);
                var effective = policy.ToEffective(overrides);
                var anchor = _sessionTracker.ResolveAnchor(effective.Anchor, snap);
                if (anchor <= 0) anchor = quote.LastPrice;
                var deviation = (quote.LastPrice - anchor) / anchor * 100m;
                var tradesToday = await TodayTradeCount(account.Id, symbol, ct);

                // 刷新当日已实现（简化：今日该股卖出盈亏近似）
                runtime.DayRealizedPnL = await ApproxDayRealizedAsync(account.Id, symbol, position?.AvgCost ?? 0, ct);

                var heldQty = position?.Quantity ?? 0;
                var baseQty = position?.BaseQuantity > 0 ? position.BaseQuantity : heldQty;
                var needsBaseBuild = baseQty < 100;
                var canDoT = !needsBaseBuild && IsAllowTDay(poolItem, now);

                var ctx = new EvaluateContext
                {
                    Policy = policy,
                    Effective = effective,
                    Quote = quote,
                    Position = position,
                    Runtime = runtime,
                    AnchorPrice = anchor,
                    DeviationPct = deviation,
                    VolumeRatio = snap.VolumeRatio,
                    RealizedVolPct = snap.RealizedVolPct,
                    Now = now,
                    StrategyDefinitionId = strategy.Id,
                    StockPoolId = binding.StockPoolId ?? poolItem?.StockPoolId,
                    TradesToday = tradesToday,
                    AvailableCash = poolItem?.AvailableCash ?? account.Cash,
                    CanDoT = canDoT,
                    NeedsBaseBuild = needsBaseBuild
                };

                var stateBefore = runtime.State;
                var signal = _evaluator.Evaluate(ctx);
                var stateChanged = stateBefore != runtime.State;

                if (signal.Action == SignalAction.None)
                {
                    if (stateChanged)
                        await PersistRuntimeAsync(runtime, ct);

                    // 抽样记录未触发（避免刷屏：仅在接近阈值时记）
                    if (Math.Abs(deviation) >= Math.Min(Math.Abs(effective.BuyDeviationPct), effective.SellDeviationPct) * 0.8m)
                    {
                        await AddDecisionLogAsync(symbol, runtime, signal, quote, anchor, deviation, effective, strategy.Id, signal.Reason, ct);
                        await _db.SaveChangesAsync(ct);
                    }

                    continue;
                }

                decimal? cashBudget = poolItem?.AvailableCash;
                var risk = _riskChecker.Validate(
                    signal, account, position, quote, tradesToday, cashBudget);
                if (!risk.IsAllowed)
                {
                    _db.Orders.Add(new Order
                    {
                        SimAccountId = account.Id,
                        StrategyDefinitionId = strategy.Id,
                        StockPoolId = binding.StockPoolId ?? poolItem?.StockPoolId,
                        Symbol = symbol,
                        Side = signal.Action == SignalAction.Buy ? OrderSide.Buy : OrderSide.Sell,
                        Quantity = signal.Quantity,
                        Price = signal.Price,
                        Status = OrderStatus.Rejected,
                        RejectReason = risk.Reason,
                        TradeDate = DateTime.Today,
                        IsManual = false
                    });
                    await AddDecisionLogAsync(symbol, runtime, signal, quote, anchor, deviation, effective, strategy.Id, risk.Reason, ct);
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                var result = _matchingEngine.PlaceAndFill(
                    account,
                    position,
                    signal,
                    DateTime.Today,
                    isManual: false,
                    effective.MaxTradesPerDay,
                    tradesToday,
                    cashBudget,
                    onCashBudgetChanged: poolItem is null
                        ? null
                        : newCash => { poolItem.AvailableCash = newCash; });

                var pos = account.Positions.FirstOrDefault(p => p.Symbol == symbol);
                if (pos is not null && poolItem is not null && string.IsNullOrWhiteSpace(pos.Name))
                    pos.Name = poolItem.Name;

                _db.Orders.Add(result.Order);
                if (result.Trade is not null)
                {
                    _db.Trades.Add(result.Trade);
                    filled++;
                    UpdateRuntimeAfterFill(runtime, signal, result.Trade);

                    // 建底仓成交：记 BaseQuantity；做T买回时 BaseQuantity 已≥100，不会误覆盖
                    if (signal.Action == SignalAction.Buy)
                    {
                        var posAfter = account.Positions.FirstOrDefault(p =>
                            string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
                        if (posAfter is not null && posAfter.BaseQuantity < 100 && posAfter.Quantity >= 100)
                        {
                            posAfter.BaseQuantity = posAfter.Quantity;
                            posAfter.SellableQuantity = Math.Max(posAfter.SellableQuantity, posAfter.Quantity);
                        }
                    }
                }

                await AddDecisionLogAsync(symbol, runtime, signal, quote, anchor, deviation, effective, strategy.Id,
                    result.Trade is null ? result.Order.RejectReason : null, ct);
                await PersistRuntimeAsync(runtime, ct);
                await _db.SaveChangesAsync(ct);

                if (result.Trade is not null && poolItem?.NotifyOnTrade == true)
                {
                    await TryNotifyTradeAsync(
                        poolItem,
                        result.Trade,
                        strategy.Name,
                        ct);
                }
            }
        }

        return filled;
    }

    private async Task TryNotifyTradeAsync(
        StockPoolItem poolItem,
        Trade trade,
        string? strategyName,
        CancellationToken ct)
    {
        try
        {
            var side = trade.Side == OrderSide.Buy ? "买入" : "卖出";
            await _alerts.NotifyAsync(new TradeAlertMessage(
                poolItem.Symbol,
                poolItem.Name,
                side,
                trade.Quantity,
                trade.Price,
                DateTime.Now,
                strategyName,
                trade.Id), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trade alert failed for {Symbol}", poolItem.Symbol);
        }
    }

    private static void UpdateRuntimeAfterFill(SymbolRuntimeState runtime, TradeSignal signal, Trade trade)
    {
        // 粗略连亏：卖出价低于上次买 / 买入高于上次卖
        if (signal.Action == SignalAction.Sell)
        {
            runtime.LastSellPrice = trade.Price;
            if (runtime.LastBuyPrice.HasValue && trade.Price < runtime.LastBuyPrice.Value)
                runtime.ConsecutiveLosses++;
            else
                runtime.ConsecutiveLosses = 0;
        }
        else if (signal.Action == SignalAction.Buy)
        {
            runtime.LastBuyPrice = trade.Price;
            if (runtime.LastSellPrice.HasValue && trade.Price > runtime.LastSellPrice.Value)
                runtime.ConsecutiveLosses++;
            else if (runtime.LastSellPrice.HasValue && trade.Price <= runtime.LastSellPrice.Value)
                runtime.ConsecutiveLosses = 0;
        }

        runtime.UpdatedAt = DateTime.UtcNow;
    }

    private async Task<SymbolRuntimeState> GetOrLoadRuntimeAsync(string symbol, DateTime now, CancellationToken ct)
    {
        if (_runtime.TryGetValue(symbol, out var mem))
        {
            mem.ResetIfNewDay(now.Date);
            return mem;
        }

        var row = await _db.StrategyRuntimeStates
            .FirstOrDefaultAsync(r => r.Symbol == symbol && r.TradeDate == now.Date, ct);

        var state = new SymbolRuntimeState
        {
            Symbol = symbol,
            TradeDate = now.Date
        };

        if (row is not null)
        {
            state.State = Enum.TryParse<TState>(row.State, true, out var st) ? st : TState.Idle;
            state.ConsecutiveLosses = row.ConsecutiveLosses;
            state.DayRealizedPnL = row.DayRealizedPnL;
            state.LastSellPrice = row.LastSellPrice;
            state.LastBuyPrice = row.LastBuyPrice;
            if (!string.IsNullOrWhiteSpace(row.OverridesJson))
            {
                try
                {
                    state.Overrides = JsonSerializer.Deserialize<RuntimeOverrides>(row.OverridesJson) ?? new();
                }
                catch
                {
                    state.Overrides = new();
                }
            }
        }

        _runtime[symbol] = state;
        return state;
    }

    private async Task PersistRuntimeAsync(SymbolRuntimeState runtime, CancellationToken ct)
    {
        var row = await _db.StrategyRuntimeStates
            .FirstOrDefaultAsync(r => r.Symbol == runtime.Symbol && r.TradeDate == runtime.TradeDate, ct);
        if (row is null)
        {
            row = new StrategyRuntimeStateRow
            {
                Symbol = runtime.Symbol,
                TradeDate = runtime.TradeDate
            };
            _db.StrategyRuntimeStates.Add(row);
        }

        row.State = runtime.State.ToString();
        row.ConsecutiveLosses = runtime.ConsecutiveLosses;
        row.DayRealizedPnL = runtime.DayRealizedPnL;
        row.LastSellPrice = runtime.LastSellPrice;
        row.LastBuyPrice = runtime.LastBuyPrice;
        row.OverridesJson = JsonSerializer.Serialize(runtime.Overrides);
        row.LastModificationTime = DateTime.UtcNow;
        // SaveChanges 由调用方统一提交（或此处立即提交以确保崩溃恢复）
        await _db.SaveChangesAsync(ct);
    }

    private async Task AddDecisionLogAsync(
        string symbol,
        SymbolRuntimeState runtime,
        TradeSignal signal,
        QuoteTick quote,
        decimal anchor,
        decimal deviation,
        EffectiveStrategyParams effective,
        Guid strategyId,
        string? blockedBy,
        CancellationToken ct)
    {
        _db.StrategyDecisionLogs.Add(new StrategyDecisionLog
        {
            Symbol = symbol,
            TradeDate = DateTime.Today,
            DecidedAt = DateTime.UtcNow,
            State = runtime.State.ToString(),
            Action = signal.Action.ToString(),
            Reason = signal.Reason,
            LastPrice = quote.LastPrice,
            AnchorPrice = anchor,
            DeviationPct = deviation,
            EffectiveParamsJson = JsonSerializer.Serialize(new
            {
                effective.BuyDeviationPct,
                effective.SellDeviationPct,
                effective.PositionPctPerTrade,
                effective.Anchor,
                effective.Paused
            }),
            StrategyDefinitionId = strategyId,
            BlockedBy = blockedBy
        });
        await Task.CompletedTask;
    }

    private async Task<decimal> ApproxDayRealizedAsync(Guid accountId, string symbol, decimal avgCost, CancellationToken ct)
    {
        var trades = await _db.Trades
            .Where(t => t.SimAccountId == accountId && t.Symbol == symbol && t.TradeDate == DateTime.Today)
            .ToListAsync(ct);
        decimal pnl = 0;
        foreach (var t in trades)
        {
            if (t.Side == OrderSide.Sell)
                pnl += (t.Price - avgCost) * t.Quantity - t.TotalFee;
            else
                pnl -= t.TotalFee;
        }

        return pnl;
    }

    private Task<int> TodayTradeCount(Guid accountId, string symbol, CancellationToken ct) =>
        _db.Trades.CountAsync(
            t => t.SimAccountId == accountId && t.TradeDate == DateTime.Today && t.Symbol == symbol, ct);

    /// <summary>null AllowTFromDate = 旧数据，有底仓即可做 T；否则需到起始日。</summary>
    private static bool IsAllowTDay(StockPoolItem? item, DateTime now)
    {
        if (item?.AllowTFromDate is null) return true;
        return now.Date >= item.AllowTFromDate.Value.Date;
    }
}
