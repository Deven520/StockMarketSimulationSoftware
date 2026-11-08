using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockSim.Application.Services;
using StockSim.Application.Services.Alerts;
using StockSim.Domain.Strategies;
using StockSim.Domain.Trading;

namespace StockSim.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddStockSimApplication(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddMemoryCache();
        services.AddSingleton<IFeeCalculator, AShareFeeCalculator>();
        services.AddSingleton<IRiskChecker, DefaultRiskChecker>();
        services.AddSingleton<IMatchingEngine, InstantMatchingEngine>();
        services.AddSingleton<IStrategyEvaluator, IntradayTStrategyEvaluator>();
        services.AddSingleton<IntradaySessionTracker>();
        services.AddSingleton<AdaptiveOverrideService>();

        services.AddScoped<CachedLightQuoteService>();
        services.AddScoped<TradingAppService>();
        services.AddScoped<StrategyAppService>();
        services.AddScoped<PerStockStrategyService>();
        services.AddScoped<StockPoolAppService>();
        services.AddScoped<PnLAppService>();
        services.AddScoped<AiAppService>();
        services.AddScoped<InvestmentGoalAppService>();
        services.AddScoped<AiQueueAppService>();
        services.AddHostedService<AiQueueHostedService>();
        services.AddScoped<StrategyEngineService>();
        services.AddScoped<StockDetailAppService>();

        if (configuration is not null)
        {
            services.Configure<TradeAlertOptions>(configuration.GetSection(TradeAlertOptions.SectionName));
        }
        else
        {
            services.Configure<TradeAlertOptions>(_ => { });
        }

        services.AddHttpClient<ITradeAlertNotifier, CompositeTradeAlertNotifier>();

        return services;
    }
}
