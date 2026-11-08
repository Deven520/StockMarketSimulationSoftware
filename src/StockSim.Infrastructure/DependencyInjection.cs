using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockSim.Application.Services;
using StockSim.Infrastructure.Persistence;

namespace StockSim.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddStockSimInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("Database:Provider") ?? "Sqlite";
        var connectionString = configuration.GetConnectionString("StockSim")
            ?? "Data Source=stocksim.db";

        services.AddDbContext<StockSimDbContext>(options =>
        {
            if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                options.UseSqlServer(connectionString, sql =>
                {
                    // 兼容 SQL Server 2012+
                    sql.UseCompatibilityLevel(110);
                });
            }
            else
            {
                options.UseSqlite(connectionString);
            }
        });

        services.AddScoped<IStockSimDataStore>(sp => sp.GetRequiredService<StockSimDbContext>());
        return services;
    }
}
