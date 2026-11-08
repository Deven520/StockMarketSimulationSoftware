namespace StockSim.Domain.Strategies;

/// <summary>L2：根据波动/量比/连亏在 Policy 边界内生成 RuntimeOverrides。</summary>
public sealed class AdaptiveOverrideService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, DateTime> _lastAdjust = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeOverrides Adjust(
        StrategyParameters policy,
        SymbolRuntimeState runtime,
        SessionSnapshot snap,
        DateTime now)
    {
        var existing = runtime.Overrides;
        if (_lastAdjust.TryGetValue(runtime.Symbol, out var last)
            && now - last < RefreshInterval
            && existing.AsOf.Date == now.Date)
        {
            return existing;
        }

        _lastAdjust[runtime.Symbol] = now;
        _ = policy.ToEffective(); // 确保 legacy 已迁移、区间已规范化
        var expectedVol = 0.6m; // 经验基准：短窗收益标准差约 0.6%
        var volScale = snap.RealizedVolPct <= 0
            ? 1m
            : Math.Clamp(snap.RealizedVolPct / expectedVol, 0.7m, 1.6m);

        var buyBase = policy.Triggers.BuyDeviationPct.Base;
        var sellBase = policy.Triggers.SellDeviationPct.Base;
        // 波动大 → 阈值放宽（买更负、卖更正），仓位缩小
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
            // 极缩量：不暂停整股，但仓位砍半（通过 size）
            sizeAdj *= 0.5m;
        }

        if (string.Equals(policy.Style, "defensive", StringComparison.OrdinalIgnoreCase))
        {
            buyAdj *= 1.15m;
            sellAdj *= 1.15m;
            sizeAdj *= 0.75m;
        }

        var overrides = new RuntimeOverrides
        {
            BuyDeviationPct = buyAdj,
            SellDeviationPct = sellAdj,
            PositionPctPerTrade = sizeAdj,
            Paused = paused,
            PauseReason = pauseReason,
            AsOf = now
        };

        // 用 Resolve 钳住，确保不越界
        overrides.BuyDeviationPct = policy.Triggers.BuyDeviationPct.Resolve(overrides.BuyDeviationPct);
        overrides.SellDeviationPct = policy.Triggers.SellDeviationPct.Resolve(overrides.SellDeviationPct);
        overrides.PositionPctPerTrade = policy.Sizing.PositionPctPerTrade.Resolve(overrides.PositionPctPerTrade);

        runtime.Overrides = overrides;
        runtime.UpdatedAt = DateTime.UtcNow;
        return overrides;
    }
}
