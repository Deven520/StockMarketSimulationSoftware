using StockSim.AI.Cursor;
using StockSim.Application;
using StockSim.Application.Services;
using StockSim.Domain.MarketData;
using StockSim.Infrastructure;
using StockSim.Infrastructure.Persistence;
using StockSim.MarketData.Replay;

var builder = WebApplication.CreateBuilder(args);

var repoRoot = FindRepoRoot(builder.Environment.ContentRootPath);
builder.Configuration["MarketData:ReplayDirectory"] ??= Path.Combine(repoRoot, "data", "replay");
builder.Configuration["AI:WorkDirectory"] ??= Path.Combine(repoRoot, "data", "ai");

builder.Services.AddStockSimInfrastructure(builder.Configuration);
builder.Services.AddStockSimApplication(builder.Configuration);
builder.Services.AddSingleton<ReplayMarketDataProvider>();
builder.Services.AddSingleton<IMarketDataProvider>(sp =>
    new LiveOrReplayMarketDataProvider(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ReplayMarketDataProvider>(),
        sp.GetRequiredService<ILogger<LiveOrReplayMarketDataProvider>>()));
builder.Services.AddHttpClient<IStockDetailProvider, EastMoneyStockDetailProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
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
builder.Services.AddSingleton<ICursorAiClient, CursorSdkAiClient>();
builder.Services.AddControllersWithViews();

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services);
await app.Services.GetRequiredService<IMarketDataProvider>().ConnectAsync();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
// 站点开放访问，无需登录
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();

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
