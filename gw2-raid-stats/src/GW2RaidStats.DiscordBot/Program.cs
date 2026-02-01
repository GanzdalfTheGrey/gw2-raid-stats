using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GW2RaidStats.DiscordBot.Notifications;
using GW2RaidStats.DiscordBot.Services;
using GW2RaidStats.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Enable legacy timestamp behavior for Npgsql to properly handle DateTimeOffset with timezones
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var host = Host.CreateDefaultBuilder(args)
    .UseContentRoot(AppContext.BaseDirectory)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
        config.AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        // Discord bot settings
        services.Configure<DiscordBotSettings>(context.Configuration.GetSection("Discord"));

        // Discord client
        var discordConfig = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers,
            LogLevel = LogSeverity.Info
        };
        services.AddSingleton(discordConfig);
        services.AddSingleton<DiscordSocketClient>();

        // Interaction service for slash commands
        var interactionConfig = new InteractionServiceConfig
        {
            LogLevel = LogSeverity.Info,
            DefaultRunMode = RunMode.Async
        };
        services.AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>(), interactionConfig));

        // Database and infrastructure services
        var connectionString = context.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        services.AddInfrastructure(connectionString);

        // Bot services
        services.AddScoped<DiscordConfigService>();

        // Notification handlers
        services.AddScoped<SessionNotificationHandler>();
        services.AddScoped<RecordNotificationHandler>();
        services.AddScoped<MilestoneNotificationHandler>();
        services.AddScoped<HtcmProgressNotificationHandler>();
        services.AddScoped<Top5NotificationHandler>();

        // Background services
        services.AddHostedService<DiscordBotService>();
        services.AddHostedService<NotificationProcessor>();
    })
    .ConfigureLogging(logging =>
    {
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

Console.WriteLine("Starting GW2 Raid Stats Discord Bot...");
await host.RunAsync();
