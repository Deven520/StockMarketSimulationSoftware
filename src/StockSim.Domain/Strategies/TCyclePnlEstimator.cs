using StockSim.Domain.Enums;

namespace StockSim.Domain.Strategies;

/// <summary>
/// 正 T 配对盈亏：按时间序 FIFO 将卖腿与后续买腿配对（与持仓成本口径解耦）。
/// </summary>
public static class TCyclePnlEstimator
{
    public readonly record struct Leg(OrderSide Side, decimal Price, int Quantity, decimal Fee);

    /// <summary>
    /// 配对毛利、已配对腿费用、已完整关闭的卖腿次数（完整往返）。
    /// </summary>
    public static (decimal CycleGross, decimal FeesOnMatched, int CompletedRoundTrips) Analyze(
        IReadOnlyList<Leg> trades)
    {
        if (trades is null || trades.Count == 0)
            return (0m, 0m, 0);

        // (price, remainQty, feePerShare)
        var openSells = new List<(decimal Price, int Qty, decimal FeePerShare)>();
        decimal cycle = 0m;
        decimal fees = 0m;
        var completed = 0;

        foreach (var t in trades)
        {
            if (t.Quantity <= 0) continue;

            if (t.Side == OrderSide.Sell)
            {
                var feePer = t.Fee / t.Quantity;
                openSells.Add((t.Price, t.Quantity, feePer));
                continue;
            }

            if (t.Side != OrderSide.Buy) continue;

            var buyLeft = t.Quantity;
            var buyFeePer = t.Fee / t.Quantity;
            for (var i = 0; i < openSells.Count && buyLeft > 0; i++)
            {
                var s = openSells[i];
                if (s.Qty <= 0) continue;

                var matched = Math.Min(buyLeft, s.Qty);
                cycle += (s.Price - t.Price) * matched;
                fees += (s.FeePerShare + buyFeePer) * matched;

                var remain = s.Qty - matched;
                openSells[i] = (s.Price, remain, s.FeePerShare);
                buyLeft -= matched;

                if (s.Qty > 0 && remain == 0)
                    completed++;
            }
        }

        return (Math.Round(cycle, 2, MidpointRounding.AwayFromZero),
            Math.Round(fees, 2, MidpointRounding.AwayFromZero),
            completed);
    }

    /// <summary>配对毛利 − 已配对腿费用。</summary>
    public static decimal CycleNet(IReadOnlyList<Leg> trades)
    {
        var (gross, fees, _) = Analyze(trades);
        return Math.Round(gross - fees, 2, MidpointRounding.AwayFromZero);
    }

    public static int CompletedRoundTrips(IReadOnlyList<Leg> trades) => Analyze(trades).CompletedRoundTrips;
}
