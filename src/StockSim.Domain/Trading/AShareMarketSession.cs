namespace StockSim.Domain.Trading;

/// <summary>A股连续竞价时段判断（简化：工作日 9:30-11:30 / 13:00-15:00，不含节假日表）。</summary>
public static class AShareMarketSession
{
    public static bool IsTradingDay(DateTime localTime)
    {
        return localTime.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    /// <summary>下一交易日（简化：跳过周末；不含法定节假日表）。</summary>
    public static DateTime NextTradingDay(DateTime localTime)
    {
        var d = localTime.Date.AddDays(1);
        while (!IsTradingDay(d))
            d = d.AddDays(1);
        return d;
    }

    public static bool IsContinuousAuction(DateTime localTime)
    {
        if (!IsTradingDay(localTime)) return false;
        var t = localTime.TimeOfDay;
        var morning = t >= new TimeSpan(9, 30, 0) && t <= new TimeSpan(11, 30, 0);
        var afternoon = t >= new TimeSpan(13, 0, 0) && t < new TimeSpan(15, 0, 0);
        return morning || afternoon;
    }

    public static string Describe(DateTime localTime)
    {
        if (!IsTradingDay(localTime)) return "非交易日";
        var t = localTime.TimeOfDay;
        if (t < new TimeSpan(9, 15, 0)) return "未开市";
        if (t < new TimeSpan(9, 25, 0)) return "集合竞价";
        if (t < new TimeSpan(9, 30, 0)) return "开盘准备";
        if (t <= new TimeSpan(11, 30, 0)) return "上午连续竞价";
        if (t < new TimeSpan(13, 0, 0)) return "午间休市";
        if (t < new TimeSpan(15, 0, 0)) return "下午连续竞价";
        return "已收盘";
    }
}
