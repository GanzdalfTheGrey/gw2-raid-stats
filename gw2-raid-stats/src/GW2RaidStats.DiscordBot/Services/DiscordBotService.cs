using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GW2RaidStats.DiscordBot.Services;

public class DiscordBotSettings
{
    public string BotToken { get; set; } = null!;
    public ulong? TestGuildId { get; set; }
    public string? AppUrl { get; set; }
}

public class DiscordBotService : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactionService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly DiscordBotSettings _settings;

    public DiscordBotService(
        DiscordSocketClient client,
        InteractionService interactionService,
        IServiceProvider serviceProvider,
        IOptions<DiscordBotSettings> settings,
        ILogger<DiscordBotService> logger)
    {
        _client = client;
        _interactionService = interactionService;
        _serviceProvider = serviceProvider;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.Log += LogAsync;
        _client.Ready += ReadyAsync;
        _client.InteractionCreated += HandleInteractionAsync;
        _client.JoinedGuild += JoinedGuildAsync;

        _interactionService.Log += LogAsync;

        // Add command modules
        await _interactionService.AddModulesAsync(typeof(DiscordBotService).Assembly, _serviceProvider);

        // Login and start
        await _client.LoginAsync(TokenType.Bot, _settings.BotToken);
        await _client.StartAsync();

        _logger.LogInformation("Discord bot started");

        // Wait until cancellation is requested
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Discord bot stopping");
        await _client.StopAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task ReadyAsync()
    {
        _logger.LogInformation("Discord bot is ready. Connected to {GuildCount} guild(s)", _client.Guilds.Count);

        // Register commands
        if (_settings.TestGuildId.HasValue)
        {
            // Register to test guild instantly (for development)
            _logger.LogInformation("Registering commands to test guild {GuildId}", _settings.TestGuildId.Value);
            await _interactionService.RegisterCommandsToGuildAsync(_settings.TestGuildId.Value);
        }
        else
        {
            // Register globally (takes up to an hour to propagate)
            _logger.LogInformation("Registering commands globally");
            await _interactionService.RegisterCommandsGloballyAsync();
        }
    }

    private async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        try
        {
            var context = new SocketInteractionContext(_client, interaction);
            var result = await _interactionService.ExecuteCommandAsync(context, _serviceProvider);

            if (!result.IsSuccess)
            {
                _logger.LogError("Command execution failed: {Error}", result.ErrorReason);

                if (interaction.Type == InteractionType.ApplicationCommand)
                {
                    await interaction.RespondAsync($"Error: {result.ErrorReason}", ephemeral: true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while handling interaction");

            if (interaction.Type == InteractionType.ApplicationCommand)
            {
                await interaction.RespondAsync("An error occurred while processing the command.", ephemeral: true);
            }
        }
    }

    private async Task JoinedGuildAsync(SocketGuild guild)
    {
        _logger.LogInformation("Joined guild: {GuildName} ({GuildId})", guild.Name, guild.Id);

        // Create default config for this guild
        // This will be handled by DiscordConfigService
        await Task.CompletedTask;
    }

    private Task LogAsync(LogMessage message)
    {
        var severity = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };

        _logger.Log(severity, message.Exception, "{Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}
