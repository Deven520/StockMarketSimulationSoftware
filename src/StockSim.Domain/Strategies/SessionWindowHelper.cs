namespace StockSim.Domain.Strategies;

public static class SessionWindowHelper
{
    public static bool IsInAnyWindow(DateTime now, IReadOnlyList<string> windows)
    {
        var tod = now.TimeOfDay;
        foreach (var w in windows)
        {
            if (TryParseWindow(w, out var start, out var end) && tod >= start && tod <= end)
                return true;
        }

        return false;
    }

    public static bool IsAtOrAfter(DateTime now, string hhmm)
    {
        if (!TimeSpan.TryParse(hhmm, out var t)) return false;
        return now.TimeOfDay >= t;
    }

    public static bool TryParseWindow(string window, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;
        if (string.IsNullOrWhiteSpace(window)) return false;
        var parts = window.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        return TimeSpan.TryParse(parts[0], out start) && TimeSpan.TryParse(parts[1], out end);
    }
}
