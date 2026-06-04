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
            embed.AddField($"Accepted ({accepted.Count})", value, inline: false);
        }

        var reserves = signups.Where(s => s.Status == "Reserve").ToList();
        if (reserves.Count > 0)
        {
            embed.AddField($"Reserve ({reserves.Count})", string.Join("\n", reserves.Select(FormatSignup)), inline: false);
        }

        var acceptedCount = signups.Count(s => s.Status == "Accepted");
        embed.WithFooter($"{acceptedCount} accepted · {reserves.Count} reserve · click a slot to sign up, Drop out to remove yourself");

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

        if (roleSlots != null && roleSlots.Count > 0)
        {
            // Slot buttons fill rows 0..3 at 5 buttons per row. Discord caps at 25
            // buttons total; we reserve row 4 for Reserve/Drop so cap slot buttons at 20.
            var placed = 0;
            foreach (var slot in roleSlots)
            {
                if (placed >= 20) break;
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
                label: "Accept",
                customId: $"event:{ev.Id}:accept",
                style: ButtonStyle.Success,
                row: 0,
                disabled: cancelled);
        }

        // Reserve + Drop pinned to row 4 so they stay findable regardless of slot count.
        builder.WithButton("Reserve", $"event:{ev.Id}:reserve", ButtonStyle.Secondary, row: 4, disabled: cancelled);
        builder.WithButton("Drop out", $"event:{ev.Id}:drop", ButtonStyle.Danger, row: 4, disabled: cancelled);

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
