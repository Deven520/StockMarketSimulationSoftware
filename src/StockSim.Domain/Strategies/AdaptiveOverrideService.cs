namespace StockSim.Domain.Strategies;

/// <summary>L2：根据波动/量比/连亏/路径剧本在 Policy 边界内生成 RuntimeOverrides。</summary>
public sealed class AdaptiveOverrideService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, DateTime> _lastAdjust = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeOverrides Adjust(
        StrategyParameters policy,
        SymbolRuntimeState runtime,
        SessionSnapshot snap,
        DateTime now,
        PathPlaybookResolver.Playbook? playbook = null)
    {
        var existing = runtime.Overrides;
        if (_lastAdjust.TryGetValue(runtime.Symbol, out var last)
            && now - last < RefreshInterval
            && existing.AsOf.Date == now.Date
            && playbook is null)
        {
            return existing;
        }

        _lastAdjust[runtime.Symbol] = now;
        _ = policy.ToEffective();
        var expectedVol = 0.6m;
        var volScale = snap.RealizedVolPct <= 0
            ? 1m
            : Math.Clamp(snap.RealizedVolPct / expectedVol, 0.7m, 1.6m);

        var buyBase = policy.Triggers.BuyDeviationPct.Base;
        var sellBase = policy.Triggers.SellDeviationPct.Base;
        var buyAdj = buyBase * volScale;
        var sellAdj = sellBase * volScale;
        var sizeAdj = policy.Sizing.PositionPctPerTrade.Base / volScale;

        var aggression = Math.Clamp(policy.Aggression, 0.1m, 1m);
        sizeAdj *= (0.6m + 0.8m * aggression);

        var paused = false;
        string? pauseReason = null;
        if (runtime.ConsecutiveLosses >= policy.Risk.SymbolPauseOnConsecutiveLosses
            && policy.Risk.SymbolPauseOnConsecutiveLosses > 0)
        {
            paused = true;
            pauseReason = $"连亏{runtime.ConsecutiveLosses}次";
        }
        else if (snap.VolumeRatio < policy.Factors.MinVolumeRatio * 0.5m && policy.Factors.UseVolumeConfirm)
        {
            sizeAdj *= 0.5m;
        }

        var style = policy.Style;
        var allowBreak = false;
        decimal? breakSigma = policy.Path.BreakAvoidSigma;
        decimal? pullback = policy.Path.PullbackSellPct;
        var blockNewT = false;
        string? pathLabel = null;

        if (playbook is not null)
        {
            style = playbook.Style;
            allowBreak = playbook.AllowBreakAvoid;
            breakSigma = playbook.BreakAvoidSigma;
            pullback = playbook.PullbackSellPct;
            blockNewT = playbook.BlockNewT;
            pathLabel = playbook.Label;
            buyAdj *= playbook.BuyThresholdScale;
            sellAdj *= playbook.SellThresholdScale;
            sizeAdj *= playbook.SizeScale;
        }

        // Locked + HighGo：再抬卖阈，减少早盘偏离单批清仓
        if (runtime.PathPhase == PathPhase.Locked
            && pathLabel is not null
            && pathLabel.Contains("HighGo", StringComparison.OrdinalIgnoreCase))
        {
            sellAdj *= 1.08m;
        }

        if (string.Equals(style, "defensive", StringComparison.OrdinalIgnoreCase)
            || string.Equals(policy.Style, "defensive", StringComparison.OrdinalIgnoreCase))
        {
            buyAdj *= 1.15m;
            sellAdj *= 1.15m;
            sizeAdj *= 0.75m;
        }

        // 预测低显著失效（暴跌日）：买偏离更负、softPrefer 更接近放行，卖侧略抬阈值
        var softShrink = PriceForecastMetrics.SoftPreferShrinkScale;
        var lastPx = snap.Last > 0 ? snap.Last : snap.DayLow;
        var recentAcc = policy.Forecast?.RecentCombinedAccuracyPct;
        if (recentAcc is < PriceForecastMetrics.LowAccuracyDeweightThreshold)
            softShrink = Math.Max(softShrink, 0.8m); // 低准日少缩仓，避免锚在错误区间
        else if (recentAcc is < 55m)
            softShrink = Math.Max(softShrink, 0.7m);
        if (PriceForecastMetrics.IsForecastLowBroken(policy.Forecast, lastPx))
        {
            if (buyAdj < 0) buyAdj *= 1.15m;
            sellAdj *= 1.05m;
            softShrink = PriceForecastMetrics.SoftPreferShrinkScaleWhenForecastBroken;
            sizeAdj *= 0.9m;
            if (pathLabel is not null && !pathLabel.Contains("ForecastBreak", StringComparison.Ordinal))
                pathLabel += "+ForecastBreak";
        }

        // 当日大跌：略降仓位；大涨/HighGo：缩 softBand、允许近预测高仍低吸
        var skipNearHighAbandon = false;
        if (snap.PrevClose > 0 && lastPx > 0)
        {
            var dayRet = (lastPx - snap.PrevClose) / snap.PrevClose * 100m;
            if (dayRet <= -5m)
                sizeAdj *= 0.85m;
            if (dayRet >= 7m
                || (pathLabel?.Contains("HighOpenHighGo", StringComparison.OrdinalIgnoreCase) ?? false)
                || (pathLabel?.Contains("HighGo", StringComparison.OrdinalIgnoreCase) ?? false)
                   && dayRet >= 4m)
            {
                softShrink = Math.Min(softShrink, 0.35m);
                skipNearHighAbandon = true;
            }
        }

        if (runtime.ForceFlatExcessReduced)
            blockNewT = true;

        // HighGo Locked：剧本不应继续 BlockNewT（反弹解锁后的防守残留）
        if (runtime.PathPhase == PathPhase.Locked
            && pathLabel is not null
            && pathLabel.Contains("HighGo", StringComparison.OrdinalIgnoreCase)
            && !runtime.ForceFlatExcessReduced
            && playbook is { BlockNewT: false })
        {
            blockNewT = false;
        }

        var overrides = new RuntimeOverrides
        {
            BuyDeviationPct = buyAdj,
            SellDeviationPct = sellAdj,
            PositionPctPerTrade = sizeAdj,
            Paused = paused,
            PauseReason = pauseReason,
            AsOf = now,
            StyleHint = style,
            AllowBreakAvoid = allowBreak,
            BreakAvoidSigma = breakSigma,
            PullbackSellPct = pullback,
            BlockNewT = blockNewT,
            SoftBandShrinkScale = softShrink,
            PathLabel = pathLabel,
            SkipNearPredictedHighAbandon = skipNearHighAbandon
        };

        overrides.BuyDeviationPct = policy.Triggers.BuyDeviationPct.Resolve(overrides.BuyDeviationPct);
        overrides.SellDeviationPct = policy.Triggers.SellDeviationPct.Resolve(overrides.SellDeviationPct);
        overrides.PositionPctPerTrade = policy.Sizing.PositionPctPerTrade.Resolve(overrides.PositionPctPerTrade);

        runtime.Overrides = overrides;
        runtime.UpdatedAt = DateTime.UtcNow;
        return overrides;
    }
}
