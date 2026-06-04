using System.Text.Json;
using Discord;
using GW2RaidStats.Core.Events;
using GW2RaidStats.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.DiscordBot.Notifications;

/// <summary>
/// Handles event_post notifications enqueued by the EventsController. Edits the
/// existing Discord message in place if the event was already posted, otherwise
/// posts a new one and stores the channel + message IDs on the event row.
///
/// NotificationProcessor calls this handler once per configured guild; we filter to
/// the event's own guild so a multi-guild bot only posts the event to its owner.
/// </summary>
public class EventPostNotificationHandler : INotificationHandler
{
    private readonly EventService _events;
    private readonly EventSignupService _signups;
    private readonly ILogger<EventPostNotificationHandler> _logger;

    public EventPostNotificationHandler(
        EventService events,
        EventSignupService signups,
        ILogger<EventPostNotificationHandler> logger)
    {
        _events = events;
        _signups = signups;
        _logger = logger;
    }

    public async Task SendAsync(IMessageChannel channel, string payload, bool wallOfShameEnabled, CancellationToken ct)
    {
        EventPostPayload? p;
        try
        {
            p = JsonSerializer.Deserialize<EventPostPayload>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize event post payload");
            return;
        }
        if (p == null) return;

        var ev = await _events.GetAsync(p.EventId, ct);
        if (ev == null)
        {
            _logger.LogWarning("Event {Id} not found for post", p.EventId);
            return;
        }

        // Only post to the event's own guild — NotificationProcessor fans out across
        // every configured guild but events belong to exactly one.
        if (channel is IGuildChannel gc && (long)gc.GuildId != ev.GuildId)
        {
            return;
        }

        var slots = string.IsNullOrEmpty(ev.RoleSlotsJson)
            ? null
            : JsonSerializer.Deserialize<List<RoleSlot>>(ev.RoleSlotsJson);
        var signups = await _signups.ListForEventAsync(ev.Id, ct);
        var (embed, components) = EventEmbedBuilder.Build(ev, slots, signups);

        // Edit in place if the event has already been posted to this channel; fail-soft
        // to a fresh post if the original message was deleted.
        if (ev.MessageId.HasValue && ev.ChannelId.HasValue && (long)channel.Id == ev.ChannelId.Value)
        {
            try
            {
                var existing = await channel.GetMessageAsync((ulong)ev.MessageId.Value);
                if (existing is IUserMessage userMsg)
                {
                    await userMsg.ModifyAsync(m =>
                    {
                        m.Embed = embed;
                        m.Components = components;
                    });
                    _logger.LogInformation("Re-rendered event {Id} message {MsgId}", ev.Id, ev.MessageId);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Edit failed for event {Id}, reposting", ev.Id);
            }
        }

        var sent = await channel.SendMessageAsync(embed: embed, components: components);
        ev.ChannelId = (long)channel.Id;
        ev.MessageId = (long)sent.Id;
        await _events.UpdateAsync(ev, ct);
        _logger.LogInformation("Posted event {Id} to channel {ChannelId} as {MsgId}", ev.Id, channel.Id, sent.Id);
    }
}

public record EventPostPayload(Guid EventId);
