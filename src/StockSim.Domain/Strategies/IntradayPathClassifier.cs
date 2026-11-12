namespace StockSim.Domain.Strategies;

/// <summary>开盘假设 → 早盘确认 → 锁定的路径分型器。</summary>
public static class IntradayPathClassifier
{
    public const decimal GapThresholdPct = 0.5m;
    public const decimal RetConfirmPct = 0.8m;
    public const decimal PullbackConfirmPct = 1.0m;
    public const decimal ReverseUnlockPct = 2.0m;

    public static (IntradayPathRegime Regime, PathPhase Phase, string Reason) Classify(
        SessionSnapshot snap,
        DateTime now,
        IntradayPathRegime previous,
        PathPhase previousPhase,
        PathPolicy? pathPolicy = null)
    {
        var enable = pathPolicy?.Enable ?? true;
        if (!enable)
            return (IntradayPathRegime.Unknown, PathPhase.Hypothesis, "path.disable");

        var tod = now.TimeOfDay;
        var confirmUntil = ParseHm(pathPolicy?.ConfirmUntil) ?? new TimeSpan(10, 15, 0);
        var gap = snap.OpenGapPct;
        var ret = snap.RetFromOpenPct;
        var pullback = snap.PullbackFromHighPct;
        var aboveVwap = snap.Vwap > 0 && snap.Last >= snap.Vwap;

        // 已锁定：仅极端反转才重分
        if (previousPhase == PathPhase.Locked && previous != IntradayPathRegime.Unknown)
        {
            if (Math.Abs(ret) >= ReverseUnlockPct
                && IsOpposite(previous, gap, ret, pullback, aboveVwap))
            {
                var flipped = InferConfirmed(gap, ret, pullback, aboveVwap);
                return (flipped, PathPhase.Confirmed, $"反转重分 ret={ret:F2}%");
            }

            return (previous, PathPhase.Locked, "已锁定");
        }

        // 确认窗之后：固化
        if (tod >= confirmUntil && previous != IntradayPathRegime.Unknown)
            return (previous, PathPhase.Locked, "确认窗结束锁定");

        // 开盘假设
        if (tod < new TimeSpan(9, 35, 0) || previous == IntradayPathRegime.Unknown && tod < confirmUntil)
        {
            var hyp = gap >= GapThresholdPct
                ? IntradayPathRegime.HighOpenHighGo // 暂用 High*，确认时再拆
                : gap <= -GapThresholdPct
                    ? IntradayPathRegime.LowOpenLowGo
                    : IntradayPathRegime.Unknown;

            // 用更细的假设标签：高开先标 HighOpenHighGo，低开先标 LowOpenLowGo；确认阶段再改
            if (gap >= GapThresholdPct)
                hyp = IntradayPathRegime.HighOpenHighGo;
            else if (gap <= -GapThresholdPct)
                hyp = IntradayPathRegime.LowOpenLowGo;

            if (tod < new TimeSpan(9, 45, 0))
                return (hyp, PathPhase.Hypothesis, $"开盘假设 gap={gap:F2}%");
        }

        // 确认阶段 09:45–confirmUntil
        if (tod >= new TimeSpan(9, 45, 0) && tod < confirmUntil)
        {
            var confirmed = InferConfirmed(gap, ret, pullback, aboveVwap);
            return (confirmed, PathPhase.Confirmed, $"早盘确认 ret={ret:F2}% pb={pullback:F2}%");
        }

        if (previous != IntradayPathRegime.Unknown)
            return (previous, previousPhase == PathPhase.Hypothesis ? PathPhase.Confirmed : previousPhase, "沿用");

        return (InferConfirmed(gap, ret, pullback, aboveVwap), PathPhase.Confirmed, "即时确认");
    }

    public static IntradayPathRegime InferConfirmed(
        decimal gap,
        decimal retFromOpen,
        decimal pullbackFromHigh,
        bool aboveVwap)
    {
        var highOpen = gap >= GapThresholdPct;
        var lowOpen = gap <= -GapThresholdPct;

        if (highOpen)
        {
            if (pullbackFromHigh >= PullbackConfirmPct || (!aboveVwap && retFromOpen < 0.3m))
                return IntradayPathRegime.HighOpenLowGo;
            if (retFromOpen >= RetConfirmPct)
                return IntradayPathRegime.HighOpenHighGo;
            // 高开但尚未确认方向：偏 HighOpenLowGo 以便允许正 T（更贴做 T 目标）
            return pullbackFromHigh >= 0.5m ? IntradayPathRegime.HighOpenLowGo : IntradayPathRegime.HighOpenHighGo;
        }

        if (lowOpen)
        {
            if (retFromOpen >= RetConfirmPct && aboveVwap)
                return IntradayPathRegime.LowOpenHighGo;
            if (retFromOpen <= -RetConfirmPct || !aboveVwap)
                return IntradayPathRegime.LowOpenLowGo;
            return retFromOpen > 0 ? IntradayPathRegime.LowOpenHighGo : IntradayPathRegime.LowOpenLowGo;
        }

        // 平开：用开后收益
        if (retFromOpen >= RetConfirmPct) return IntradayPathRegime.LowOpenHighGo;
        if (retFromOpen <= -RetConfirmPct || pullbackFromHigh >= PullbackConfirmPct)
            return IntradayPathRegime.HighOpenLowGo;
        return IntradayPathRegime.Unknown;
    }

    private static bool IsOpposite(
        IntradayPathRegime previous,
        decimal gap,
        decimal ret,
        decimal pullback,
        bool aboveVwap)
    {
        var now = InferConfirmed(gap, ret, pullback, aboveVwap);
        return now != previous && now != IntradayPathRegime.Unknown;
    }

    private static TimeSpan? ParseHm(string? hm)
    {
        if (string.IsNullOrWhiteSpace(hm)) return null;
        var parts = hm.Split(':');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        return new TimeSpan(h, m, 0);
    }
}
