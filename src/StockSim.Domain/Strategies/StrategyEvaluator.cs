using StockSim.Domain.Entities;
using StockSim.Domain.MarketData;

namespace StockSim.Domain.Strategies;

public interface IStrategyEvaluator
{
    /// <summary>自适应状态机评估（主路径）。</summary>
    TradeSignal Evaluate(EvaluateContext ctx);

    /// <summary>兼容旧调用：映射为基于开盘/基准价的简易上下文。</summary>
    TradeSignal Evaluate(
        StrategyParameters parameters,
        QuoteTick quote,
        Position? position,
        decimal dayOpenOrBasePrice,
        Guid strategyDefinitionId,
        Guid? stockPoolId);
}

/// <summary>
/// 自适应日内做T：先高抛后低吸状态机 + VWAP/锚点偏离 + 量能确认。
/// </summary>
public sealed class IntradayTStrategyEvaluator : IStrategyEvaluator
{
    public TradeSignal Evaluate(
        StrategyParameters parameters,
        QuoteTick quote,
        Position? position,
        decimal dayOpenOrBasePrice,
        Guid strategyDefinitionId,
        Guid? stockPoolId)
    {
        var effective = parameters.ToEffective();
        var anchor = dayOpenOrBasePrice > 0 ? dayOpenOrBasePrice : quote.LastPrice;
        var deviation = anchor > 0 ? (quote.LastPrice - anchor) / anchor * 100m : 0m;
        var runtime = new SymbolRuntimeState { Symbol = quote.Symbol, TradeDate = DateTime.Today, State = TState.Idle };
        // 旧路径无状态：按偏离直接映射（跌买涨卖），便于单测兼容
        if (deviation <= effective.BuyDeviationPct)
            runtime.State = TState.SoldHigh; // 允许买回
        else if (deviation >= effective.SellDeviationPct)
            runtime.State = TState.Idle; // 允许高抛

        return Evaluate(new EvaluateContext
        {
            Policy = parameters,
            Effective = effective,
            Quote = quote,
            Position = position,
            Runtime = runtime,
            AnchorPrice = anchor,
            DeviationPct = deviation,
            VolumeRatio = 1m,
            RealizedVolPct = 0.5m,
            Now = DateTime.Now,
            StrategyDefinitionId = strategyDefinitionId,
            StockPoolId = stockPoolId,
            TradesToday = 0
        });
    }

