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

        if (!string.IsNullOrWhiteSpace(data.BossesText))
        {
            embed.WithDescription($"**Bosses:** {data.BossesText}");
        }

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

        // Mid-set swaps (from reset segments)
        if (data.Swaps != null && data.Swaps.Count > 0)
        {
            var swapSb = new StringBuilder();
            foreach (var swap in data.Swaps)
            {
                swapSb.AppendLine($"**At {swap.FromBossName}:**");
                if (swap.Entries.Count == 0)
                {
                    swapSb.AppendLine("  • (mechanic re-assignment only — no base-role swaps)");
                }
                else
                {
                    foreach (var e in swap.Entries)
                    {
                        var from = string.IsNullOrEmpty(e.FromRole) ? "—" : FormatRole(e.FromRole);
                        var to = string.IsNullOrEmpty(e.ToRole) ? "—" : FormatRole(e.ToRole);
                        swapSb.AppendLine($"  • {e.AccountName}: {from} → {to}");
                    }
                }
            }
            var swapText = swapSb.ToString().TrimEnd();
            if (swapText.Length > 1024) swapText = swapText[..1020] + "...";
            embed.AddField("Mid-set Swaps", swapText);
        }

        // Mechanics field (consolidated)
        var mechBosses = data.PerBoss
            .Where(b => b.Mechanics.Any(m => m.AssignedPlayers.Any(p => p.PlayerId.HasValue)))
            .ToList();
        if (mechBosses.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var boss in mechBosses)
            {
                var lines = new List<string>();
                foreach (var mech in boss.Mechanics)
                {
                    var names = mech.AssignedPlayers
                        .Where(p => p.PlayerId.HasValue)
                        .Select(p => MentionOrName(p.PlayerId, p.AccountName))
                        .ToList();
                    if (names.Count == 0) continue;
                    lines.Add($"  • {mech.Name}: {string.Join(", ", names)}");
                }
                if (lines.Count > 0)
                {
                    var marker = boss.IsResetSegment ? " *(reset)*" : "";
                    sb.AppendLine($"**{boss.BossName}**{marker}");
                    foreach (var line in lines) sb.AppendLine(line);
                }
            }
            var text = sb.ToString().TrimEnd();
            if (!string.IsNullOrEmpty(text))
            {
                if (text.Length > 1024) text = text[..1020] + "...";
                embed.AddField("Mechanics", text);
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
