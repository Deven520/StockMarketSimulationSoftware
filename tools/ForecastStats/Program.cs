using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

var db = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("src/StockSim.Web/stocksim.db");
await using var c = new SqliteConnection($"Data Source={db}");
await c.OpenAsync();

await using var cmd = c.CreateCommand();
cmd.CommandText = """
SELECT v.Id, v.ParametersJson
FROM AppStockPoolItems i
JOIN AppStrategies s ON s.Id = i.BoundStrategyId
JOIN AppStrategyVersions v ON v.Id = s.CurrentVersionId
WHERE i.Symbol='300502'
""";
await using var r = await cmd.ExecuteReaderAsync();
if (!await r.ReadAsync())
{
    Console.WriteLine("No strategy");
    return;
}

var versionId = r.GetGuid(0);
var json = r.GetString(1);
r.Close();

var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
var fc = root["forecast"]?.AsObject();
var high = fc?["predictedHigh"]?.GetValue<decimal>() ?? 0;
var low = fc?["predictedLow"]?.GetValue<decimal>() ?? 0;
Console.WriteLine($"Before high={high} low={low}");
if (high <= 0 || high >= 80)
{
    Console.WriteLine("No fake forecast to clear");
    return;
}

root.Remove("forecast");
var notes = root["aiNotes"]?.GetValue<string>() ?? "";
root["aiNotes"] = notes + " | 已清除疑似模拟行情假预测(≈40)，待真实行情下重新AI刷新";
var updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

await using var upd = c.CreateCommand();
upd.CommandText = "UPDATE AppStrategyVersions SET ParametersJson=$j WHERE Id=$id";
upd.Parameters.AddWithValue("$j", updated);
upd.Parameters.AddWithValue("$id", versionId.ToString());
var n = await upd.ExecuteNonQueryAsync();
Console.WriteLine($"Cleared forecast from strategy version, rows={n}");
