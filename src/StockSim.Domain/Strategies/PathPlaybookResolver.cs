namespace StockSim.Domain.Strategies;

/// <summary>路径剧本 → 有效风格与触发偏好。</summary>
public static class PathPlaybookResolver
{
    public sealed class Playbook
    {
        public string Style { get; init; } = "mean_reversion";
        public bool AllowBreakAvoid { get; init; }
        public decimal BreakAvoidSigma { get; init; } = 1.5m;
        public decimal PullbackSellPct { get; init; }
        public decimal SellThresholdScale { get; init; } = 1m;
        public decimal BuyThresholdScale { get; init; } = 1m;
        public decimal SizeScale { get; init; } = 1m;
        public bool BlockNewT { get; init; }
        public string Label { get; init; } = "Unknown";
    }

    public static Playbook Resolve(IntradayPathRegime regime, PathPolicy? path = null)
    {
        var breakSigma = path?.BreakAvoidSigma > 0 ? path.BreakAvoidSigma : 1.5m;
        var pullback = path?.PullbackSellPct > 0 ? path.PullbackSellPct : 0.8m;

        return regime switch
        {
            IntradayPathRegime.HighOpenLowGo => new Playbook
            {
                Style = "mean_reversion",
                AllowBreakAvoid = true,
                BreakAvoidSigma = breakSigma,
                PullbackSellPct = pullback,
                SellThresholdScale = 0.9m,
                SizeScale = 1m,
                Label = "HighOpenLowGo"
            },
            IntradayPathRegime.HighOpenHighGo => new Playbook
            {
                Style = "momentum_t",
                AllowBreakAvoid = false,
                PullbackSellPct = pullback * 1.25m,
                SellThresholdScale = 1.35m,
                BuyThresholdScale = 0.9m,
                SizeScale = 0.85m,
                Label = "HighOpenHighGo"
            },
            // 倒T：买入偏紧防阴跌加仓；卖出略松 + 启用回撤卖，避免 BoughtLow 空等高抛
            IntradayPathRegime.LowOpenHighGo => new Playbook
            {
                Style = "momentum_t",
                AllowBreakAvoid = false,
                PullbackSellPct = Math.Max(0.55m, pullback * 0.75m),
                SellThresholdScale = 0.95m,
                BuyThresholdScale = 0.9m,
                SizeScale = 1m,
                Label = "LowOpenHighGo"
            },
            IntradayPathRegime.LowOpenLowGo => new Playbook
            {
                Style = "defensive",
                AllowBreakAvoid = false,
                SellThresholdScale = 1.25m,
                BuyThresholdScale = 1.25m,
                SizeScale = 0.5m,
                BlockNewT = true,
                Label = "LowOpenLowGo"
            },
            _ => new Playbook
            {
                Style = "mean_reversion",
                Label = "Unknown"
            }
        };
    }

    /// <summary>结合创业板指涨跌对剧本做确认/削弱/禁止新开 T。</summary>
    public static Playbook ApplyIndexFilter(Playbook playbook, IntradayPathRegime regime, decimal? indexChangePct)
    {
        if (indexChangePct is null) return playbook;

        var idx = indexChangePct.Value;
        var filter = PathFilterAction.None;

        // 高开低走 + 指数转弱 → 确认正 T
        if (regime == IntradayPathRegime.HighOpenLowGo && idx <= -0.3m)
            filter = PathFilterAction.Confirm;
        // 想高抛但指数仍强
        else if ((regime is IntradayPathRegime.HighOpenLowGo or IntradayPathRegime.HighOpenHighGo) && idx >= 0.8m)
            filter = PathFilterAction.Weaken;
        // 倒 T 但指数仍弱
        else if (regime == IntradayPathRegime.LowOpenHighGo && idx <= -0.5m)
            filter = PathFilterAction.BlockNewT;
        else if (regime == IntradayPathRegime.LowOpenLowGo && idx <= -0.8m)
            filter = PathFilterAction.BlockNewT;

        return filter switch
        {
            PathFilterAction.Confirm => new Playbook
            {
                Style = playbook.Style,
                AllowBreakAvoid = playbook.AllowBreakAvoid,
                BreakAvoidSigma = playbook.BreakAvoidSigma,
                PullbackSellPct = playbook.PullbackSellPct,
                SellThresholdScale = playbook.SellThresholdScale,
                BuyThresholdScale = playbook.BuyThresholdScale,
                SizeScale = Math.Min(1.25m, playbook.SizeScale * 1.1m),
                BlockNewT = playbook.BlockNewT,
                Label = playbook.Label + "+Confirm"
            },
            PathFilterAction.Weaken => new Playbook
            {
                Style = playbook.Style,
                AllowBreakAvoid = playbook.AllowBreakAvoid,
                BreakAvoidSigma = playbook.BreakAvoidSigma,
                PullbackSellPct = playbook.PullbackSellPct,
                SellThresholdScale = playbook.SellThresholdScale * 1.15m,
                BuyThresholdScale = playbook.BuyThresholdScale,
                SizeScale = playbook.SizeScale * 0.7m,
                BlockNewT = playbook.BlockNewT,
                Label = playbook.Label + "+Weaken"
            },
            PathFilterAction.BlockNewT => new Playbook
            {
                Style = playbook.Style,
                AllowBreakAvoid = false,
                BreakAvoidSigma = playbook.BreakAvoidSigma,
                PullbackSellPct = playbook.PullbackSellPct,
                SellThresholdScale = playbook.SellThresholdScale,
                BuyThresholdScale = playbook.BuyThresholdScale,
                SizeScale = playbook.SizeScale * 0.5m,
                BlockNewT = true,
                Label = playbook.Label + "+BlockNewT"
            },
            _ => playbook
        };
    }
}
