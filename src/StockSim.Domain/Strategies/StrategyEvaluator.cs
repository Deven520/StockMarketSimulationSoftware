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

        var forceFlatWindow = SessionWindowHelper.IsAtOrAfter(ctx.Now, eff.ForceFlatBefore);
        if (forceFlatWindow)
            runtime.State = TState.ForceFlat;
        else if (SessionWindowHelper.IsInAnyWindow(ctx.Now, eff.AvoidWindows)
                 && runtime.State is not (TState.SoldHigh or TState.BoughtLow or TState.ForceFlat))
            runtime.State = TState.SessionAvoid;
        else if ((runtime.State is TState.SessionAvoid or TState.BuildingBase)
                 && !SessionWindowHelper.IsInAnyWindow(ctx.Now, eff.AvoidWindows))
            runtime.State = TState.Idle;

        // DayRealizedPnL 由引擎写入「T 配对净利(毛利−费)」；勿用持仓成本口径，避免假停机
        var dayLossHit = false;
        decimal dayLossPct = 0m;
        if (eff.DayLossStopPct < 0 && ctx.AnchorPrice > 0)
        {
            dayLossPct = runtime.DayRealizedPnL
                / (ctx.AnchorPrice * Math.Max(position?.BaseQuantity ?? 100, 100)) * 100m;
            dayLossHit = dayLossPct <= eff.DayLossStopPct;
        }

        var consecutiveLossHit = runtime.ConsecutiveLosses >= eff.SymbolPauseOnConsecutiveLosses
                                 && eff.SymbolPauseOnConsecutiveLosses > 0;
        var riskHaltHit = dayLossHit || consecutiveLossHit || runtime.State == TState.RiskHalt;

        if (riskHaltHit)
        {
            // 不覆盖 ForceFlat/SoldHigh：仍允许关 OpenT / 减超配；只禁止新开
            if (runtime.State is not (TState.ForceFlat or TState.SoldHigh))
                runtime.State = TState.RiskHalt;
            runtime.Overrides.BlockNewT = true;
        }

        var hasOpenTLeg = runtime.OpenTQty >= 100 || HasSoldHighDeficit(position);
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
        // 底仓过小易一轮卖穿；关 OpenT / SoldHigh 仍允许
        if (baseQty < MinBaseQtyForNewT && runtime.State != TState.SoldHigh && !hasOpenTLeg
            && runtime.State != TState.BoughtLow)
            return None(quote.Symbol, id, poolId, $"{pathTag}底仓不足{MinBaseQtyForNewT}股，暂不做T");

        var tradeQty = AlignLot((int)Math.Floor(Math.Max(baseQty, 100) * eff.PositionPctPerTrade));
        if (tradeQty < 100) tradeQty = 100;

        var volOk = !eff.UseVolumeConfirm || ctx.VolumeRatio >= eff.MinVolumeRatio;
        var slipBps = ctx.Policy.Risk.MaxSlippageBps > 0 ? ctx.Policy.Risk.MaxSlippageBps : 20m;

        // ForceFlat：优先减超配至 Base；OpenT 买回须不追高；禁止卖后再买回空转
        if (runtime.State == TState.ForceFlat || (forceFlatWindow && hasOpenTLeg))
        {
            runtime.State = TState.ForceFlat;
            var flat = EvaluateForceFlat(
                ctx, quote, position, runtime, tradeQty, pathTag, id, poolId, slipBps,
                relaxClose: dayLossHit);
            if (flat.Action != SignalAction.None || !riskHaltHit || hasOpenTLeg)
                return flat;
        }

        // 残腿优先：Idle 却仍有 OpenT → 归一为 SoldHigh，禁止新开卖腿
        if (hasOpenTLeg && runtime.State == TState.Idle && !forceFlatWindow)
            runtime.State = TState.SoldHigh;

        // RiskHalt：仅允许关 OpenT / SoldHigh 低吸；日亏时放宽买回带，禁止新开
        if (riskHaltHit && runtime.State == TState.RiskHalt)
        {
            if (hasOpenTLeg)
            {
                var close = TryCloseOpenTLeg(
                    ctx, quote, position, runtime, tradeQty, pathTag, id, poolId, slipBps,
                    reasonPrefix: dayLossHit
                        ? $"日亏 {dayLossPct:F2}% 停机后仅关OpenT"
                        : "风控停机后仅关OpenT",
                    relaxClose: dayLossHit);
                if (close.Action == SignalAction.Buy)
                    return close;
                return None(quote.Symbol, id, poolId,
                    $"{pathTag}{(dayLossHit ? $"日亏 {dayLossPct:F2}% 触发停机" : "风控冻结中")}；OpenT未平且买回价不满足");
            }

            return None(quote.Symbol, id, poolId,
                dayLossHit
                    ? $"{pathTag}日亏 {dayLossPct:F2}% 触发停机"
                    : consecutiveLossHit
                        ? $"{pathTag}连亏 {runtime.ConsecutiveLosses} 次暂停"
                        : $"{pathTag}风控冻结中");
        }

        if ((eff.BlockNewT || runtime.ForceFlatExcessReduced || riskHaltHit)
            && runtime.State == TState.Idle
            && runtime.OpenTQty <= 0
            && !HasSoldHighDeficit(position))
        {
            return None(quote.Symbol, id, poolId,
                runtime.ForceFlatExcessReduced
                    ? $"{pathTag}ForceFlat后禁止新开T"
                    : riskHaltHit
                        ? $"{pathTag}日亏停机后禁止新开T"
                        : $"{pathTag}BlockNewT 观望");
        }

        // SoldHigh → 低吸买回（不追高；强势日不因近预测高放弃）
        if (runtime.State == TState.SoldHigh)
        {
            var ask = QuotePriceGuard.SafeAsk(quote);
            if (runtime.LastSellPrice is > 0 && ask > runtime.LastSellPrice.Value
                && !dayLossHit)
                return None(quote.Symbol, id, poolId, $"{pathTag}SoldHigh 现价高于卖出价，不追买");

            if (!ShouldSkipNearPredictedHighAbandon(ctx, eff)
                && IsNearPredictedHigh(ctx.Policy.Forecast, ask))
                return None(quote.Symbol, id, poolId, $"{pathTag}SoldHigh 逼近预测高，放弃低吸等待");

            var closeOk = CanCloseOpenTAtPrice(
                ask, runtime.LastSellPrice, slipBps, relaxForDayLoss: dayLossHit);
            if (deviation <= eff.BuyDeviationPct && volOk && closeOk)
            {
                var (ok, qty, softNote) = ApplySoftBand(ctx, tradeQty, forSell: false);
                if (!ok)
                    return None(quote.Symbol, id, poolId, $"{pathTag}{softNote}");

                qty = CapBuyToOpenT(qty, runtime);
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

        // BoughtLow → 平掉倒T多头腿（不计 OpenT；不得卖穿底仓）
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
                var sell = TrySellHigh(
                    quote, position, qty, eff, id, poolId,
                    $"{pathTag}倒T平仓 {why}{softNote}",
                    countsTowardOpenT: false);
                if (sell.Action == SignalAction.Sell)
                {
                    runtime.State = TState.Idle;
                    // 倒T平仓不记正T卖出价，避免尾盘误买回
                    return sell;
                }

                // 透出护底仓等明确拒绝，避免吞成「等待高抛」
                if (sell.Reason.Contains("保留底仓", StringComparison.Ordinal))
                    return sell;
            }

            return None(quote.Symbol, id, poolId, $"{pathTag}BoughtLow 等待高抛");
        }

        // Idle：先高抛 / 冲高回撤 / 补回 / 动量低吸
        if (runtime.State == TState.Idle)
        {
            // 日亏已超阈：禁止新开第一腿，避免「先卖再停机」残腿
            if (riskHaltHit)
                return None(quote.Symbol, id, poolId, $"{pathTag}日亏停机后禁止新开T");

            // 残腿未清：Idle 不得新开卖；仅允许补回 OpenT/底仓缺口
            if (hasOpenTLeg)
            {
                var close = TryCloseOpenTLeg(
                    ctx, quote, position, runtime, tradeQty, pathTag, id, poolId, slipBps,
                    reasonPrefix: "残腿优先买回",
                    relaxClose: false);
                if (close.Action == SignalAction.Buy)
                    return close;
                return None(quote.Symbol, id, poolId, $"{pathTag}残腿未平，禁止新开卖腿");
            }

            var pathReady = IsPathReadyForNewT(runtime, eff);
            var isMomentum = string.Equals(eff.Style, "momentum_t", StringComparison.OrdinalIgnoreCase);
            var isHighGo = isMomentum
                || (eff.PathLabel?.Contains("HighGo", StringComparison.OrdinalIgnoreCase) ?? false);
            var pullbackHit = eff.PullbackSellPct > 0
                && ctx.PullbackFromHighPct >= eff.PullbackSellPct
                && deviation >= Math.Min(0.2m, eff.SellDeviationPct * 0.5m)
                && volOk;

            // HighGo/动量：纯偏离高抛须近预测高或软抛区，优先回撤卖，减少早盘卖飞
            var sellDevHit = volOk && deviation >= (isHighGo
                ? eff.SellDeviationPct * 1.2m
                : eff.SellDeviationPct);
            var momentumNearHigh = !isHighGo
                || pullbackHit
                || IsNearPredictedHigh(ctx.Policy.Forecast, quote.LastPrice)
                || IsInSellSoftPreferZone(ctx);

            if ((sellDevHit && momentumNearHigh) || pullbackHit)
            {
                if (!pathReady)
                    return None(quote.Symbol, id, poolId, $"{pathTag}路径未确认，跳过新开卖腿");

                if (!HasMinRoundTripEdge(quote.LastPrice, tradeQty, eff))
                    return None(quote.Symbol, id, poolId, $"{pathTag}费后净价差不足，跳过新开卖腿");

                // 新开正T卖腿：PreferShrink 直接跳过，避免缩仓残腿过夜
                var (ok, qty, softNote) = ApplySoftBand(
                    ctx, tradeQty, forSell: true, blockOnPreferShrink: true);
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

            // 仅补 SoldHigh/OpenT 缺口；无缺口时不做「裸补底仓」烧手续费（含 BlockNewT）
            if (deviation <= eff.BuyDeviationPct && volOk && HasSoldHighDeficit(position))
            {
                var (ok, qty, softNote) = ApplySoftBand(ctx, tradeQty, forSell: false);
                if (!ok)
                    return None(quote.Symbol, id, poolId, $"{pathTag}{softNote}");

                qty = CapBuyToOpenT(qty, runtime);
                var tag = eff.BlockNewT || runtime.ForceFlatExcessReduced
                    ? "BlockNewT下仅补SoldHigh缺口"
                    : "补回底仓";
                var buy = TryBuyBack(quote, position, qty, eff, id, poolId,
                    $"{pathTag}{tag} 偏离 {deviation:F2}%{softNote}");
                if (buy.Action == SignalAction.Buy)
                {
                    runtime.State = TState.Idle;
                    runtime.LastBuyPrice = buy.Price;
                    return buy;
                }
            }

            if (isMomentum
                && deviation <= eff.BuyDeviationPct && volOk
                && (position?.Quantity ?? 0) >= 100
                && !eff.BlockNewT
                && !runtime.ForceFlatExcessReduced)
            {
                if (!pathReady)
                    return None(quote.Symbol, id, poolId, $"{pathTag}路径未确认，跳过动量低吸");

                if (!HasMinRoundTripEdge(quote.LastPrice, tradeQty, eff))
                    return None(quote.Symbol, id, poolId, $"{pathTag}费后净价差不足，跳过动量低吸");

                var (ok, qty, softNote) = ApplySoftBand(
                    ctx, tradeQty, forSell: false, blockOnPreferShrink: true);
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

    /// <summary>
    /// ForceFlat：优先一次性减超配至 Base；OpenT 买回须不高于卖出价（扣费差）；否则留待次日。
    /// </summary>
    private static TradeSignal EvaluateForceFlat(
        EvaluateContext ctx,
        QuoteTick quote,
        Position? position,
        SymbolRuntimeState runtime,
        int tradeQty,
        string pathTag,
        Guid id,
        Guid? poolId,
        decimal slipBps,
        bool relaxClose = false)
    {
        var qtyNow = position?.Quantity ?? 0;
        var baseNow = position?.BaseQuantity ?? 0;
        var excess = qtyNow - baseNow;

        // 1) 优先减超配到 Base（不计 OpenT）；减完禁新开，禁止随后空转买回
        if (excess >= 100)
        {
            var sellQty = AlignLot(excess);
            var sell = TrySellExcess(quote, position, sellQty, id, poolId,
                $"{pathTag}收盘前减仓至基准仓");
            if (sell.Action == SignalAction.Sell)
            {
                runtime.ForceFlatExcessReduced = true;
                runtime.Overrides.BlockNewT = true;
                return sell;
            }
        }

        if (runtime.ForceFlatExcessReduced
            && runtime.OpenTQty < 100
            && !HasSoldHighDeficit(position)
            && excess < 100)
        {
            return None(quote.Symbol, id, poolId, $"{pathTag}ForceFlat 超配已减，禁止买回");
        }

        // 2) 关 OpenT：不追高；须满足扣费边距；价不满足则留待次日（日亏时略放宽）
        if (runtime.OpenTQty >= 100 || HasSoldHighDeficit(position))
        {
            return TryCloseOpenTLeg(
                ctx, quote, position, runtime, tradeQty, pathTag, id, poolId, slipBps,
                reasonPrefix: "收盘前买回",
                relaxClose: relaxClose);
        }

        return None(quote.Symbol, id, poolId, $"{pathTag}ForceFlat 无需动作");
    }

    private static TradeSignal TryCloseOpenTLeg(
        EvaluateContext ctx,
        QuoteTick quote,
        Position? position,
        SymbolRuntimeState runtime,
        int tradeQty,
        string pathTag,
        Guid id,
        Guid? poolId,
        decimal slipBps,
        string reasonPrefix,
        bool relaxClose = false)
    {
        var ask = QuotePriceGuard.SafeAsk(quote);
        if (runtime.LastSellPrice is > 0 && ask > runtime.LastSellPrice.Value && !relaxClose)
            return None(quote.Symbol, id, poolId,
                $"{pathTag}{reasonPrefix} 现价高于卖出价，留待次日");

        if (!CanCloseOpenTAtPrice(ask, runtime.LastSellPrice, slipBps, relaxForDayLoss: relaxClose))
            return None(quote.Symbol, id, poolId,
                $"{pathTag}{reasonPrefix} 买回价过高(>{runtime.LastSellPrice:F2}扣费差)，留待次日");

        if (!relaxClose
            && !ShouldSkipNearPredictedHighAbandon(ctx, ctx.Effective)
            && IsNearPredictedHigh(ctx.Policy.Forecast, ask)
            && runtime.LastSellPrice is > 0
            && ask > runtime.LastSellPrice.Value * 0.995m)
        {
            return None(quote.Symbol, id, poolId,
                $"{pathTag}{reasonPrefix} 逼近预测高且高于卖出价，留待次日");
        }

        var cap = runtime.OpenTQty >= 100 ? runtime.OpenTQty : tradeQty;
        var buyQty = CapBuyToOpenT(Math.Min(tradeQty, AlignLot(cap)), runtime);
        buyQty = CapBuyQtyByCashReserve(buyQty, quote, ctx.AvailableCash, ctx.Effective);
        var buy = TryBuyBack(quote, position, buyQty, ctx.Effective, id, poolId,
            $"{pathTag}{reasonPrefix} OpenT={runtime.OpenTQty}");
        if (buy.Action == SignalAction.Buy)
        {
            runtime.State = TState.Idle;
            runtime.LastBuyPrice = buy.Price;
        }

        return buy;
    }

    /// <summary>
    /// OpenT 买回：现价须 ≤ 卖出价×(1−滑点bps)；日亏停机放宽至卖出价+0.3%，避免死等惨案。
    /// </summary>
    private static bool CanCloseOpenTAtPrice(
        decimal ask,
        decimal? lastSell,
        decimal maxSlippageBps,
        bool relaxForDayLoss = false)
    {
        if (ask <= 0) return false;
        if (lastSell is null or <= 0) return true;
        if (relaxForDayLoss)
            return ask <= lastSell.Value * 1.003m;
        var bps = Math.Max(0m, maxSlippageBps);
        // 叠加最低回合边距：买回至少低于卖出价 MinRoundTrip 一半对应价差
        var maxAsk = lastSell.Value * (1m - bps / 10_000m);
        return ask <= maxAsk;
    }

    private static bool ShouldSkipNearPredictedHighAbandon(
        EvaluateContext ctx, EffectiveStrategyParams eff)
    {
        if (eff.SkipNearPredictedHighAbandon) return true;
        return IsStrongTrendDay(ctx, eff);
    }

    private static bool IsStrongTrendDay(EvaluateContext ctx, EffectiveStrategyParams eff)
    {
        var label = eff.PathLabel ?? "";
        if (label.Contains("HighGo", StringComparison.OrdinalIgnoreCase)) return true;
        if (ctx.Quote.ChangePercent >= 7m) return true;
        return false;
    }

    private static bool IsNearPredictedHigh(ForecastPolicy? forecast, decimal lastPrice)
    {
        if (forecast is null || lastPrice <= 0) return false;
        if (forecast.PredictedHigh <= 0) return false;
        if (PriceForecastMetrics.IsPlaceholderForecast(forecast)) return false;
        return lastPrice >= forecast.PredictedHigh * 0.99m;
    }

    private static (bool Ok, int Qty, string SoftNote) ApplySoftBand(
        EvaluateContext ctx, int tradeQty, bool forSell, bool blockOnPreferShrink = false)
    {
        var refPx = ctx.ReferencePrice > 0 ? ctx.ReferencePrice : ctx.AnchorPrice;
        var verdict = PriceForecastMetrics.EvaluateForecastSoftBand(
            ctx.Policy.Forecast,
            ctx.Quote.LastPrice,
            ctx.Now,
            forSell,
            PriceForecastMetrics.ForecastSoftBandRatio,
            out var msg,
            referencePrice: refPx,
            avgCombinedAccuracyPct: ctx.Policy.Forecast?.RecentCombinedAccuracyPct);

        if (verdict == SoftBandVerdict.Block)
            return (false, tradeQty, msg ?? "预测软边界拦截");

        if (verdict == SoftBandVerdict.PreferShrink)
        {
            if (blockOnPreferShrink)
            {
                return (false, tradeQty,
                    msg is null ? "未进预测软区，跳过新开腿" : $"{msg}；跳过新开腿");
            }

            var scale = ctx.Effective.SoftBandShrinkScale > 0
                ? ctx.Effective.SoftBandShrinkScale
                : PriceForecastMetrics.SoftPreferShrinkScale;
            var qty = AlignLot((int)Math.Floor(tradeQty * scale));
            if (qty < 100) qty = 100;
            return (true, qty, msg is null ? " [软边界缩仓]" : $" [{msg};缩仓]");
        }

        return (true, tradeQty, "");
    }

    /// <summary>Unknown / Hypothesis 阶段禁止新开第一腿，等路径确认。</summary>
    private static bool IsPathReadyForNewT(SymbolRuntimeState runtime, EffectiveStrategyParams eff)
    {
        if (runtime.PathRegime == IntradayPathRegime.Unknown)
            return false;
        if (runtime.PathPhase == PathPhase.Hypothesis)
            return false;
        var label = eff.PathLabel?.Trim() ?? "";
        if (label.Length == 0)
            return false;
        if (label.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
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
        // tradeQty 已由调用方按 OpenT 钳制；无底仓缺口时也不得放大到 PositionPct
        var qty = deficit >= 100 ? AlignLot(Math.Min(deficit, tradeQty)) : AlignLot(tradeQty);
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

    /// <summary>买回数量不得超过未平 OpenT 卖腿。</summary>
    private static int CapBuyToOpenT(int qty, SymbolRuntimeState runtime)
    {
        if (qty < 100) return qty;
        if (runtime.OpenTQty < 100) return qty;
        return AlignLot(Math.Min(qty, runtime.OpenTQty));
    }

    /// <summary>现价已进入预测高抛软区（EvaluateForecastSoftBand=Pass）。</summary>
    private static bool IsInSellSoftPreferZone(EvaluateContext ctx)
    {
        var refPx = ctx.ReferencePrice > 0 ? ctx.ReferencePrice : ctx.AnchorPrice;
        var verdict = PriceForecastMetrics.EvaluateForecastSoftBand(
            ctx.Policy.Forecast,
            ctx.Quote.LastPrice,
            ctx.Now,
            forSell: true,
            PriceForecastMetrics.ForecastSoftBandRatio,
            out _,
            referencePrice: refPx,
            avgCombinedAccuracyPct: ctx.Policy.Forecast?.RecentCombinedAccuracyPct);
        return verdict == SoftBandVerdict.Pass;
    }

    /// <summary>
    /// 开腿前经济性：毛捕获（偏离带×0.35）须 ≥ 约 2×双边费，且净 bps ≥ MinRoundTripEdgeBps。
    /// 专治「+25 价差被 40 手续费吃掉」类空转。
    /// </summary>
    private static bool HasMinRoundTripEdge(decimal price, int qty, EffectiveStrategyParams eff)
    {
        if (eff.MinRoundTripEdgeBps < 0) return true;
        if (price <= 0 || qty < 100) return false;

        var bandPct = Math.Abs(eff.SellDeviationPct) + Math.Abs(eff.BuyDeviationPct);
        var expectedCaptureBps = bandPct / 100m * 10_000m * 0.35m;
        var notional = price * qty;
        // A股往返粗估：佣金万三×2 + 印花卖万五 + 过户/滑点 ≈ 11bps，小额单抬高最低佣金影响
        var feeBps = notional < 20_000m ? 18m : 11m;
        if (expectedCaptureBps < feeBps * 2m)
            return false;
        var netBps = expectedCaptureBps - feeBps;
        var minNet = Math.Max(0m, eff.MinRoundTripEdgeBps);
        return netBps >= minNet;
    }

    private static TradeSignal TrySellHigh(
        QuoteTick quote,
        Position? position,
        int tradeQty,
        EffectiveStrategyParams eff,
        Guid id,
        Guid? poolId,
        string reason,
        bool countsTowardOpenT = true)
    {
        var available = position?.Quantity ?? 0;
        if (position is not null && position.SellableQuantity > 0)
            available = Math.Min(available, position.SellableQuantity);

        var qty = AlignLot(Math.Min(available, tradeQty));
        if (qty < 100)
            return None(quote.Symbol, id, poolId, "可卖数量不足1手");

        // 正T：至少留 KeepBaseLot（可暂低于 BaseQuantity）。
        // 倒T平仓：不得卖穿 BaseQuantity，避免把底仓当 OpenT 卖掉。
        var keep = Math.Max(100, eff.KeepBaseLot);
        if (!countsTowardOpenT && position?.BaseQuantity > 0)
            keep = Math.Max(keep, position.BaseQuantity);

        if (available <= keep)
            return None(quote.Symbol, id, poolId, "保留底仓后无可卖");

        var maxSell = AlignLot(available - keep);
        qty = AlignLot(Math.Min(qty, maxSell));
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
            CountsTowardOpenT = countsTowardOpenT
        };
    }

    /// <summary>新开 T 要求至少 2 手底仓，避免一轮卖空。</summary>
    private const int MinBaseQtyForNewT = 200;

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
