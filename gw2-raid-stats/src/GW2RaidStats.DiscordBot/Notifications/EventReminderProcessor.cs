using Discord.WebSocket;
using GW2RaidStats.Infrastructure.Database;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.DiscordBot.Notifications;

/// <summary>
/// Background poller (parallel to NotificationProcessor) that DMs accepted-signup
/// users a fixed 30 minutes before each event starts. Per-user lead-time and
/// per-event lead-time overrides are deferred to Phase 2.
///
/// Each event fires reminders exactly once: reminder_sent_at on the events row is the
/// idempotency marker. Cancelled events are skipped. Subscribers come from the
/// event_reminder_preferences table (one row per Discord user, opt-in only).
/// </summary>
public class EventReminderProcessor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<EventReminderProcessor> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(30);

    public EventReminderProcessor(
        IServiceProvider serviceProvider,
        DiscordSocketClient client,
        ILogger<EventReminderProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _client = client;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_client.ConnectionState != Discord.ConnectionState.Connected)
        {
            await Task.Delay(1000, stoppingToken);
        }

        _logger.LogInformation("Event reminder processor started (lead time: {Lead})", LeadTime);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessRemindersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing event reminders");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task ProcessRemindersAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaidStatsDb>();

        var now = DateTimeOffset.UtcNow;
        var windowEnd = now.Add(LeadTime);

        // Due = not cancelled, not yet reminded, scheduled within the next LeadTime
        // (and still in the future so we don't fire reminders for events already past).
        var dueEvents = await db.Events
            .Where(e => e.Status != "Cancelled"
                     && e.ReminderSentAt == null
                     && e.ScheduledAt > now
                     && e.ScheduledAt <= windowEnd)
            .ToListAsync(ct);

        if (dueEvents.Count == 0) return;
        _logger.LogDebug("Found {Count} events due for reminder", dueEvents.Count);

        foreach (var ev in dueEvents)
        {
            var dispatched = await DispatchRemindersForEventAsync(db, ev, ct);
            ev.ReminderSentAt = DateTimeOffset.UtcNow;
            await db.UpdateAsync(ev, token: ct);
            _logger.LogInformation("Sent {Count} reminder DM(s) for event {Id} ({Title})",
                dispatched, ev.Id, ev.Title);
        }
    }

    private async Task<int> DispatchRemindersForEventAsync(
        RaidStatsDb db, Infrastructure.Database.Entities.EventEntity ev, CancellationToken ct)
    {
        var acceptedUserIds = await db.EventSignups
            .Where(s => s.EventId == ev.Id && s.Status == "Accepted")
            .Select(s => s.DiscordUserId)
            .ToListAsync(ct);
        if (acceptedUserIds.Count == 0) return 0;

        var subscribers = await db.EventReminderPreferences
            .Where(p => acceptedUserIds.Contains(p.DiscordUserId) && p.Enabled)
            .Select(p => p.DiscordUserId)
            .ToListAsync(ct);
        if (subscribers.Count == 0) return 0;

        var unix = ev.ScheduledAt.ToUnixTimeSeconds();
        var message = $"⏰ Reminder: **{ev.Title}** starts <t:{unix}:R> (<t:{unix}:F>).";

        var sent = 0;
        foreach (var discordUserId in subscribers)
        {
            try
            {
                var user = await _client.GetUserAsync((ulong)discordUserId);
                if (user == null)
                {
                    _logger.LogWarning("User {Id} not found for reminder DM", discordUserId);
                    continue;
                }
                var dm = await user.CreateDMChannelAsync();
                await dm.SendMessageAsync(message);
                sent++;
            }
            catch (Exception ex)
            {
                // DMs can fail when the user has them disabled — log and move on.
                _logger.LogWarning(ex, "Failed to DM reminder to user {Id}", discordUserId);
            }
        }
        return sent;
    }
}
