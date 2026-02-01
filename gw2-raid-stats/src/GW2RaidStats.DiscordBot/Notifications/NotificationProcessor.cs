using Discord.WebSocket;
using GW2RaidStats.DiscordBot.Services;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.DiscordBot.Notifications;

public class NotificationProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<NotificationProcessor> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public NotificationProcessor(
        IServiceProvider serviceProvider,
        DiscordSocketClient client,
        ILogger<NotificationProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _client = client;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for bot to be ready
        while (_client.ConnectionState != Discord.ConnectionState.Connected)
        {
            await Task.Delay(1000, stoppingToken);
        }

        _logger.LogInformation("Notification processor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingNotificationsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing notifications");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task ProcessPendingNotificationsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaidStatsDb>();
        var configService = scope.ServiceProvider.GetRequiredService<DiscordConfigService>();

        // Get unprocessed notifications
        var notifications = await db.NotificationQueue
            .Where(n => n.ProcessedAt == null)
            .OrderBy(n => n.CreatedAt)
            .Take(10)
            .ToListAsync(ct);

        if (notifications.Count == 0) return;

        _logger.LogDebug("Processing {Count} notifications", notifications.Count);

        // Get all enabled configs
        var configs = await configService.GetAllEnabledConfigsAsync(ct);

        foreach (var notification in notifications)
        {
            try
            {
                await ProcessNotificationAsync(notification, configs, ct);

                // Mark as processed
                notification.ProcessedAt = DateTimeOffset.UtcNow;
                await db.UpdateAsync(notification, token: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process notification {Id}", notification.Id);
            }
        }
    }

    private async Task ProcessNotificationAsync(
        NotificationQueueEntity notification,
        List<DiscordConfigEntity> configs,
        CancellationToken ct)
    {
        var handler = GetHandler(notification.NotificationType);
        if (handler == null)
        {
            _logger.LogWarning("Unknown notification type: {Type}", notification.NotificationType);
            return;
        }

        foreach (var config in configs)
        {
            if (!config.NotificationChannelId.HasValue) continue;

            var channel = _client.GetChannel((ulong)config.NotificationChannelId.Value) as Discord.IMessageChannel;
            if (channel == null)
            {
                _logger.LogWarning("Could not find channel {ChannelId} for guild {GuildId}",
                    config.NotificationChannelId, config.GuildId);
                continue;
            }

            try
            {
                await handler.SendAsync(channel, notification.Payload, config.WallOfShameEnabled, ct);
                _logger.LogInformation("Sent {Type} notification to guild {GuildId}",
                    notification.NotificationType, config.GuildId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send notification to guild {GuildId}", config.GuildId);
            }
        }
    }

    private INotificationHandler? GetHandler(string notificationType)
    {
        return notificationType switch
        {
            "session_complete" => _serviceProvider.GetService<SessionNotificationHandler>(),
            "record_broken" => _serviceProvider.GetService<RecordNotificationHandler>(),
            "milestone" => _serviceProvider.GetService<MilestoneNotificationHandler>(),
            "htcm_progress" => _serviceProvider.GetService<HtcmProgressNotificationHandler>(),
            "top_5" => _serviceProvider.GetService<Top5NotificationHandler>(),
            _ => null
        };
    }
}

public interface INotificationHandler
{
    Task SendAsync(Discord.IMessageChannel channel, string payload, bool wallOfShameEnabled, CancellationToken ct);
}
