using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StockSim.Domain.Entities;
using StockSim.Domain.Enums;
using StockSim.Domain.Strategies;

namespace StockSim.Infrastructure.Persistence;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockSimDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<StockSimDbContext>>();

        await db.Database.EnsureCreatedAsync();
        await EnsureAiColumnsAsync(db, logger);
        await EnsurePerStockStrategyColumnsAsync(db, logger);
        await EnsureStrategyRuntimeTablesAsync(db, logger);
        await EnsureInvestmentGoalTablesAsync(db, logger);
        await EnsurePriceRangeForecastTableAsync(db, logger);
        await EnsureAiQueueTableAsync(db, logger);

        if (await db.Accounts.AnyAsync())
        {
            return;
        }

        logger.LogInformation("Seeding StockSim demo data...");

        var account = new SimAccount
        {
            Name = "默认模拟账户",
            Cash = 1_000_000m,
            InitialCash = 1_000_000m,
            TradingMode = TradingMode.SimIntradaySellAllowed
        };
        account.Positions.Add(new Position
        {
            Symbol = "600519",
            Name = "贵州茅台",
            BaseQuantity = 1000,
            Quantity = 1000,
            SellableQuantity = 1000,
            AvgCost = 1700m
        });
        account.Positions.Add(new Position
        {
            Symbol = "000001",
            Name = "平安银行",
            BaseQuantity = 5000,
            Quantity = 5000,
            SellableQuantity = 5000,
            AvgCost = 11.5m
        });
        db.Accounts.Add(account);

        var pool = new StockPool { Name = "默认做T池", Description = "演示股票池（一股一策）" };
        var item1 = new StockPoolItem { Symbol = "600519", Name = "贵州茅台", Source = PoolItemSource.Manual };
        var item2 = new StockPoolItem { Symbol = "000001", Name = "平安银行", Source = PoolItemSource.Manual };
        pool.Items.Add(item1);
        pool.Items.Add(item2);
        db.StockPools.Add(pool);

        // 种子：每只股票一条专属策略（默认参数；运行后可由 AI 夜间任务更新）
        foreach (var item in new[] { item1, item2 })
        {
            var strategy = new StrategyDefinition
            {
                Name = item.Name.EndsWith("策略", StringComparison.Ordinal) ? item.Name : item.Name + "策略",
                Description = $"池内专属策略 | {item.Symbol}",
                IsEnabled = true
            };
            var version = new StrategyVersion
            {
                StrategyDefinition = strategy,
                VersionNumber = 1,
                ParametersJson = StrategyParameters.Default().ToJson(),
                ChangeNote = "种子版本",
                CreatedBy = "seed"
            };
            strategy.Versions.Add(version);
            db.Strategies.Add(strategy);
            await db.SaveChangesAsync();

            strategy.CurrentVersionId = version.Id;
            item.BoundStrategyId = strategy.Id;
            item.StrategyGeneratedAt = DateTime.UtcNow;
            item.StrategyUpdatedAt = DateTime.UtcNow;
            db.StrategyBindings.Add(new StrategyBinding
            {
                StrategyDefinitionId = strategy.Id,
                StockPoolId = pool.Id,
                Symbol = item.Symbol,
                Priority = 100,
                IsEnabled = true
            });
            await db.SaveChangesAsync();
        }

        logger.LogInformation("StockSim demo data ready.");
    }

    /// <summary>
    /// EnsureCreated 不会升级已有库，这里为 SQLite/SQL Server 补齐 AI 查阅所需列。
    /// </summary>
    private static async Task EnsureAiColumnsAsync(StockSimDbContext db, ILogger logger)
    {
        try
        {
            var provider = db.Database.ProviderName ?? string.Empty;
            if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await TryAddSqliteColumnAsync(db, "AppAiJobs", "AnalysisSummary", "TEXT");
                await TryAddSqliteColumnAsync(db, "AppAiJobs", "AnalysisStepsJson", "TEXT");
                await TryAddSqliteColumnAsync(db, "AppAiSuggestions", "Symbol", "TEXT");
                await TryAddSqliteColumnAsync(db, "AppAiSuggestions", "Name", "TEXT");
                await TryAddSqliteColumnAsync(db, "AppAiSuggestions", "Reason", "TEXT");
                await TryAddSqliteColumnAsync(db, "AppAiSuggestions", "SelectionLogic", "TEXT");
                await TryAddSqliteColumnAsync(db, "AppAiSuggestions", "Risk", "TEXT");
            }
            else if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlRawAsync("""
                    IF COL_LENGTH('AppAiJobs', 'AnalysisSummary') IS NULL ALTER TABLE AppAiJobs ADD AnalysisSummary nvarchar(max) NULL;
                    IF COL_LENGTH('AppAiJobs', 'AnalysisStepsJson') IS NULL ALTER TABLE AppAiJobs ADD AnalysisStepsJson nvarchar(max) NULL;
                    IF COL_LENGTH('AppAiSuggestions', 'Symbol') IS NULL ALTER TABLE AppAiSuggestions ADD Symbol nvarchar(16) NULL;
                    IF COL_LENGTH('AppAiSuggestions', 'Name') IS NULL ALTER TABLE AppAiSuggestions ADD Name nvarchar(64) NULL;
                    IF COL_LENGTH('AppAiSuggestions', 'Reason') IS NULL ALTER TABLE AppAiSuggestions ADD Reason nvarchar(max) NULL;
                    IF COL_LENGTH('AppAiSuggestions', 'SelectionLogic') IS NULL ALTER TABLE AppAiSuggestions ADD SelectionLogic nvarchar(max) NULL;
                    IF COL_LENGTH('AppAiSuggestions', 'Risk') IS NULL ALTER TABLE AppAiSuggestions ADD Risk nvarchar(max) NULL;
                    """);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EnsureAiColumns skipped or partial");
        }
    }

    private static async Task EnsurePerStockStrategyColumnsAsync(StockSimDbContext db, ILogger logger)
    {
        try
        {
            var provider = db.Database.ProviderName ?? string.Empty;
            if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await TryAddSqliteColumnAsync(db, "AppStockPoolItems", "BoundStrategyId", "TEXT");
                await TryAddSqliteColumnAsync(db, "AppStockPoolItems", "StrategyGeneratedAt", "TEXT");
                await TryAddSqliteColumnAsync(db, "AppStockPoolItems", "StrategyUpdatedAt", "TEXT");
                await TryAddSqliteColumnAsync(db, "AppStockPoolItems", "InitialCash", "TEXT");
                await TryAddSqliteColumnAsync(db, "AppStockPoolItems", "AvailableCash", "TEXT");
                // SQLite：布尔用 INTEGER；避免 NOT NULL 在部分旧库 ALTER 失败
                await TryAddSqliteColumnAsync(db, "AppStockPoolItems", "NotifyOnTrade", "INTEGER DEFAULT 0");
                await TryAddSqliteColumnAsync(db, "AppStockPoolItems", "AllowTFromDate", "TEXT");
                await db.Database.ExecuteSqlRawAsync("""
                    UPDATE AppStockPoolItems
                    SET InitialCash = '200000', AvailableCash = '200000'
                    WHERE InitialCash IS NULL OR InitialCash = '' OR InitialCash = '0'
                       OR AvailableCash IS NULL OR AvailableCash = '';
                    """);
            }
            else if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlRawAsync("""
                    IF COL_LENGTH('AppStockPoolItems', 'BoundStrategyId') IS NULL ALTER TABLE AppStockPoolItems ADD BoundStrategyId uniqueidentifier NULL;
                    IF COL_LENGTH('AppStockPoolItems', 'StrategyGeneratedAt') IS NULL ALTER TABLE AppStockPoolItems ADD StrategyGeneratedAt datetime2 NULL;
                    IF COL_LENGTH('AppStockPoolItems', 'StrategyUpdatedAt') IS NULL ALTER TABLE AppStockPoolItems ADD StrategyUpdatedAt datetime2 NULL;
                    IF COL_LENGTH('AppStockPoolItems', 'InitialCash') IS NULL ALTER TABLE AppStockPoolItems ADD InitialCash decimal(18,2) NOT NULL DEFAULT 200000;
                    IF COL_LENGTH('AppStockPoolItems', 'AvailableCash') IS NULL ALTER TABLE AppStockPoolItems ADD AvailableCash decimal(18,2) NOT NULL DEFAULT 200000;
                    IF COL_LENGTH('AppStockPoolItems', 'NotifyOnTrade') IS NULL ALTER TABLE AppStockPoolItems ADD NotifyOnTrade bit NOT NULL DEFAULT 0;
                    IF COL_LENGTH('AppStockPoolItems', 'AllowTFromDate') IS NULL ALTER TABLE AppStockPoolItems ADD AllowTFromDate datetime2 NULL;
                    UPDATE AppStockPoolItems SET InitialCash = 200000 WHERE InitialCash = 0;
                    UPDATE AppStockPoolItems SET AvailableCash = 200000 WHERE AvailableCash = 0 AND InitialCash = 200000;
                    """);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EnsurePerStockStrategyColumns skipped or partial");
        }
    }

    private static async Task EnsureStrategyRuntimeTablesAsync(StockSimDbContext db, ILogger logger)
    {
        try
        {
            var provider = db.Database.ProviderName ?? string.Empty;
            if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS AppStrategyRuntimeStates (
                        Id TEXT NOT NULL PRIMARY KEY,
                        CreationTime TEXT NOT NULL,
                        CreatorUserId TEXT NULL,
                        LastModificationTime TEXT NULL,
                        Symbol TEXT NOT NULL,
                        TradeDate TEXT NOT NULL,
                        State TEXT NOT NULL,
                        ConsecutiveLosses INTEGER NOT NULL,
                        DayRealizedPnL TEXT NOT NULL,
                        LastSellPrice TEXT NULL,
                        LastBuyPrice TEXT NULL,
                        OverridesJson TEXT NULL
                    );
                    """);
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE UNIQUE INDEX IF NOT EXISTS IX_AppStrategyRuntimeStates_Symbol_TradeDate
                    ON AppStrategyRuntimeStates (Symbol, TradeDate);
                    """);
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS AppStrategyDecisionLogs (
                        Id TEXT NOT NULL PRIMARY KEY,
                        CreationTime TEXT NOT NULL,
                        Symbol TEXT NOT NULL,
                        TradeDate TEXT NOT NULL,
                        DecidedAt TEXT NOT NULL,
                        State TEXT NULL,
                        Action TEXT NULL,
                        Reason TEXT NULL,
                        LastPrice TEXT NULL,
                        AnchorPrice TEXT NULL,
                        DeviationPct TEXT NULL,
                        EffectiveParamsJson TEXT NULL,
                        StrategyDefinitionId TEXT NULL,
                        BlockedBy TEXT NULL
                    );
                    """);
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE INDEX IF NOT EXISTS IX_AppStrategyDecisionLogs_Symbol_TradeDate
                    ON AppStrategyDecisionLogs (Symbol, TradeDate, DecidedAt);
                    """);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EnsureStrategyRuntimeTables skipped or partial");
        }
    }

    private static async Task EnsureInvestmentGoalTablesAsync(StockSimDbContext db, ILogger logger)
    {
        try
        {
            var provider = db.Database.ProviderName ?? string.Empty;
            if (!provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return;

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS AppInvestmentGoals (
                    Id TEXT NOT NULL PRIMARY KEY,
                    CreationTime TEXT NOT NULL,
                    CreatorUserId TEXT NULL,
                    LastModificationTime TEXT NULL,
                    ExpectedReturnPct TEXT NOT NULL,
                    HorizonDays INTEGER NOT NULL,
                    ThemePreference TEXT NOT NULL,
                    GeneratedPrompt TEXT NULL,
                    PromptRationale TEXT NULL,
                    Status INTEGER NOT NULL,
                    StartDate TEXT NULL,
                    TargetEndDate TEXT NULL,
                    StockPickJobId TEXT NULL
                );
                """);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS AppInvestmentGoalStocks (
                    Id TEXT NOT NULL PRIMARY KEY,
                    CreationTime TEXT NOT NULL,
                    CreatorUserId TEXT NULL,
                    LastModificationTime TEXT NULL,
                    InvestmentGoalId TEXT NOT NULL,
                    Symbol TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    PoolItemId TEXT NULL,
                    SuggestionId TEXT NULL,
                    BaselineCash TEXT NOT NULL,
                    BaselinePrice TEXT NULL
                );
                """);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_AppInvestmentGoalStocks_Goal_Symbol
                ON AppInvestmentGoalStocks (InvestmentGoalId, Symbol);
                """);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS AppGoalProgressDailies (
                    Id TEXT NOT NULL PRIMARY KEY,
                    CreationTime TEXT NOT NULL,
                    InvestmentGoalId TEXT NOT NULL,
                    TradeDate TEXT NOT NULL,
                    PortfolioReturnPct TEXT NOT NULL,
                    AchievementRate TEXT NOT NULL,
                    TotalMarketValue TEXT NOT NULL,
                    TotalBaseline TEXT NOT NULL,
                    RealizedPnL TEXT NOT NULL,
                    UnrealizedPnL TEXT NOT NULL,
                    DayIndex INTEGER NOT NULL,
                    Notes TEXT NULL
                );
                """);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_AppGoalProgressDailies_Goal_Date
                ON AppGoalProgressDailies (InvestmentGoalId, TradeDate);
                """);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EnsureInvestmentGoalTables skipped or partial");
        }
    }

    private static async Task EnsurePriceRangeForecastTableAsync(StockSimDbContext db, ILogger logger)
    {
        try
        {
            var provider = db.Database.ProviderName ?? string.Empty;
            if (!provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return;

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS AppPriceRangeForecasts (
                    Id TEXT NOT NULL PRIMARY KEY,
                    CreationTime TEXT NOT NULL,
                    Symbol TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    ForecastForDate TEXT NOT NULL,
                    PredictedAt TEXT NOT NULL,
                    PredictedHigh TEXT NOT NULL,
                    PredictedLow TEXT NOT NULL,
                    ActualHigh TEXT NULL,
                    ActualLow TEXT NULL,
                    HighDeviationPct TEXT NULL,
                    LowDeviationPct TEXT NULL,
                    HighAccuracyPct TEXT NULL,
                    LowAccuracyPct TEXT NULL,
                    CombinedAccuracyPct TEXT NULL,
                    RangeHit INTEGER NULL,
                    EvaluatedAt TEXT NULL,
                    Status INTEGER NOT NULL,
                    Source INTEGER NOT NULL DEFAULT 1,
                    CountsTowardAccuracy INTEGER NOT NULL DEFAULT 1,
                    StrategyDefinitionId TEXT NULL,
                    StrategyVersionId TEXT NULL,
                    AiJobId TEXT NULL,
                    Prompt TEXT NULL,
                    Notes TEXT NULL
                );
                """);
            await TryAddSqliteColumnAsync(db, "AppPriceRangeForecasts", "Prompt", "TEXT");
            await TryAddSqliteColumnAsync(db, "AppPriceRangeForecasts", "Source", "INTEGER NOT NULL DEFAULT 1");
            await TryAddSqliteColumnAsync(db, "AppPriceRangeForecasts", "CountsTowardAccuracy", "INTEGER NOT NULL DEFAULT 1");
            await db.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_AppPriceRangeForecasts_Symbol_ForecastForDate
                ON AppPriceRangeForecasts (Symbol, ForecastForDate);
                """);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_AppPriceRangeForecasts_Symbol_Date_Source
                ON AppPriceRangeForecasts (Symbol, ForecastForDate, Source);
                """);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_AppPriceRangeForecasts_Status_ForecastForDate
                ON AppPriceRangeForecasts (Status, ForecastForDate);
                """);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EnsurePriceRangeForecastTable skipped or partial");
        }
    }

    private static async Task TryAddSqliteColumnAsync(StockSimDbContext db, string table, string column, string type)
    {
        var exists = false;
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
        {
            await cmd.Connection.OpenAsync();
        }

        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }

        await reader.DisposeAsync();
        if (!exists)
        {
            // table/column 仅来自本方法调用处的固定字面量
            var sql = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + type;
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    private static async Task EnsureAiQueueTableAsync(StockSimDbContext db, ILogger logger)
    {
        try
        {
            var provider = db.Database.ProviderName ?? string.Empty;
            if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS AppAiQueueItems (
                        Id TEXT NOT NULL PRIMARY KEY,
                        CreationTime TEXT NOT NULL,
                        CreatorUserId TEXT NULL,
                        LastModificationTime TEXT NULL,
                        TaskType INTEGER NOT NULL,
                        Status INTEGER NOT NULL,
                        Title TEXT NOT NULL,
                        CorrelationId TEXT NULL,
                        RelatedGoalId TEXT NULL,
                        RelatedAiJobId TEXT NULL,
                        RelatedPoolItemId TEXT NULL,
                        Symbol TEXT NULL,
                        InputPayload TEXT NULL,
                        OutputPayload TEXT NULL,
                        ErrorMessage TEXT NULL,
                        StartedAt TEXT NULL,
                        CompletedAt TEXT NULL,
                        WorkerId TEXT NULL
                    );
                    """);
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE INDEX IF NOT EXISTS IX_AppAiQueueItems_Status ON AppAiQueueItems (Status);
                    """);
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE INDEX IF NOT EXISTS IX_AppAiQueueItems_CreationTime ON AppAiQueueItems (CreationTime);
                    """);
            }
            else if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlRawAsync("""
                    IF OBJECT_ID('AppAiQueueItems', 'U') IS NULL
                    BEGIN
                        CREATE TABLE AppAiQueueItems (
                            Id uniqueidentifier NOT NULL PRIMARY KEY,
                            CreationTime datetime2 NOT NULL,
                            CreatorUserId nvarchar(64) NULL,
                            LastModificationTime datetime2 NULL,
                            TaskType int NOT NULL,
                            Status int NOT NULL,
                            Title nvarchar(256) NOT NULL,
                            CorrelationId uniqueidentifier NULL,
                            RelatedGoalId uniqueidentifier NULL,
                            RelatedAiJobId uniqueidentifier NULL,
                            RelatedPoolItemId uniqueidentifier NULL,
                            Symbol nvarchar(16) NULL,
                            InputPayload nvarchar(max) NULL,
                            OutputPayload nvarchar(max) NULL,
                            ErrorMessage nvarchar(max) NULL,
                            StartedAt datetime2 NULL,
                            CompletedAt datetime2 NULL,
                            WorkerId nvarchar(128) NULL
                        );
                        CREATE INDEX IX_AppAiQueueItems_Status ON AppAiQueueItems (Status);
                        CREATE INDEX IX_AppAiQueueItems_CreationTime ON AppAiQueueItems (CreationTime);
                    END
                    """);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EnsureAiQueueTable skipped or partial");
        }
    }
}

