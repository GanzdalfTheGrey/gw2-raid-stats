using System.Text.Json;
using Discord.WebSocket;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.DiscordBot.Notifications;

/// <summary>
/// Background poller (parallel to NotificationProcessor) for the event lifecycle.
/// Each 60-second tick does two things:
///   1. Reminder DMs: 30 minutes before each event starts, DM every accepted-signup
///      user whose event_reminder_preferences.enabled is on. Idempotency via
///      events.reminder_sent_at.
///   2. Auto-close: once scheduled_at passes, transition status Scheduled → Closed,
///      and queue an event_post notification so the live Discord embed re-renders
///      with disabled buttons (signups locked, attendance frozen).
///
/// Cancelled events are skipped by both paths.
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

            try
            {
                await ProcessClosingAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing event auto-close");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    // Transitions any Scheduled events whose start time has arrived (or passed) to
    // Closed, then queues an event_post notification so the bot re-renders the embed
    // with disabled buttons. Lag is at most _pollInterval (~60s) past scheduled_at.
    private async Task ProcessClosingAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RaidStatsDb>();

        var now = DateTimeOffset.UtcNow;

        var dueEvents = await db.Events
            .Where(e => e.Status == "Scheduled" && e.ScheduledAt <= now)
            .ToListAsync(ct);

        if (dueEvents.Count == 0) return;

        foreach (var ev in dueEvents)
        {
            ev.Status = "Closed";
            ev.UpdatedAt = now;
            await db.UpdateAsync(ev, token: ct);

            await db.InsertAsync(new NotificationQueueEntity
            {
                Id = Guid.NewGuid(),
                NotificationType = "event_post",
                Payload = JsonSerializer.Serialize(new { EventId = ev.Id }),
                CreatedAt = now
            }, token: ct);

            _logger.LogInformation(
                "Auto-closed event {Id} ({Title}); scheduled at {Scheduled}",
                ev.Id, ev.Title, ev.ScheduledAt);
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
