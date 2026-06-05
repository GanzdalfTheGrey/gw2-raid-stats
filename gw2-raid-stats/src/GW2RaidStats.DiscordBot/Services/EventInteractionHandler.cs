using System.Text.Json;
using Discord;
using Discord.WebSocket;
using GW2RaidStats.Core.Events;
using GW2RaidStats.DiscordBot.Notifications;
using GW2RaidStats.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.DiscordBot.Services;

/// <summary>
/// Handles button clicks on event embeds. Custom IDs are:
///   event:{eventId}:slot:{slotId}   – sign up for a role slot (overflow to Reserve if full)
///   event:{eventId}:accept          – generic accept on a no-roles event
///   event:{eventId}:reserve         – go to Reserve
///   event:{eventId}:drop            – remove signup
/// After mutating, re-renders the source message in place via UpdateAsync and follows
/// up with an ephemeral confirmation so the clicker sees what happened.
/// </summary>
public class EventInteractionHandler
{
    private readonly EventService _events;
    private readonly EventSignupService _signups;
    private readonly ILogger<EventInteractionHandler> _logger;

    public EventInteractionHandler(
        EventService events,
        EventSignupService signups,
        ILogger<EventInteractionHandler> logger)
    {
        _events = events;
        _signups = signups;
        _logger = logger;
    }

    public bool CanHandle(string customId) => customId.StartsWith("event:");

    public async Task HandleAsync(SocketMessageComponent interaction, CancellationToken ct)
    {
        var parts = interaction.Data.CustomId.Split(':');
        // ["event", "{eventId}", action, ...]
        if (parts.Length < 3 || parts[0] != "event")
        {
            await interaction.RespondAsync("Bad interaction id.", ephemeral: true);
            return;
        }
        if (!Guid.TryParse(parts[1], out var eventId))
        {
            await interaction.RespondAsync("Bad event id.", ephemeral: true);
            return;
        }

        var ev = await _events.GetAsync(eventId, ct);
        if (ev == null)
        {
            await interaction.RespondAsync("That event no longer exists.", ephemeral: true);
            return;
        }
        if (ev.Status == "Cancelled")
        {
            await interaction.RespondAsync("This event has been cancelled.", ephemeral: true);
            return;
        }

        var discordUserId = interaction.User.Id;
        string ack;
        try
        {
            switch (parts[2])
            {
                case "slot":
                    if (parts.Length < 4)
                    {
                        await interaction.RespondAsync("Bad slot id.", ephemeral: true);
                        return;
                    }
                    var slotId = parts[3];
                    var slots = string.IsNullOrEmpty(ev.RoleSlotsJson)
                        ? null
                        : JsonSerializer.Deserialize<List<RoleSlot>>(ev.RoleSlotsJson);
                    var slot = slots?.FirstOrDefault(s => s.Id == slotId);
                    if (slot == null)
                    {
                        await interaction.RespondAsync("That role slot no longer exists on this event.", ephemeral: true);
                        return;
                    }
                    var result = await _signups.JoinSlotAsync(eventId, discordUserId, slotId, slot.Count, ct);
                    ack = result.OverflowedToReserve
                        ? $"**{slot.Label}** is full — you've been placed in Reserve."
                        : $"Signed up for **{slot.Label}**.";
                    break;
                case "accept":
                    await _signups.JoinAcceptAsync(eventId, discordUserId, ct);
                    ack = "You're signed up.";
                    break;
                case "reserve":
                    await _signups.JoinReserveAsync(eventId, discordUserId, ct);
                    ack = "You're in Reserve.";
                    break;
                case "drop":
                    var deleted = await _signups.DropAsync(eventId, discordUserId, ct);
                    if (deleted == 0)
                    {
                        await interaction.RespondAsync("You weren't signed up.", ephemeral: true);
                        return;
                    }
                    ack = "You've dropped out.";
                    break;
                default:
                    await interaction.RespondAsync("Unknown action.", ephemeral: true);
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signup mutation failed for event {Id} user {User}", eventId, discordUserId);
            await interaction.RespondAsync("Something went wrong updating your signup.", ephemeral: true);
            return;
        }

        // Re-render the embed in place. Re-load the event in case it was edited mid-flight.
        try
        {
            var updated = await _events.GetAsync(eventId, ct) ?? ev;
            var updatedSlots = string.IsNullOrEmpty(updated.RoleSlotsJson)
                ? null
                : JsonSerializer.Deserialize<List<RoleSlot>>(updated.RoleSlotsJson);
            var updatedSignups = await _signups.ListForEventAsync(eventId, ct);
            var (embed, components) = EventEmbedBuilder.Build(updated, updatedSlots, updatedSignups);

            await interaction.UpdateAsync(m =>
            {
                m.Embed = embed;
                m.Components = components;
            });
            await interaction.FollowupAsync(ack, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to re-render event {Id} after interaction", eventId);
            // Best-effort fallback: ack the user even if the embed didn't update.
            if (!interaction.HasResponded)
            {
                await interaction.RespondAsync(ack, ephemeral: true);
            }
        }
    }
}
