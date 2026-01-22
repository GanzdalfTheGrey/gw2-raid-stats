using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Services;
using GW2RaidStats.Infrastructure.Services.Import;

namespace GW2RaidStats.Infrastructure;

public static class ServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        // Register database connection factory
        services.AddScoped(_ =>
        {
            var options = new DataOptions<RaidStatsDb>(
                new DataOptions().UsePostgreSQL(connectionString)
            );
            return new RaidStatsDb(options);
        });

        // Register factory for parallel operations (each call creates new connection)
        services.AddSingleton<Func<RaidStatsDb>>(sp =>
        {
            return () => new RaidStatsDb(
                new DataOptions<RaidStatsDb>(
                    new DataOptions().UsePostgreSQL(connectionString)
                )
            );
        });

        // Register import services
        services.AddScoped<LogImportService>();
        services.AddScoped<BulkImportService>();

        // Register stats services
        services.AddScoped<StatsService>();
        services.AddScoped<LeaderboardService>();
        services.AddScoped<IgnoredBossService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<IncludedPlayerService>();
        services.AddScoped<PlayerProfileService>();

        return services;
    }
}
