using GW2RaidStats.Infrastructure.Configuration;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Services;
using GW2RaidStats.Infrastructure.Services.Import;
using GW2RaidStats.Processor.Configuration;
using GW2RaidStats.Processor.Services;
using GW2RaidStats.Processor.Workers;
using LinqToDB;
using LinqToDB.Data;

// Enable legacy timestamp behavior for Npgsql to properly handle DateTimeOffset with timezones
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = Host.CreateApplicationBuilder(args);

// Configuration
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<ProcessorOptions>(builder.Configuration.GetSection(ProcessorOptions.SectionName));

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddScoped<RaidStatsDb>(_ =>
    new RaidStatsDb(new DataOptions<RaidStatsDb>(
        new DataOptions().UsePostgreSQL(connectionString)
    )));

// Services
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<IncludedPlayerService>();
builder.Services.AddScoped<RecordNotificationService>();
builder.Services.AddScoped<LogImportService>();
builder.Services.AddSingleton<Gw2EiRunner>();
builder.Services.AddScoped<LogProcessor>();

// Background worker
builder.Services.AddHostedService<LogProcessingWorker>();

var host = builder.Build();

host.Run();
