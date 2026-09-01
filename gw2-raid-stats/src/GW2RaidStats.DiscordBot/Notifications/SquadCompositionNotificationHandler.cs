using System.Text;
using System.Text.Json;
using Discord;
using GW2RaidStats.Infrastructure.Database;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.DiscordBot.Notifications;

public class SquadCompositionNotificationHandler : INotificationHandler
{
    private readonly RaidStatsDb _db;
    private readonly ILogger<SquadCompositionNotificationHandler> _logger;

    public SquadCompositionNotificationHandler(
        RaidStatsDb db,
        ILogger<SquadCompositionNotificationHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SendAsync(IMessageChannel channel, string payload, bool wallOfShameEnabled, CancellationToken ct)
    {
        SquadPublishPayload? data;
        try
        {
            data = JsonSerializer.Deserialize<SquadPublishPayload>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize squad composition payload");
            return;
        }
        if (data == null) return;

        // Resolve Discord mentions for any player IDs that have a link
        var allPlayerIds = data.SubGroups
            .SelectMany(s => s.Slots.Where(sl => sl.PlayerId.HasValue).Select(sl => sl.PlayerId!.Value))
            .Concat(data.PerBoss
                .SelectMany(b => b.Mechanics
                    .SelectMany(m => m.AssignedPlayers.Where(p => p.PlayerId.HasValue).Select(p => p.PlayerId!.Value))))
            .Distinct()
            .ToList();

        var links = allPlayerIds.Count == 0
            ? new Dictionary<Guid, long>()
            : (await _db.DiscordUserLinks
                .Where(l => allPlayerIds.Contains(l.PlayerId))
                .ToListAsync(ct))
              .ToDictionary(l => l.PlayerId, l => l.DiscordUserId);

        string MentionOrName(Guid? playerId, string? accountName)
        {
            if (playerId.HasValue && links.TryGetValue(playerId.Value, out var did))
            {
                return $"<@{did}>";
            }
            return accountName ?? "Unknown";
        }

        var embed = new EmbedBuilder()
            .WithTitle(string.IsNullOrWhiteSpace(data.Title) ? "Tonight's Squad" : data.Title)
            .WithColor(Color.Purple)
            .WithCurrentTimestamp();

        // No top-level Bosses description — the Mechanics section below lists every
        // boss in order with its number, so a duplicate comma-separated list is just
        // noise.

        // Sub-group fields
        foreach (var sub in data.SubGroups.OrderBy(s => s.Index))
        {
            var sb = new StringBuilder();
            foreach (var slot in sub.Slots)
            {
                var kindLabel = slot.Kind switch
                {
                    "Heal" => "🟢 Heal",
                    "BoonDps" => "🔵 Boon DPS",
                    "Dps" => "🔴 DPS",
                    _ => slot.Kind
                };

                string nameField;
                if (slot.PlayerId.HasValue && !string.IsNullOrEmpty(slot.AccountName))
                {
                    nameField = MentionOrName(slot.PlayerId, slot.AccountName);
                }
                else if (!string.IsNullOrEmpty(slot.AccountName))
                {
                    // Named non-guildie ("guest") — no PlayerId, so no mention to resolve
                    nameField = slot.AccountName;
                }
                else if (slot.IsPug)
                {
                    nameField = "*PUG*";
                }
                else
                {
                    nameField = "*(empty)*";
                }

                var roleSuffix = string.IsNullOrEmpty(slot.Role) ? "" : $" — {FormatRole(slot.Role)}";
                sb.AppendLine($"{kindLabel}: {nameField}{roleSuffix}");
            }
            embed.AddField($"Sub {sub.Index}", sb.ToString().TrimEnd(), inline: true);
        }

        // Mid-set swaps — one full-width field per reset segment. Splitting per segment
        // keeps each field well under Discord's 1024-char-per-field cap (the old
        // single-field version had to truncate at 1024 and lost the tail of long sets).
        if (data.Swaps != null && data.Swaps.Count > 0)
        {
            // Section header — empty-value field reads as a subheading.
            embed.AddField("Mid-set Swaps", "​", inline: false);
            foreach (var swap in data.Swaps)
            {
                string value;
                if (swap.Entries.Count == 0)
                {
                    value = "*(mechanic re-assignment only — no base-role swaps)*";
                }
                else
                {
                    value = string.Join("\n", swap.Entries.Select(e =>
                    {
                        var from = string.IsNullOrEmpty(e.FromRole) ? "—" : FormatRole(e.FromRole);
                        var to = string.IsNullOrEmpty(e.ToRole) ? "—" : FormatRole(e.ToRole);
                        return $"• {e.AccountName}: {from} → {to}";
                    }));
                }
                if (value.Length > 1024) value = value[..1020] + "...";
                embed.AddField($"At {swap.FromBossName}", value, inline: false);
            }
        }

        // Mechanics — one full-width field per boss, prefixed with the boss's order
        // number in the squad. Splitting one-field-per-boss keeps each value well
        // under Discord's 1024-char cap (the old single-field version truncated after
        // ~14 bosses). Every boss in the squad gets a row, even those with no
        // mechanic assignments — that way the numbering stays consecutive and the
        // commander can see at a glance which bosses still need assignments.
        var mechBosses = data.PerBoss
            .Select((b, idx) => new
            {
                Order = idx + 1,
                b.BossName,
                b.IsResetSegment,
                Lines = b.Mechanics
                    .Select(m => new
                    {
                        m.Name,
                        Names = m.AssignedPlayers
                            .Where(p => p.PlayerId.HasValue)
                            .Select(p => MentionOrName(p.PlayerId, p.AccountName))
                            .ToList()
                    })
                    .Where(m => m.Names.Count > 0)
                    .Select(m => $"• {m.Name}: {string.Join(", ", m.Names)}")
                    .ToList()
            })
            .ToList();

        if (mechBosses.Count > 0)
        {
            embed.AddField("Mechanics", "​", inline: false);
            foreach (var boss in mechBosses)
            {
                // Field names render bold in Discord; no need for **...** inside.
                var name = $"{boss.Order}. {boss.BossName}" + (boss.IsResetSegment ? " (reset)" : "");
                var value = boss.Lines.Count > 0
                    ? string.Join("\n", boss.Lines)
                    : "*(no mechanic assignments)*";
                if (value.Length > 1024) value = value[..1020] + "...";
                embed.AddField(name, value, inline: false);
            }
        }

        // Warnings
        if (data.Warnings.Count > 0)
        {
            embed.AddField("⚠️ Warnings", string.Join("\n", data.Warnings.Select(w => $"• {w}")));
        }

        if (!string.IsNullOrWhiteSpace(data.CommanderName))
        {
            embed.WithFooter($"Built by {data.CommanderName}");
        }

        await channel.SendMessageAsync(embed: embed.Build());
    }

    // Squad-display label drops the Power/Condi variant; the boon/slot pairing is what
    // the commander coordinates around, Power vs Condi is a build detail per boss.
    private static string FormatRole(string? roleEnumName) => roleEnumName switch
    {
        "AlacHeal" => "Alac Heal",
        "QuickHeal" => "Quick Heal",
        "AlacDpsPower" or "AlacDpsCondi" => "Alac DPS",
        "QuickDpsPower" or "QuickDpsCondi" => "Quick DPS",
        "DpsPower" or "DpsCondi" => "DPS",
        _ => roleEnumName ?? ""
    };
}

// Payload mirrors GW2RaidStats.Server.Controllers.SquadPublishPayload (kept in sync manually).
public record SquadPublishPayload(
    string Title,
    string BossesText,
    List<SquadPublishSubGroup> SubGroups,
    int PugDpsCount,
    List<SquadPublishBoss> PerBoss,
    List<string> Warnings,
    List<SquadPublishSwap> Swaps,
    string? CommanderName);

public record SquadPublishSubGroup(int Index, List<SquadPublishSlot> Slots);

public record SquadPublishSlot(
    string Kind,
    string? Role,
    Guid? PlayerId,
    string? AccountName,
    bool IsPug);

public record SquadPublishBoss(string BossName, List<SquadPublishMechanic> Mechanics, bool IsResetSegment);

public record SquadPublishMechanic(string Name, List<SquadPublishMechanicSlot> AssignedPlayers);

public record SquadPublishMechanicSlot(Guid? PlayerId, string? AccountName);

public record SquadPublishSwap(string FromBossName, List<SquadPublishSwapEntry> Entries);

public record SquadPublishSwapEntry(string AccountName, string? FromRole, string? ToRole);
