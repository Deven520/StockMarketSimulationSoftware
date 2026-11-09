using StockSim.AI.Cursor;
using StockSim.Application;
using StockSim.Application.Services;
using StockSim.Domain.MarketData;
using StockSim.Infrastructure;
using StockSim.Infrastructure.Persistence;
using StockSim.MarketData.Replay;
using StockSim.Worker;

var builder = Host.CreateApplicationBuilder(args);

var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
builder.Configuration["MarketData:ReplayDirectory"] ??= Path.Combine(repoRoot, "data", "replay");
builder.Configuration["AI:WorkDirectory"] ??= Path.Combine(repoRoot, "data", "ai");

// 默认与 Web 开发库对齐：使用同一份 stocksim.db（须带 Data Source= 前缀）
var configuredCs = builder.Configuration.GetConnectionString("StockSim");
if (string.IsNullOrWhiteSpace(configuredCs)
    || configuredCs.Contains("StockSim.Web", StringComparison.OrdinalIgnoreCase)
    || !configuredCs.Contains('=', StringComparison.Ordinal))
{
    var dbPath = Path.Combine(repoRoot, "src", "StockSim.Web", "stocksim.db");
    builder.Configuration["ConnectionStrings:StockSim"] = "Data Source=" + dbPath;
}

builder.Services.AddStockSimInfrastructure(builder.Configuration);
builder.Services.AddStockSimApplication(builder.Configuration);
builder.Services.AddSingleton<ReplayMarketDataProvider>();
builder.Services.AddSingleton<IMarketDataProvider>(sp =>
    new LiveOrReplayMarketDataProvider(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ReplayMarketDataProvider>(),
        sp.GetRequiredService<ILogger<LiveOrReplayMarketDataProvider>>()));
builder.Services.AddSingleton<ICursorAiClient, CursorSdkAiClient>();
builder.Services.AddHttpClient<IStockDetailProvider, EastMoneyStockDetailProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
});
builder.Services.AddHttpClient<IMarketNewsProvider, EastMoneyMarketNewsProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddHttpClient<IStockSuggestProvider, EastMoneyStockSuggestProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHostedService<StrategyWorker>();
builder.Services.AddHostedService<DailySettlementWorker>();
builder.Services.AddHostedService<NightlyStrategyRefreshWorker>();

var host = builder.Build();
await DbInitializer.InitializeAsync(host.Services);

var market = host.Services.GetRequiredService<IMarketDataProvider>();
await market.ConnectAsync();

host.Run();

static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "StockSim.slnx"))
            || Directory.Exists(Path.Combine(dir.FullName, "docs")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return start;
}
