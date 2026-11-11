using Microsoft.Data.Sqlite;

var dbPath = args.Length > 0
    ? args[0]
    : @"d:\Projects\StockMarketWeb\StockMarketSimulationSoftware\publish\web\stocksim.db";
var mode = args.Length > 1 ? args[1] : "inspect";

await using var conn = new SqliteConnection($"Data Source={dbPath}");
await conn.OpenAsync();

async Task Print(string title, string sql)
{
    Console.WriteLine($"\n=== {title} ===");
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await using var r = await cmd.ExecuteReaderAsync();
    var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
    Console.WriteLine(string.Join(" | ", cols));
    while (await r.ReadAsync())
    {
        var vals = cols.Select((_, i) => r.IsDBNull(i) ? "NULL" : r.GetValue(i)?.ToString() ?? "").ToArray();
        Console.WriteLine(string.Join(" | ", vals));
    }
}

if (mode == "tables")
{
    await Print("TABLES", "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;");
    return;
}

if (mode == "inspect")
{
    await Print("ITEMS", "SELECT Symbol, Name, InitialCash, AvailableCash, AllowTFromDate, TradeStyle FROM AppStockPoolItems ORDER BY Symbol;");
    await Print("POSITIONS", "SELECT Symbol, Quantity, SellableQuantity, BaseQuantity, AvgCost FROM AppPositions WHERE Quantity<>0 OR AvgCost<>0 ORDER BY Symbol;");
    await Print("TODAY TRADES", @"
SELECT Symbol, Side, Quantity, Price, TotalFee, Amount, TradedAt
FROM AppTrades
WHERE date(TradeDate)=date('now','localtime') OR date(TradedAt,'localtime')=date('now','localtime')
ORDER BY Symbol, TradedAt;");
    await Print("DAILY PNL TODAY", "SELECT Symbol, TradeDate, RealizedPnL, TradeCount FROM AppDailyPnLs WHERE date(TradeDate)=date('now','localtime') ORDER BY Symbol;");
    await Print("ACCOUNT", "SELECT Id, Cash, InitialCash FROM AppSimAccounts;");
    return;
}

if (mode == "reset-pool")
{
    // 全池今日成交普遍含异常 Bid 卖出价，无法逐笔纠正 → 重置为入池状态
    var symbols = new List<string>();
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT Symbol FROM AppStockPoolItems ORDER BY Symbol;";
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            symbols.Add(r.GetString(0));
    }

    Console.WriteLine("Reset symbols: " + string.Join(", ", symbols));
    if (symbols.Count == 0)
    {
        Console.WriteLine("No pool items.");
        return;
    }

    // 下一交易日（简化：跳过周末）
    var next = DateTime.Today.AddDays(1);
    while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        next = next.AddDays(1);
    var allowT = next.ToString("yyyy-MM-dd");

    await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

    decimal oldCashSum = 0m;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT IFNULL(SUM(AvailableCash),0) FROM AppStockPoolItems;";
        oldCashSum = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
    }

    foreach (var sym in symbols)
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE AppStockPoolItems
SET AvailableCash = CASE WHEN InitialCash > 0 THEN InitialCash ELSE 200000 END,
    AllowTFromDate = $allowT,
    LastModificationTime = datetime('now')
WHERE Symbol = $s;";
            cmd.Parameters.AddWithValue("$allowT", allowT);
            cmd.Parameters.AddWithValue("$s", sym);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE AppPositions
SET Quantity = 0, BaseQuantity = 0, SellableQuantity = 0, AvgCost = 0
WHERE Symbol = $s;";
            cmd.Parameters.AddWithValue("$s", sym);
            await cmd.ExecuteNonQueryAsync();
        }

        int trades;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM AppTrades WHERE Symbol = $s;";
            cmd.Parameters.AddWithValue("$s", sym);
            trades = await cmd.ExecuteNonQueryAsync();
        }

        int pnls;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM AppDailyPnLs WHERE Symbol = $s;";
            cmd.Parameters.AddWithValue("$s", sym);
            pnls = await cmd.ExecuteNonQueryAsync();
        }

        int orders = 0;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM AppOrders WHERE Symbol = $s;";
            cmd.Parameters.AddWithValue("$s", sym);
            orders = await cmd.ExecuteNonQueryAsync();
        }
        catch { /* optional */ }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE AppStrategyRuntimeStates
SET DayRealizedPnL = 0, State = 0, ConsecutiveLosses = 0
WHERE Symbol = $s;";
            cmd.Parameters.AddWithValue("$s", sym);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* optional / schema may differ */ }

        Console.WriteLine($"{sym}: trades-{trades}, pnl-{pnls}, orders-{orders}, AllowTFrom={allowT}");
    }

    decimal newCashSum;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT IFNULL(SUM(AvailableCash),0) FROM AppStockPoolItems;";
        newCashSum = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
    }

    var delta = oldCashSum - newCashSum;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE AppSimAccounts SET Cash = MAX(0, Cash - $d);";
        cmd.Parameters.AddWithValue("$d", delta);
        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"Account cash adjusted by -{delta} (pool cash {oldCashSum} -> {newCashSum})");
    }

    await tx.CommitAsync();
    Console.WriteLine("\nReset complete.");
    await Print("ITEMS AFTER", "SELECT Symbol, InitialCash, AvailableCash, AllowTFromDate FROM AppStockPoolItems ORDER BY Symbol;");
    await Print("POS AFTER", "SELECT Symbol, Quantity, AvgCost FROM AppPositions WHERE Quantity<>0 OR AvgCost<>0 ORDER BY Symbol;");
    await Print("TRADES LEFT TODAY", "SELECT COUNT(*) AS Cnt FROM AppTrades WHERE date(TradeDate)=date('now','localtime');");
    await Print("ACCOUNT AFTER", "SELECT Cash, InitialCash FROM AppSimAccounts;");
    return;
}

Console.WriteLine("Unknown mode. Use: inspect | reset-pool | tables");
