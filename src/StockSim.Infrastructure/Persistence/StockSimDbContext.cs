using Microsoft.EntityFrameworkCore;
using StockSim.Application.Services;
using StockSim.Domain.Entities;

namespace StockSim.Infrastructure.Persistence;

public class StockSimDbContext : DbContext, IStockSimDataStore
{
    public StockSimDbContext(DbContextOptions<StockSimDbContext> options) : base(options)
    {
    }

    public DbSet<StockPool> StockPools => Set<StockPool>();
    public DbSet<StockPoolItem> StockPoolItems => Set<StockPoolItem>();
    public DbSet<StrategyDefinition> Strategies => Set<StrategyDefinition>();
    public DbSet<StrategyVersion> StrategyVersions => Set<StrategyVersion>();
    public DbSet<StrategyBinding> StrategyBindings => Set<StrategyBinding>();
    public DbSet<SimAccount> Accounts => Set<SimAccount>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<DailyPnL> DailyPnLs => Set<DailyPnL>();
    public DbSet<AiJob> AiJobs => Set<AiJob>();
    public DbSet<AiSuggestion> AiSuggestions => Set<AiSuggestion>();
    public DbSet<AiQueueItem> AiQueueItems => Set<AiQueueItem>();
    public DbSet<StrategyRuntimeStateRow> StrategyRuntimeStates => Set<StrategyRuntimeStateRow>();
    public DbSet<StrategyDecisionLog> StrategyDecisionLogs => Set<StrategyDecisionLog>();
    public DbSet<InvestmentGoal> InvestmentGoals => Set<InvestmentGoal>();
    public DbSet<InvestmentGoalStock> InvestmentGoalStocks => Set<InvestmentGoalStock>();
    public DbSet<GoalProgressDaily> GoalProgressDailies => Set<GoalProgressDaily>();
    public DbSet<PriceRangeForecast> PriceRangeForecasts => Set<PriceRangeForecast>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<StockPool>(e =>
        {
            e.ToTable("AppStockPools");
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<StockPoolItem>(e =>
        {
            e.ToTable("AppStockPoolItems");
            e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.StockPoolId, x.Symbol }).IsUnique();
            e.HasIndex(x => x.BoundStrategyId);
            e.Property(x => x.AllowTFromDate);
            e.HasOne(x => x.BoundStrategy)
                .WithMany()
                .HasForeignKey(x => x.BoundStrategyId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<StrategyDefinition>(e =>
        {
            e.ToTable("AppStrategies");
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<StrategyVersion>(e =>
        {
            e.ToTable("AppStrategyVersions");
            // 不指定 SQL Server 专有 nvarchar(max)，由提供程序映射（Sqlite→TEXT / SqlServer→nvarchar(max)）
            e.Property(x => x.ParametersJson).IsRequired();
            e.HasIndex(x => new { x.StrategyDefinitionId, x.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<StrategyBinding>(e =>
        {
            e.ToTable("AppStrategyBindings");
            e.Property(x => x.Symbol).HasMaxLength(16);
        });

        modelBuilder.Entity<SimAccount>(e =>
        {
            e.ToTable("AppSimAccounts");
            e.Property(x => x.Cash).HasPrecision(18, 2);
            e.Property(x => x.InitialCash).HasPrecision(18, 2);
        });

        modelBuilder.Entity<Position>(e =>
        {
            e.ToTable("AppPositions");
            e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
            e.Property(x => x.AvgCost).HasPrecision(18, 4);
            e.HasIndex(x => new { x.SimAccountId, x.Symbol }).IsUnique();
        });

        modelBuilder.Entity<Order>(e =>
        {
            e.ToTable("AppOrders");
            e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
            e.Property(x => x.Price).HasPrecision(18, 4);
            e.HasIndex(x => new { x.TradeDate, x.Symbol });
        });

        modelBuilder.Entity<Trade>(e =>
        {
            e.ToTable("AppTrades");
            e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
            e.Property(x => x.Price).HasPrecision(18, 4);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Commission).HasPrecision(18, 2);
            e.Property(x => x.StampTax).HasPrecision(18, 2);
            e.Property(x => x.TransferFee).HasPrecision(18, 2);
            e.Property(x => x.Slippage).HasPrecision(18, 2);
            e.Property(x => x.TotalFee).HasPrecision(18, 2);
            e.HasIndex(x => new { x.TradeDate, x.Symbol });
            e.HasIndex(x => new { x.StockPoolId, x.TradeDate });
        });

        modelBuilder.Entity<DailyPnL>(e =>
        {
            e.ToTable("AppDailyPnLs");
            e.Property(x => x.Symbol).HasMaxLength(16);
            e.Property(x => x.RealizedPnL).HasPrecision(18, 2);
            e.Property(x => x.Fees).HasPrecision(18, 2);
            e.Property(x => x.WinRate).HasPrecision(18, 4);
            e.HasIndex(x => new { x.TradeDate, x.Symbol });
        });

        modelBuilder.Entity<AiJob>(e =>
        {
            e.ToTable("AppAiJobs");
            e.Property(x => x.Prompt).IsRequired();
            e.Property(x => x.InputPayload);
            e.Property(x => x.OutputPayload);
            e.Property(x => x.AnalysisSummary);
            e.Property(x => x.AnalysisStepsJson);
        });

        modelBuilder.Entity<AiSuggestion>(e =>
        {
            e.ToTable("AppAiSuggestions");
            e.Property(x => x.PayloadJson).IsRequired();
            e.Property(x => x.Symbol).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Reason);
            e.Property(x => x.SelectionLogic);
            e.Property(x => x.Risk);
        });

        modelBuilder.Entity<AiQueueItem>(e =>
        {
            e.ToTable("AppAiQueueItems");
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.Symbol).HasMaxLength(16);
            e.Property(x => x.WorkerId).HasMaxLength(128);
            e.Property(x => x.InputPayload);
            e.Property(x => x.OutputPayload);
            e.Property(x => x.ErrorMessage);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreationTime);
            e.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<StrategyRuntimeStateRow>(e =>
        {
            e.ToTable("AppStrategyRuntimeStates");
            e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
            e.Property(x => x.State).HasMaxLength(32).IsRequired();
            e.Property(x => x.DayRealizedPnL).HasPrecision(18, 2);
            e.Property(x => x.LastSellPrice).HasPrecision(18, 4);
            e.Property(x => x.LastBuyPrice).HasPrecision(18, 4);
            e.HasIndex(x => new { x.Symbol, x.TradeDate }).IsUnique();
        });

        modelBuilder.Entity<StrategyDecisionLog>(e =>
        {
            e.ToTable("AppStrategyDecisionLogs");
            e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
            e.Property(x => x.State).HasMaxLength(32);
            e.Property(x => x.Action).HasMaxLength(16);
            e.Property(x => x.LastPrice).HasPrecision(18, 4);
            e.Property(x => x.AnchorPrice).HasPrecision(18, 4);
            e.Property(x => x.DeviationPct).HasPrecision(18, 4);
            e.HasIndex(x => new { x.Symbol, x.TradeDate, x.DecidedAt });
        });

        modelBuilder.Entity<InvestmentGoal>(e =>
        {
            e.ToTable("AppInvestmentGoals");
            e.Property(x => x.ExpectedReturnPct).HasPrecision(18, 4);
            e.Property(x => x.ThemePreference).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<InvestmentGoalStock>(e =>
        {
            e.ToTable("AppInvestmentGoalStocks");
            e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.BaselineCash).HasPrecision(18, 2);
            e.Property(x => x.BaselinePrice).HasPrecision(18, 4);
            e.Property(x => x.ActualClose).HasPrecision(18, 4);
            e.Property(x => x.NextDayReturnPct).HasPrecision(18, 4);
            e.HasIndex(x => new { x.InvestmentGoalId, x.Symbol }).IsUnique();
        });

        modelBuilder.Entity<GoalProgressDaily>(e =>
        {
            e.ToTable("AppGoalProgressDailies");
            e.Property(x => x.PortfolioReturnPct).HasPrecision(18, 4);
            e.Property(x => x.AchievementRate).HasPrecision(18, 4);
            e.Property(x => x.TotalMarketValue).HasPrecision(18, 2);
            e.Property(x => x.TotalBaseline).HasPrecision(18, 2);
            e.Property(x => x.RealizedPnL).HasPrecision(18, 2);
            e.Property(x => x.UnrealizedPnL).HasPrecision(18, 2);
            e.HasIndex(x => new { x.InvestmentGoalId, x.TradeDate }).IsUnique();
        });

        modelBuilder.Entity<PriceRangeForecast>(e =>
        {
            e.ToTable("AppPriceRangeForecasts");
            e.Property(x => x.Symbol).HasMaxLength(16).IsRequired();
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.PredictedHigh).HasPrecision(18, 4);
            e.Property(x => x.PredictedLow).HasPrecision(18, 4);
            e.Property(x => x.ActualHigh).HasPrecision(18, 4);
            e.Property(x => x.ActualLow).HasPrecision(18, 4);
            e.Property(x => x.HighDeviationPct).HasPrecision(18, 4);
            e.Property(x => x.LowDeviationPct).HasPrecision(18, 4);
            e.Property(x => x.HighAccuracyPct).HasPrecision(18, 4);
            e.Property(x => x.LowAccuracyPct).HasPrecision(18, 4);
            e.Property(x => x.CombinedAccuracyPct).HasPrecision(18, 4);
            e.HasIndex(x => new { x.Symbol, x.ForecastForDate });
            e.HasIndex(x => new { x.Symbol, x.ForecastForDate, x.Source });
            e.HasIndex(x => new { x.Status, x.ForecastForDate });
        });
    }
}