    public TradeSignal Evaluate(EvaluateContext ctx)
    {
        var quote = ctx.Quote;
        var eff = ctx.Effective;
        var runtime = ctx.Runtime;
        var position = ctx.Position;
        var id = ctx.StrategyDefinitionId;
        var poolId = ctx.StockPoolId;

        if (eff.Paused)
            return None(quote.Symbol, id, poolId, $"暂停: {eff.PauseReason ?? "override"}");

        if (ctx.AnchorPrice <= 0)
            return None(quote.Symbol, id, poolId, "无有效锚点价");

        // 涨跌停附近
        if (quote.IsLimitUp || quote.IsLimitDown)
        {
            runtime.State = TState.RiskHalt;
            return None(quote.Symbol, id, poolId, "涨跌停附近暂停");
        }

        // 入池后、做T日前：仅按行情建底仓，禁止高抛低吸
        if (ctx.NeedsBaseBuild)
        {
            runtime.State = TState.BuildingBase;
            return TryBuildBase(ctx);
        }

        if (!ctx.CanDoT)
        {
            runtime.State = TState.BuildingBase;
            return None(quote.Symbol, id, poolId, "已有底仓，等待下一交易日开始做T");
        }

        // 时段
        if (SessionWindowHelper.IsAtOrAfter(ctx.Now, eff.ForceFlatBefore))
            runtime.State = TState.ForceFlat;
        else if (SessionWindowHelper.IsInAnyWindow(ctx.Now, eff.AvoidWindows)
                 && runtime.State is not (TState.SoldHigh or TState.BoughtLow or TState.ForceFlat))
            runtime.State = TState.SessionAvoid;
        else if ((runtime.State is TState.SessionAvoid or TState.BuildingBase)
                 && !SessionWindowHelper.IsInAnyWindow(ctx.Now, eff.AvoidWindows))
            runtime.State = TState.Idle;

        // 日亏停
        if (eff.DayLossStopPct < 0 && ctx.AnchorPrice > 0)
        {
            var dayLossPct = ctx.AnchorPrice == 0
                ? 0
                : runtime.DayRealizedPnL / (ctx.AnchorPrice * Math.Max(position?.BaseQuantity ?? 100, 100)) * 100m;
            if (dayLossPct <= eff.DayLossStopPct)
            {
                runtime.State = TState.RiskHalt;
                return None(quote.Symbol, id, poolId, $"日亏 {dayLossPct:F2}% 触发停机");
            }
        }

        if (runtime.ConsecutiveLosses >= eff.SymbolPauseOnConsecutiveLosses && eff.SymbolPauseOnConsecutiveLosses > 0)
        {
            runtime.State = TState.RiskHalt;
            return None(quote.Symbol, id, poolId, $"连亏 {runtime.ConsecutiveLosses} 次暂停");
        }

        if (runtime.State == TState.RiskHalt)
            return None(quote.Symbol, id, poolId, "风控冻结中");

        if (runtime.State == TState.SessionAvoid)
            return None(quote.Symbol, id, poolId, "avoid 时段观望");

        var baseQty = position?.BaseQuantity > 0
            ? position.BaseQuantity
            : position?.Quantity ?? 0;
        if (baseQty < 100 && runtime.State != TState.SoldHigh)
            return None(quote.Symbol, id, poolId, "底仓不足100股");

        var tradeQty = AlignLot((int)Math.Floor(Math.Max(baseQty, 100) * eff.PositionPctPerTrade));
        if (tradeQty < 100) tradeQty = 100;

        var volOk = !eff.UseVolumeConfirm || ctx.VolumeRatio >= eff.MinVolumeRatio;
        var deviation = ctx.DeviationPct;

        // ForceFlat：只平 T 腿（买回 SoldHigh / 卖出 BoughtLow）
        if (runtime.State == TState.ForceFlat)
        {
            if (runtime.LastSellPrice.HasValue || HasSoldHighDeficit(position))
            {
                var buy = TryBuyBack(quote, position, tradeQty, eff, id, poolId, "收盘前买回");
                if (buy.Action == SignalAction.Buy)
                {
                    runtime.State = TState.Idle;
                    return buy;
                }
            }

            if (runtime.State == TState.BoughtLow || (position?.Quantity ?? 0) > (position?.BaseQuantity ?? 0))
            {
                var sell = TrySellHigh(quote, position, tradeQty, eff, id, poolId, "收盘前减仓");
                if (sell.Action == SignalAction.Sell)
                {
                    runtime.State = TState.Idle;
                    return sell;
                }
            }

            return None(quote.Symbol, id, poolId, "ForceFlat 无需动作");
        }

        // SoldHigh → 低吸买回
        if (runtime.State == TState.SoldHigh)
        {
            if (deviation <= eff.BuyDeviationPct && volOk)
            {
                var buy = TryBuyBack(quote, position, tradeQty, eff, id, poolId,
                    $"低吸买回 偏离 {deviation:F2}%≤{eff.BuyDeviationPct:F2}%");
                if (buy.Action == SignalAction.Buy)
                {
                    runtime.State = TState.Idle;
                    runtime.LastBuyPrice = buy.Price;
                    return buy;
                }
            }

            return None(quote.Symbol, id, poolId, "SoldHigh 等待低吸");
        }

        // BoughtLow → 高抛卖出
        if (runtime.State == TState.BoughtLow)
        {
            if (deviation >= eff.SellDeviationPct && volOk)
            {
                var sell = TrySellHigh(quote, position, tradeQty, eff, id, poolId,
                    $"高抛 偏离 {deviation:F2}%≥{eff.SellDeviationPct:F2}%");
                if (sell.Action == SignalAction.Sell)
                {
                    runtime.State = TState.Idle;
                    runtime.LastSellPrice = sell.Price;
                    return sell;
                }
            }

            return None(quote.Symbol, id, poolId, "BoughtLow 等待高抛");
        }

        // Idle：默认先高抛；若已明显低于锚点且有卖出缺口则允许买回（兼容旧底仓未卖场景）
        if (runtime.State == TState.Idle)
        {
            if (deviation >= eff.SellDeviationPct && volOk)
            {
                var sell = TrySellHigh(quote, position, tradeQty, eff, id, poolId,
                    $"高抛 偏离 {deviation:F2}%≥{eff.SellDeviationPct:F2}%");
                if (sell.Action == SignalAction.Sell)
                {
                    runtime.State = TState.SoldHigh;
                    runtime.LastSellPrice = sell.Price;
                    return sell;
                }
            }

            // 风格为 mean_reversion 且持仓低于底仓：允许补回
            if (deviation <= eff.BuyDeviationPct && volOk && HasSoldHighDeficit(position))
            {
                var buy = TryBuyBack(quote, position, tradeQty, eff, id, poolId,
                    $"补回底仓 偏离 {deviation:F2}%");
                if (buy.Action == SignalAction.Buy)
                {
                    runtime.State = TState.Idle;
                    runtime.LastBuyPrice = buy.Price;
                    return buy;
                }
            }

            // momentum_t：允许先低吸再高抛
            if (string.Equals(eff.Style, "momentum_t", StringComparison.OrdinalIgnoreCase)
                && deviation <= eff.BuyDeviationPct && volOk
                && (position?.Quantity ?? 0) >= 100)
            {
                var buy = new TradeSignal
                {
                    Symbol = quote.Symbol,
                    Action = SignalAction.Buy,
                    Quantity = tradeQty,
                    Price = quote.Ask > 0 ? quote.Ask : quote.LastPrice,
                    Reason = $"动量低吸 偏离 {deviation:F2}%",
                    StrategyDefinitionId = id,
                    StockPoolId = poolId
                };
                runtime.State = TState.BoughtLow;
                runtime.LastBuyPrice = buy.Price;
                return buy;
            }

            return None(quote.Symbol, id, poolId, volOk ? "Idle 未触发" : $"量比 {ctx.VolumeRatio:F2} 不足");
        }

        return None(quote.Symbol, id, poolId, $"未知状态 {runtime.State}");
    }

