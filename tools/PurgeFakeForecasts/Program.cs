using Microsoft.Data.Sqlite;

// 删除因模拟行情污染的离谱预测（含已 Evaluated 且虚高准确率）。
// 用法: dotnet run --project tools/PurgeFakeForecasts -- [dbPath] [--dry-run]
var dryRun = args.Any(a => a is "--dry-run" or "-n");
var dbArg = args.FirstOrDefault(a => !a.StartsWith('-'));
var db = Path.GetFullPath(dbArg
    ?? Path.Combine("src", "StockSim.Web", "stocksim.db"));
if (!File.Exists(db))
{
    Console.Error.WriteLine("DB not found: " + db);
    return 1;
}

Console.WriteLine($"DB={db} dryRun={dryRun}");

await using var c = new SqliteConnection("Data Source=" + db);
await c.OpenAsync();

var refPx = await LoadReferencePricesAsync(c);
Console.WriteLine($"reference symbols={refPx.Count}");

var ids = new List<string>();
var samples = new List<string>();
await using (var cmd = c.CreateCommand())
{
    cmd.CommandText = """
        SELECT Id, Symbol, ForecastForDate, PredictedHigh, PredictedLow,
               IFNULL(ActualHigh,0), IFNULL(ActualLow,0),
               IFNULL(CombinedAccuracyPct,0), Status, Source, CountsTowardAccuracy,
               IFNULL(Notes,'')
        FROM AppPriceRangeForecasts
        """;
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        var id = r.GetString(0);
        var sym = r.GetString(1);
        if (!refPx.TryGetValue(sym, out var px) || px <= 0) continue;

        var ph = r.GetDecimal(3);
        var pl = r.GetDecimal(4);
        var ah = r.GetDecimal(5);
        var al = r.GetDecimal(6);
        var acc = r.GetDecimal(7);
        var st = r.GetInt32(8);
        var src = r.GetInt32(9);
        var cta = r.GetBoolean(10);
        var notes = r.GetString(11);

        var predBad = !IsPlausible(ph, pl, px);
        var actBad = ah > 0 && al > 0 && !IsPlausible(ah, al, px);
        // 假价对假价：预测与 Actual 彼此接近（虚高准确率），但都偏离真价
        var fakeSuccess = false;
        if (ah > 0 && al > 0 && ph > 0 && pl > 0)
        {
            var predMid = (Math.Max(ph, pl) + Math.Min(ph, pl)) / 2m;
            var actMid = (ah + al) / 2m;
            var closeToEachOther = IsPlausible(predMid, predMid, actMid, 0.20m);
            var bothFarFromReal = !IsPlausible(predMid, predMid, px, 0.45m)
                                  && !IsPlausible(actMid, actMid, px, 0.45m);
            fakeSuccess = closeToEachOther && bothFarFromReal && (cta || acc >= 40m || st == 1);
        }

        var noteFake = notes.Contains("模拟", StringComparison.Ordinal)
                       || notes.Contains("假预测", StringComparison.Ordinal)
                       || notes.Contains("价位离谱", StringComparison.Ordinal);
        if (!predBad && !actBad && !noteFake && !fakeSuccess) continue;

        ids.Add(id);
        if (samples.Count < 60)
        {
            samples.Add(
                $"{sym} {r.GetDateTime(2):yyyy-MM-dd} P{ph:F2}/{pl:F2} A{ah:F2}/{al:F2} " +
                $"acc={acc:F0} st={st} src={src} cta={cta} ref={px:F2} " +
                $"predBad={predBad} actBad={actBad}");
        }
    }
}

Console.WriteLine($"to_delete={ids.Count}");
foreach (var s in samples) Console.WriteLine(s);
if (ids.Count > samples.Count)
    Console.WriteLine($"... +{ids.Count - samples.Count} more");

if (dryRun || ids.Count == 0)
{
    Console.WriteLine(dryRun ? "Dry-run only, no delete." : "Nothing to delete.");
    return 0;
}

await using (var tx = c.BeginTransaction())
{
    await using var del = c.CreateCommand();
    del.Transaction = tx;
    del.CommandText = "DELETE FROM AppPriceRangeForecasts WHERE Id = $id";
    var p = del.Parameters.Add("$id", SqliteType.Text);
    var n = 0;
    foreach (var id in ids)
    {
        p.Value = id;
        n += await del.ExecuteNonQueryAsync();
    }

    // 清掉策略参数里仍挂着的离谱 forecast（按成交/成本参考）
    var clearedStrategies = await ClearFakeStrategyForecastsAsync(c, tx, refPx);
    await tx.CommitAsync();
    Console.WriteLine($"deleted_forecast_rows={n} cleared_strategy_forecasts={clearedStrategies}");
}

return 0;

