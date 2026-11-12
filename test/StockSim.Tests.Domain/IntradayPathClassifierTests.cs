using StockSim.Domain.Strategies;

namespace StockSim.Tests.Domain;

public class IntradayPathClassifierTests
{
    private static SessionSnapshot Snap(
        decimal open, decimal prev, decimal last, decimal high, decimal vwap)
    {
        var gap = prev > 0 ? (open - prev) / prev * 100m : 0m;
        var ret = open > 0 ? (last - open) / open * 100m : 0m;
        var pb = high > 0 ? (high - last) / high * 100m : 0m;
        return new SessionSnapshot
        {
            Symbol = "300502",
            Open = open,
            PrevClose = prev,
            DayHigh = high,
            DayLow = Math.Min(open, last),
            Vwap = vwap,
            Last = last,
            OpenGapPct = gap,
            RetFromOpenPct = ret,
            PullbackFromHighPct = pb,
            RealizedVolPct = 0.5m,
            VolumeRatio = 1m,
            AsOf = DateTime.Now
        };
    }

    [Fact]
    public void HighOpen_Hypothesis_Before_0935()
    {
        var snap = Snap(open: 213m, prev: 210m, last: 214m, high: 214m, vwap: 213m);
        var now = new DateTime(2026, 11, 12, 9, 32, 0);
        var (r, p, _) = IntradayPathClassifier.Classify(
            snap, now, IntradayPathRegime.Unknown, PathPhase.Hypothesis);
        Assert.Equal(IntradayPathRegime.HighOpenHighGo, r);
        Assert.Equal(PathPhase.Hypothesis, p);
    }

    [Fact]
    public void HighOpen_Pullback_Confirms_LowGo()
    {
        var snap = Snap(open: 213m, prev: 210m, last: 215m, high: 225m, vwap: 218m);
        var now = new DateTime(2026, 11, 12, 10, 0, 0);
        var (r, p, _) = IntradayPathClassifier.Classify(
            snap, now, IntradayPathRegime.HighOpenHighGo, PathPhase.Hypothesis);
        Assert.Equal(IntradayPathRegime.HighOpenLowGo, r);
        Assert.Equal(PathPhase.Confirmed, p);
    }

    [Fact]
    public void LowOpen_Rally_Confirms_HighGo()
    {
        var snap = Snap(open: 200m, prev: 210m, last: 208m, high: 209m, vwap: 204m);
        var confirmed = IntradayPathClassifier.InferConfirmed(
            snap.OpenGapPct, snap.RetFromOpenPct, snap.PullbackFromHighPct, aboveVwap: true);
        Assert.Equal(IntradayPathRegime.LowOpenHighGo, confirmed);
    }

    [Fact]
    public void LowOpen_Weak_Confirms_LowGo()
    {
        var snap = Snap(open: 200m, prev: 210m, last: 195m, high: 201m, vwap: 198m);
        var confirmed = IntradayPathClassifier.InferConfirmed(
            snap.OpenGapPct, snap.RetFromOpenPct, snap.PullbackFromHighPct, aboveVwap: false);
        Assert.Equal(IntradayPathRegime.LowOpenLowGo, confirmed);
    }

    [Fact]
    public void Playbook_HighOpenLowGo_Allows_BreakAvoid()
    {
        var pb = PathPlaybookResolver.Resolve(IntradayPathRegime.HighOpenLowGo);
        Assert.True(pb.AllowBreakAvoid);
        Assert.Equal("mean_reversion", pb.Style);
    }

    [Fact]
    public void IndexFilter_Weaken_When_Index_Strong()
    {
        var pb = PathPlaybookResolver.Resolve(IntradayPathRegime.HighOpenLowGo);
        var filtered = PathPlaybookResolver.ApplyIndexFilter(pb, IntradayPathRegime.HighOpenLowGo, 1.2m);
        Assert.Contains("Weaken", filtered.Label);
        Assert.True(filtered.SizeScale < pb.SizeScale);
    }
}