    private static TradeSignal TryBuildBase(EvaluateContext ctx)
    {
        var quote = ctx.Quote;
        var eff = ctx.Effective;
        var id = ctx.StrategyDefinitionId;
        var poolId = ctx.StockPoolId;

        if (!eff.BaseBuildEnabled)
            return None(quote.Symbol, id, poolId, "baseBuild 已关闭，等待人工建仓");

        if (SessionWindowHelper.IsInAnyWindow(ctx.Now, eff.AvoidWindows))
            return None(quote.Symbol, id, poolId, "建底仓：avoid 时段观望");

        if (eff.BaseBuildRequirePreferWindow
            && !SessionWindowHelper.IsInAnyWindow(ctx.Now, eff.PreferWindows))
            return None(quote.Symbol, id, poolId, "建底仓：等待 prefer 时段");

        var chg = quote.ChangePercent;
        if (chg < eff.BaseBuildMinChangePct || chg > eff.BaseBuildMaxChangePct)
        {
            return None(quote.Symbol, id, poolId,
                $"建底仓：涨跌幅 {chg:F2}% 不在 [{eff.BaseBuildMinChangePct:F1},{eff.BaseBuildMaxChangePct:F1}]");
        }

        var price = quote.Ask > 0 ? quote.Ask : quote.LastPrice;
        if (price <= 0)
            return None(quote.Symbol, id, poolId, "建底仓：无效价格");

        var budgetByPct = ctx.AvailableCash * eff.BaseBuildMaxCashPct;
        var budgetByReserve = Math.Max(0, ctx.AvailableCash - eff.BaseBuildMinCashReserve);
        var budget = Math.Min(budgetByPct, budgetByReserve);
        if (budget < price * 100)
            return None(quote.Symbol, id, poolId, $"建底仓：可用预算 {budget:F0} 不足1手");

        var qty = AlignLot((int)Math.Floor(budget / price));
        if (qty < 100)
            return None(quote.Symbol, id, poolId, "建底仓：手数不足1手");

        return new TradeSignal
        {
            Symbol = quote.Symbol,
            Action = SignalAction.Buy,
            Quantity = qty,
            Price = price,
            Reason =
                $"建底仓 预算{budget:F0} 涨跌{chg:F2}% 约{qty}股",
            StrategyDefinitionId = id,
            StockPoolId = poolId,
            IsBaseBuild = true
        };
    }

