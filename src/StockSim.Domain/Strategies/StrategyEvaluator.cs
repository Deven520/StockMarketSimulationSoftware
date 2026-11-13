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
/// 自适应日内做T：路径剧本 + 先高抛后低吸状态机 + VWAP/锚点偏离 + 量能确认。
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
        if (deviation <= effective.BuyDeviationPct)
            runtime.State = TState.SoldHigh;
        else if (deviation >= effective.SellDeviationPct)
            runtime.State = TState.Idle;

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
            TradesToday = 0,
            ReferencePrice = dayOpenOrBasePrice
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
        var pathTag = string.IsNullOrWhiteSpace(eff.PathLabel) ? "" : $"path={eff.PathLabel} ";

        if (eff.Paused)
            return None(quote.Symbol, id, poolId, $"{pathTag}暂停: {eff.PauseReason ?? "override"}");

        if (ctx.AnchorPrice <= 0)
            return None(quote.Symbol, id, poolId, $"{pathTag}无有效锚点价");

        if (quote.IsLimitUp || quote.IsLimitDown)
        {
            runtime.State = TState.RiskHalt;
            return None(quote.Symbol, id, poolId, $"{pathTag}涨跌停附近暂停");
        }

        if (ctx.NeedsBaseBuild)
        {
            runtime.State = TState.BuildingBase;
            return TryBuildBase(ctx);
        }

        if (!ctx.CanDoT)
        {
            runtime.State = TState.BuildingBase;
            return None(quote.Symbol, id, poolId, $"{pathTag}已有底仓，等待下一交易日开始做T");
        }

        if (SessionWindowHelper.IsAtOrAfter(ctx.Now, eff.ForceFlatBefore))
            runtime.State = TState.ForceFlat;
        else if (SessionWindowHelper.IsInAnyWindow(ctx.Now, eff.AvoidWindows)
                 && runtime.State is not (TState.SoldHigh or TState.BoughtLow or TState.ForceFlat))
            runtime.State = TState.SessionAvoid;
        else if ((runtime.State is TState.SessionAvoid or TState.BuildingBase)
                 && !SessionWindowHelper.IsInAnyWindow(ctx.Now, eff.AvoidWindows))
            runtime.State = TState.Idle;

        if (eff.DayLossStopPct < 0 && ctx.AnchorPrice > 0)
        {
            var dayLossPct = runtime.DayRealizedPnL
                / (ctx.AnchorPrice * Math.Max(position?.BaseQuantity ?? 100, 100)) * 100m;
            if (dayLossPct <= eff.DayLossStopPct)
            {
                runtime.State = TState.RiskHalt;
                return None(quote.Symbol, id, poolId, $"{pathTag}日亏 {dayLossPct:F2}% 触发停机");
            }
        }

        if (runtime.ConsecutiveLosses >= eff.SymbolPauseOnConsecutiveLosses && eff.SymbolPauseOnConsecutiveLosses > 0)
        {
            runtime.State = TState.RiskHalt;
            return None(quote.Symbol, id, poolId, $"{pathTag}连亏 {runtime.ConsecutiveLosses} 次暂停");
        }

        if (runtime.State == TState.RiskHalt)
            return None(quote.Symbol, id, poolId, $"{pathTag}风控冻结中");

        var deviation = ctx.DeviationPct;
        var breakAvoid = eff.AllowBreakAvoid
            && deviation >= Math.Abs(eff.SellDeviationPct) * Math.Max(1m, eff.BreakAvoidSigma);

        if (runtime.State == TState.SessionAvoid)
        {
            if (breakAvoid)
                runtime.State = TState.Idle; // 破 avoid 允许高抛
            else
                return None(quote.Symbol, id, poolId, $"{pathTag}avoid 时段观望");
        }

        var baseQty = position?.BaseQuantity > 0
            ? position.BaseQuantity
            : position?.Quantity ?? 0;
        if (baseQty < 100 && runtime.State != TState.SoldHigh)
            return None(quote.Symbol, id, poolId, $"{pathTag}底仓不足100股");

        var tradeQty = AlignLot((int)Math.Floor(Math.Max(baseQty, 100) * eff.PositionPctPerTrade));
        if (tradeQty < 100) tradeQty = 100;

        var volOk = !eff.UseVolumeConfirm || ctx.VolumeRatio >= eff.MinVolumeRatio;

        // ForceFlat：先平正 T 卖腿；再压超配（减仓不计入 OpenT，禁止卖后再买回空转）
        if (runtime.State == TState.ForceFlat)
        {
            // 当日已做过超配减仓 → 只出不进，禁止任何买回/新开
            if (runtime.ForceFlatExcessReduced
                && runtime.OpenTQty < 100
                && !HasSoldHighDeficit(position))
            {
                var qtyLeft = position?.Quantity ?? 0;
                var baseLeft = position?.BaseQuantity ?? 0;
                var excessLeft = qtyLeft - baseLeft;
                if (excessLeft >= 100)
                {
                    var sellQty = AlignLot(Math.Min(tradeQty, excessLeft));
                    var sellMore = TrySellExcess(quote, position, sellQty, id, poolId,
                        $"{pathTag}收盘前继续减超配");
                    if (sellMore.Action == SignalAction.Sell)
                        return sellMore;
                }

                return None(quote.Symbol, id, poolId, $"{pathTag}ForceFlat 超配已减，禁止买回");
            }

            if (!runtime.ForceFlatExcessReduced
                && (runtime.OpenTQty >= 100 || HasSoldHighDeficit(position)))
            {
                var cap = runtime.OpenTQty >= 100 ? runtime.OpenTQty : tradeQty;
                var buyQty = CapBuyQtyByCashReserve(
                    Math.Min(tradeQty, AlignLot(cap)), quote, ctx.AvailableCash, eff);
                var buy = TryBuyBack(quote, position, buyQty, eff, id, poolId,
                    $"{pathTag}收盘前买回 OpenT={runtime.OpenTQty}");
                if (buy.Action == SignalAction.Buy)
                {
                    runtime.State = TState.Idle;
                    return buy;
                }
            }

            var qtyNow = position?.Quantity ?? 0;
            var baseNow = position?.BaseQuantity ?? 0;
            var excess = qtyNow - baseNow;
            // 仅平「当日有买入痕迹」的超配；CountsTowardOpenT=false 避免引擎累加 OpenT
            if (excess >= 100 && runtime.LastBuyPrice.HasValue && runtime.OpenTQty <= 0)
            {
                var sellQty = AlignLot(Math.Min(tradeQty, excess));
                var sell = TrySellExcess(quote, position, sellQty, id, poolId,
                    $"{pathTag}收盘前减仓");
                if (sell.Action == SignalAction.Sell)
                {
                    runtime.ForceFlatExcessReduced = true;
                    runtime.Overrides.BlockNewT = true;
                    return sell;
                }
            }

            return None(quote.Symbol, id, poolId, $"{pathTag}ForceFlat 无需动作");
        }

        if ((eff.BlockNewT || runtime.ForceFlatExcessReduced)
            && runtime.State == TState.Idle
            && runtime.OpenTQty <= 0
            && !HasSoldHighDeficit(position))
        {
            return None(quote.Symbol, id, poolId,
                runtime.ForceFlatExcessReduced
                    ? $"{pathTag}ForceFlat后禁止新开T"
                    : $"{pathTag}BlockNewT 观望");
        }

        // SoldHigh → 低吸买回（预留现金给第二腿）
        if (runtime.State == TState.SoldHigh)
        {
            if (deviation <= eff.BuyDeviationPct && volOk)
            {
                var (ok, qty, softNote) = ApplySoftBand(ctx, tradeQty, forSell: false);
                if (!ok)
                    return None(quote.Symbol, id, poolId, $"{pathTag}{softNote}");

                qty = CapBuyQtyByCashReserve(qty, quote, ctx.AvailableCash, eff);
                var buy = TryBuyBack(quote, position, qty, eff, id, poolId,
                    $"{pathTag}低吸买回 偏离 {deviation:F2}%≤{eff.BuyDeviationPct:F2}%{softNote}");
                if (buy.Action == SignalAction.Buy)
                {
                    runtime.State = TState.Idle;
                    runtime.LastBuyPrice = buy.Price;
                    return buy;
                }
            }

            return None(quote.Symbol, id, poolId, $"{pathTag}SoldHigh 等待低吸");
        }

        // BoughtLow → 高抛 / 冲高回撤卖（LowOpenHighGo 等动量路径避免空等）
        if (runtime.State == TState.BoughtLow)
        {
            var pullbackExit = eff.PullbackSellPct > 0
                && ctx.PullbackFromHighPct >= eff.PullbackSellPct
                && deviation >= Math.Min(0.15m, eff.SellDeviationPct * 0.4m)
                && volOk;

            if ((deviation >= eff.SellDeviationPct && volOk) || pullbackExit)
            {
                var (ok, qty, softNote) = ApplySoftBand(ctx, tradeQty, forSell: true);
                if (!ok)
                    return None(quote.Symbol, id, poolId, $"{pathTag}{softNote}");

                var why = pullbackExit && deviation < eff.SellDeviationPct
                    ? $"回撤卖 pb={ctx.PullbackFromHighPct:F2}%≥{eff.PullbackSellPct:F2}%"
                    : $"高抛 偏离 {deviation:F2}%≥{eff.SellDeviationPct:F2}%";
                var sell = TrySellHigh(quote, position, qty, eff, id, poolId,
                    $"{pathTag}{why}{softNote}");
                if (sell.Action == SignalAction.Sell)
                {
                    runtime.State = TState.Idle;
                    runtime.LastSellPrice = sell.Price;
                    return sell;
                }
            }

            return None(quote.Symbol, id, poolId, $"{pathTag}BoughtLow 等待高抛");
        }

        // Idle：先高抛 / 冲高回撤 / 补回 / 动量低吸
        if (runtime.State == TState.Idle)
        {
            var pullbackHit = eff.PullbackSellPct > 0
                && ctx.PullbackFromHighPct >= eff.PullbackSellPct
                && deviation >= Math.Min(0.2m, eff.SellDeviationPct * 0.5m)
                && volOk;

            if ((deviation >= eff.SellDeviationPct && volOk) || pullbackHit)
            {
                var (ok, qty, softNote) = ApplySoftBand(ctx, tradeQty, forSell: true);
                if (!ok)
                    return None(quote.Symbol, id, poolId, $"{pathTag}{softNote}");

                var why = pullbackHit && deviation < eff.SellDeviationPct
                    ? $"冲高回撤卖 pb={ctx.PullbackFromHighPct:F2}%≥{eff.PullbackSellPct:F2}%"
                    : $"高抛 偏离 {deviation:F2}%≥{eff.SellDeviationPct:F2}%";
                var sell = TrySellHigh(quote, position, qty, eff, id, poolId, $"{pathTag}{why}{softNote}");
                if (sell.Action == SignalAction.Sell)
                {
                    runtime.State = TState.SoldHigh;
                    runtime.LastSellPrice = sell.Price;
                    return sell;
                }
            }

            if (deviation <= eff.BuyDeviationPct && volOk && HasSoldHighDeficit(position))
            {
                var (ok, qty, softNote) = ApplySoftBand(ctx, tradeQty, forSell: false);
                if (!ok)
                    return None(quote.Symbol, id, poolId, $"{pathTag}{softNote}");

                var buy = TryBuyBack(quote, position, qty, eff, id, poolId,
                    $"{pathTag}补回底仓 偏离 {deviation:F2}%{softNote}");
                if (buy.Action == SignalAction.Buy)
                {
                    runtime.State = TState.Idle;
                    runtime.LastBuyPrice = buy.Price;
                    return buy;
                }
            }

            if (string.Equals(eff.Style, "momentum_t", StringComparison.OrdinalIgnoreCase)
                && deviation <= eff.BuyDeviationPct && volOk
                && (position?.Quantity ?? 0) >= 100
                && !eff.BlockNewT
                && !runtime.ForceFlatExcessReduced)
            {
                var (ok, qty, softNote) = ApplySoftBand(ctx, tradeQty, forSell: false);
                if (!ok)
                    return None(quote.Symbol, id, poolId, $"{pathTag}{softNote}");

                qty = CapBuyQtyByCashReserve(qty, quote, ctx.AvailableCash, eff);
                if (qty < 100)
                    return None(quote.Symbol, id, poolId, $"{pathTag}动量低吸：预留现金后不足1手");

                var buy = new TradeSignal
                {
                    Symbol = quote.Symbol,
                    Action = SignalAction.Buy,
                    Quantity = qty,
                    Price = QuotePriceGuard.SafeAsk(quote),
                    Reason = $"{pathTag}动量低吸 偏离 {deviation:F2}%{softNote}",
                    StrategyDefinitionId = id,
                    StockPoolId = poolId
                };
                runtime.State = TState.BoughtLow;
                runtime.LastBuyPrice = buy.Price;
                return buy;
            }

            return None(quote.Symbol, id, poolId,
                volOk ? $"{pathTag}Idle 未触发" : $"{pathTag}量比 {ctx.VolumeRatio:F2} 不足");
        }

        return None(quote.Symbol, id, poolId, $"{pathTag}未知状态 {runtime.State}");
    }

    private static (bool Ok, int Qty, string SoftNote) ApplySoftBand(
        EvaluateContext ctx, int tradeQty, bool forSell)
    {
        var refPx = ctx.ReferencePrice > 0 ? ctx.ReferencePrice : ctx.AnchorPrice;
        var verdict = PriceForecastMetrics.EvaluateForecastSoftBand(
            ctx.Policy.Forecast,
            ctx.Quote.LastPrice,
            ctx.Now,
            forSell,
            PriceForecastMetrics.ForecastSoftBandRatio,
            out var msg,
            referencePrice: refPx);

        if (verdict == SoftBandVerdict.Block)
            return (false, tradeQty, msg ?? "预测软边界拦截");

        if (verdict == SoftBandVerdict.PreferShrink)
        {
            var scale = ctx.Effective.SoftBandShrinkScale > 0
                ? ctx.Effective.SoftBandShrinkScale
                : PriceForecastMetrics.SoftPreferShrinkScale;
            var qty = AlignLot((int)Math.Floor(tradeQty * scale));
            if (qty < 100) qty = 100;
            return (true, qty, msg is null ? " [软边界缩仓]" : $" [{msg};缩仓]");
        }

        return (true, tradeQty, "");
    }

    private static TradeSignal TryBuildBase(EvaluateContext ctx)
    {
        var quote = ctx.Quote;
        var eff = ctx.Effective;
        var id = ctx.StrategyDefinitionId;
        var poolId = ctx.StockPoolId;

        if (!eff.BaseBuildEnabled)
            return None(quote.Symbol, id, poolId, "baseBuild 已关闭，等待人工建仓");

        if (eff.BaseBuildRequirePreferWindow
            && !SessionWindowHelper.IsInAnyWindow(ctx.Now, eff.PreferWindows))
            return None(quote.Symbol, id, poolId, "建底仓：等待 prefer 时段");

        var chg = quote.ChangePercent;
        if (chg < eff.BaseBuildMinChangePct || chg > eff.BaseBuildMaxChangePct)
        {
            return None(quote.Symbol, id, poolId,
                $"建底仓：涨跌幅 {chg:F2}% 不在 [{eff.BaseBuildMinChangePct:F1},{eff.BaseBuildMaxChangePct:F1}]");
        }

        var price = QuotePriceGuard.SafeAsk(quote);
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
            Reason = $"建底仓 预算{budget:F0} 涨跌{chg:F2}% 约{qty}股",
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
            Price = QuotePriceGuard.SafeAsk(quote),
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
            Price = QuotePriceGuard.SafeBid(quote),
            Reason = reason,
            StrategyDefinitionId = id,
            StockPoolId = poolId,
            CountsTowardOpenT = true
        };
    }

    /// <summary>ForceFlat 超配减仓：可减到 BaseQuantity，不计入 OpenT。</summary>
    private static TradeSignal TrySellExcess(
        QuoteTick quote,
        Position? position,
        int tradeQty,
        Guid id,
        Guid? poolId,
        string reason)
    {
        var available = position?.Quantity ?? 0;
        if (position is not null && position.SellableQuantity > 0)
            available = Math.Min(available, position.SellableQuantity);

        var baseQty = position?.BaseQuantity > 0 ? position.BaseQuantity : 0;
        var maxSell = baseQty > 0 ? Math.Max(0, available - baseQty) : available;
        var qty = AlignLot(Math.Min(Math.Min(available, tradeQty), maxSell));
        if (qty < 100)
            return None(quote.Symbol, id, poolId, "超配可卖不足1手");

        return new TradeSignal
        {
            Symbol = quote.Symbol,
            Action = SignalAction.Sell,
            Quantity = qty,
            Price = QuotePriceGuard.SafeBid(quote),
            Reason = reason,
            StrategyDefinitionId = id,
            StockPoolId = poolId,
            CountsTowardOpenT = false
        };
    }

    /// <summary>买入预留 MinCashReserve，避免第一腿买回后无现金做第二腿。AvailableCash≤0 时不钳制（兼容未注入现金的调用）。</summary>
    private static int CapBuyQtyByCashReserve(
        int qty, QuoteTick quote, decimal availableCash, EffectiveStrategyParams eff)
    {
        if (qty < 100 || availableCash <= 0) return qty;
        var price = QuotePriceGuard.SafeAsk(quote);
        if (price <= 0) return qty;
        var reserve = Math.Max(0, eff.BaseBuildMinCashReserve);
        var budget = Math.Max(0, availableCash - reserve);
        if (budget < price * 100) return 0;
        var maxByCash = AlignLot((int)Math.Floor(budget / price));
        return Math.Min(qty, Math.Max(0, maxByCash));
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
