using Discord;
using GW2RaidStats.Core.Events;
using GW2RaidStats.Infrastructure.Database.Entities;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.DiscordBot.Notifications;

/// <summary>
/// Renders an event's current state (event row + signups + role slot definitions) as
/// a Discord embed plus an attached button row. Called both for the initial post and
/// for every signup-driven re-render, so the output is purely a function of inputs.
/// </summary>
public static class EventEmbedBuilder
{
    public static (Embed Embed, MessageComponent Components) Build(
        EventEntity ev,
        List<RoleSlot>? roleSlots,
        List<EventSignupRow> signups)
    {
        var cancelled = ev.Status == "Cancelled";

        var title = cancelled ? $"❌ CANCELLED — {ev.Title}" : ev.Title;
        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(cancelled ? Color.Red : Color.Purple)
            .WithCurrentTimestamp();

        if (!string.IsNullOrEmpty(ev.Description))
        {
            embed.WithDescription(ev.Description);
        }

        // Discord auto-localises <t:UNIX:F> (full date) and <t:UNIX:R> (relative).
        var unix = ev.ScheduledAt.ToUnixTimeSeconds();
        embed.AddField("When", $"<t:{unix}:F> (<t:{unix}:R>)", inline: false);

        if (roleSlots != null && roleSlots.Count > 0)
        {
            foreach (var slot in roleSlots)
            {
                var slotSignups = signups.Where(s => s.SlotId == slot.Id && s.Status == "Accepted").ToList();
                var value = slotSignups.Count == 0
                    ? "*(empty)*"
                    : string.Join("\n", slotSignups.Select(FormatSignup));
                embed.AddField($"{slot.Label} ({slotSignups.Count}/{slot.Count})", value, inline: true);
            }
        }
        else
        {
            var accepted = signups.Where(s => s.Status == "Accepted").ToList();
            var value = accepted.Count == 0 ? "*(none yet)*" : string.Join("\n", accepted.Select(FormatSignup));
            embed.AddField($"Signed Up ({accepted.Count})", value, inline: false);
        }

        var reserves = signups.Where(s => s.Status == "Reserve").ToList();
        if (reserves.Count > 0)
        {
            embed.AddField($"Reserve ({reserves.Count})", string.Join("\n", reserves.Select(FormatSignup)), inline: false);
        }

        var acceptedCount = signups.Count(s => s.Status == "Accepted");
        embed.WithFooter($"{acceptedCount} signed up · {reserves.Count} reserve · click a slot to sign up, Drop out to remove yourself");

        var components = BuildComponents(ev, roleSlots, signups, cancelled);
        return (embed.Build(), components);
    }

    private static MessageComponent BuildComponents(
        EventEntity ev,
        List<RoleSlot>? roleSlots,
        List<EventSignupRow> signups,
        bool cancelled)
    {
        var builder = new ComponentBuilder();
        var placed = 0;

        if (roleSlots != null && roleSlots.Count > 0)
        {
            // Slot buttons flow at 5 per row; Reserve and Drop continue on the same row
            // when there's space (no empty rows between slots and actions). Cap slot
            // buttons at 23 so Reserve + Drop always fit under Discord's 25-button cap.
            foreach (var slot in roleSlots)
            {
                if (placed >= 23) break;
                var slotCount = signups.Count(s => s.SlotId == slot.Id && s.Status == "Accepted");
                var isFull = slotCount >= slot.Count;
                builder.WithButton(
                    label: isFull ? $"{slot.Label} ✓" : slot.Label,
                    customId: $"event:{ev.Id}:slot:{slot.Id}",
                    style: ButtonStyle.Primary,
                    row: placed / 5,
                    disabled: cancelled || isFull);
                placed++;
            }
        }
        else
        {
            builder.WithButton(
                label: "Sign Up",
                customId: $"event:{ev.Id}:accept",
                style: ButtonStyle.Success,
                row: placed / 5,
                disabled: cancelled);
            placed++;
        }

        builder.WithButton("Reserve", $"event:{ev.Id}:reserve", ButtonStyle.Secondary, row: placed / 5, disabled: cancelled);
        placed++;

        // Drop only renders when the event has at least one signup — keeps brand-new
        // events visually clean. Discord can't hide a button per-viewer, so users who
        // haven't signed up can still click it; the handler returns a clean ephemeral
        // "You weren't signed up" in that case.
        if (signups.Count > 0)
        {
            builder.WithButton("Drop out", $"event:{ev.Id}:drop", ButtonStyle.Danger, row: placed / 5, disabled: cancelled);
        }

        return builder.Build();
    }

    private static string FormatSignup(EventSignupRow s)
    {
        var mention = $"<@{(ulong)s.DiscordUserId}>";
        return string.IsNullOrEmpty(s.AccountName)
            ? $"• {mention}"
            : $"• {mention} — *{s.AccountName}*";
    }
}
