using System.Text.Json;
using StockSim.Application.Dtos;
using StockSim.Domain.Entities;

namespace StockSim.Application.Services;

/// <summary>从 AI items JSON 解析个股字段（含选取逻辑）。</summary>
public static class AiSuggestionFactory
{
    public static AiSuggestion FromItem(Guid jobId, JsonElement item)
    {
        var reason = FirstString(item, "reason", "why", "changeNote");
        var logic = FirstString(item, "logic", "selectionLogic", "thesis", "rationale", "pickLogic")
                    ?? reason;
        return new AiSuggestion
        {
            AiJobId = jobId,
            PayloadJson = item.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? item.GetRawText()
                : "{}",
            Symbol = FirstString(item, "symbol", "code") ?? FirstNumberAsString(item, "symbol", "code"),
            Name = FirstString(item, "name", "stockName"),
            Reason = reason,
            SelectionLogic = logic,
            Risk = FirstString(item, "risk", "risks")
        };
    }

    public static AiSuggestionDto ToDto(AiSuggestion s) => new(
        s.Id,
        s.AiJobId,
        s.Symbol,
        s.Name,
        s.Reason,
        s.Risk,
        s.PayloadJson,
        s.IsAccepted,
        s.ReviewedAt,
        s.SelectionLogic);

    private static string? FirstString(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var p)) continue;
            if (p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            else if (p.ValueKind == JsonValueKind.Array)
            {
                var parts = p.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                if (parts.Count > 0) return string.Join("；", parts!);
            }
        }

        return null;
    }

    private static string? FirstNumberAsString(JsonElement item, params string[] names)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number)
                return p.ToString().PadLeft(6, '0');
        }

        return null;
    }
}