static bool IsPlausible(decimal high, decimal low, decimal reference, decimal maxRel = 0.45m)
{
    if (reference <= 0 || high <= 0 || low <= 0) return true;
    var mid = (Math.Max(high, low) + Math.Min(high, low)) / 2m;
    var lo = reference * (1m - maxRel);
    var hi = reference * (1m + maxRel);
    return mid >= lo && mid <= hi;
}

static async Task<Dictionary<string, decimal>> LoadReferencePricesAsync(SqliteConnection c)
{
    var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    // 最近成交价优先
    await using (var cmd = c.CreateCommand())
    {
        cmd.CommandText = "SELECT Symbol, Price, CreationTime FROM AppTrades WHERE Price > 0 ORDER BY CreationTime DESC";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var s = r.GetString(0);
            if (!map.ContainsKey(s)) map[s] = r.GetDecimal(1);
        }
    }

    await using (var cmd = c.CreateCommand())
    {
        cmd.CommandText = "SELECT Symbol, AvgCost FROM AppPositions WHERE AvgCost > 0";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var s = r.GetString(0);
            if (!map.ContainsKey(s)) map[s] = r.GetDecimal(1);
        }
    }

    // 同标的中，用「众数价位带」的 Evaluated Actual 中位数作补充（排除离谱 Actual）
    var bySym = new Dictionary<string, List<decimal>>(StringComparer.OrdinalIgnoreCase);
    await using (var cmd = c.CreateCommand())
    {
        cmd.CommandText = """
            SELECT Symbol, ActualHigh, ActualLow FROM AppPriceRangeForecasts
            WHERE Status = 1 AND ActualHigh > 0 AND ActualLow > 0
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var s = r.GetString(0);
            var mid = (r.GetDecimal(1) + r.GetDecimal(2)) / 2m;
            if (!bySym.TryGetValue(s, out var list))
            {
                list = [];
                bySym[s] = list;
            }
            list.Add(mid);
        }
    }

    foreach (var (sym, list) in bySym)
    {
        if (map.ContainsKey(sym) || list.Count < 3) continue;
        list.Sort();
        var median = list[list.Count / 2];
        // 去掉相对中位数离谱的点后再取中位，作为弱参考
        var cluster = list.Where(x => IsPlausible(x, x, median, 0.35m)).OrderBy(x => x).ToList();
        if (cluster.Count >= 3)
            map[sym] = cluster[cluster.Count / 2];
    }

    return map;
}

static async Task<int> ClearFakeStrategyForecastsAsync(
    SqliteConnection c,
    SqliteTransaction tx,
    Dictionary<string, decimal> refPx)
{
    var cleared = 0;
    await using var cmd = c.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = """
        SELECT v.Id, i.Symbol, v.ParametersJson
        FROM AppStockPoolItems i
        JOIN AppStrategies s ON s.Id = i.BoundStrategyId
        JOIN AppStrategyVersions v ON v.Id = s.CurrentVersionId
        WHERE i.BoundStrategyId IS NOT NULL
        """;
    var updates = new List<(string Id, string Json)>();
    await using (var r = await cmd.ExecuteReaderAsync())
    {
        while (await r.ReadAsync())
        {
            var id = r.GetString(0);
            var sym = r.GetString(1);
            var json = r.GetString(2);
            if (!refPx.TryGetValue(sym, out var px) || px <= 0) continue;
            if (!TryStripImplausibleForecast(json, px, out var updated)) continue;
            updates.Add((id, updated));
        }
    }

    foreach (var (id, json) in updates)
    {
        await using var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE AppStrategyVersions SET ParametersJson = $j, ChangeNote = IFNULL(ChangeNote,'') || ' | 清除假预测' WHERE Id = $id";
        upd.Parameters.AddWithValue("$j", json);
        upd.Parameters.AddWithValue("$id", id);
        cleared += await upd.ExecuteNonQueryAsync();
    }

    return cleared;
}

static bool TryStripImplausibleForecast(string json, decimal refPx, out string updated)
{
    updated = json;
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("forecast", out var fc))
            return false;
        var high = fc.TryGetProperty("predictedHigh", out var h) && h.TryGetDecimal(out var hv) ? hv : 0m;
        var low = fc.TryGetProperty("predictedLow", out var l) && l.TryGetDecimal(out var lv) ? lv : 0m;
        if (high <= 0 || low <= 0) return false;
        if (IsPlausible(high, low, refPx)) return false;

        var node = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsObject();
        if (node is null) return false;
        node.Remove("forecast");
        var notes = node["aiNotes"]?.GetValue<string>() ?? "";
        node["aiNotes"] = notes + $" | 已清除疑似模拟行情假预测(中枢相对真价{refPx:F2}离谱)";
        updated = node.ToJsonString();
        return true;
    }
    catch
    {
        return false;
    }
}