    private static bool HasSoldHighDeficit(Position? position)
    {
        if (position is null) return false;
        var baseQty = position.BaseQuantity > 0 ? position.BaseQuantity : position.Quantity;
        return baseQty - position.Quantity >= 100;
    }

    private static TradeSignal TryBuyBack(
        QuoteTick quote,
        Position? position,
        int tradeQty,
        EffectiveStrategyParams eff,
        Guid id,
        Guid? poolId,
        string reason)
    {
        var baseQty = position?.BaseQuantity > 0 ? position.BaseQuantity : position?.Quantity ?? 0;
        var deficit = Math.Max(0, baseQty - (position?.Quantity ?? 0));
        var qty = deficit >= 100 ? AlignLot(Math.Min(deficit, tradeQty)) : tradeQty;
        if (qty < 100)
            return None(quote.Symbol, id, poolId, "买入数量不足1手");

        return new TradeSignal
        {
            Symbol = quote.Symbol,
            Action = SignalAction.Buy,
            Quantity = qty,
            Price = quote.Ask > 0 ? quote.Ask : quote.LastPrice,
            Reason = reason,
            StrategyDefinitionId = id,
            StockPoolId = poolId
        };
    }

    private static TradeSignal TrySellHigh(
        QuoteTick quote,
        Position? position,
        int tradeQty,
        EffectiveStrategyParams eff,
        Guid id,
        Guid? poolId,
        string reason)
    {
        var available = position?.Quantity ?? 0;
        // 模拟盘允许做T时用 Quantity；严格 T+1 用 SellableQuantity
        if (position is not null && position.SellableQuantity > 0)
            available = Math.Min(available, position.SellableQuantity);

        var qty = AlignLot(Math.Min(available, tradeQty));
        if (qty < 100)
            return None(quote.Symbol, id, poolId, "可卖数量不足1手");

        var keep = Math.Max(100, eff.KeepBaseLot);
        if (available - qty < keep && available > keep)
            qty = AlignLot(available - keep);
        if (qty < 100)
            return None(quote.Symbol, id, poolId, "保留底仓后无可卖");

        return new TradeSignal
        {
            Symbol = quote.Symbol,
            Action = SignalAction.Sell,
            Quantity = qty,
            Price = quote.Bid > 0 ? quote.Bid : quote.LastPrice,
            Reason = reason,
            StrategyDefinitionId = id,
            StockPoolId = poolId
        };
    }

    private static int AlignLot(int qty) => qty / 100 * 100;

    private static TradeSignal None(string symbol, Guid strategyId, Guid? poolId, string reason) => new()
    {
        Symbol = symbol,
        Action = SignalAction.None,
        Quantity = 0,
        Price = 0,
        Reason = reason,
        StrategyDefinitionId = strategyId,
        StockPoolId = poolId
    };
}
