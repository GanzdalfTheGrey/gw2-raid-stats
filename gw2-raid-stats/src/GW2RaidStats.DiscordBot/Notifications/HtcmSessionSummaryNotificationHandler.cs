using System.Text;
using System.Text.Json;
using Discord;
using GW2RaidStats.DiscordBot.Services;
using GW2RaidStats.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GW2RaidStats.DiscordBot.Notifications;

/// <summary>
/// Posts the HTCM progression summary for a prog night. Player tables are rendered as
/// monospace code blocks inside embed descriptions rather than fields — ten players
/// across six metrics would blow past Discord's 25-field cap.
/// </summary>
public class HtcmSessionSummaryNotificationHandler : INotificationHandler
{
    private readonly HtcmSessionSummaryService _summaryService;
    private readonly DiscordBotSettings _settings;
    private readonly ILogger<HtcmSessionSummaryNotificationHandler> _logger;

    // Discord caps a single message at 6000 characters summed across all its embeds.
    // We stay well under, and split into a second message if a huge roster gets close.
    private const int MessageCharBudget = 5200;

    // Rows per table. A prog night with heavy subbing can produce more names than are
    // useful in a summary; anything dropped is reported rather than silently cut.
    private const int MaxRows = 12;

    private const int NameWidth = 12;

    public HtcmSessionSummaryNotificationHandler(
        HtcmSessionSummaryService summaryService,
        IOptions<DiscordBotSettings> settings,
        ILogger<HtcmSessionSummaryNotificationHandler> logger)
    {
        _summaryService = summaryService;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendAsync(IMessageChannel channel, string payload, bool wallOfShameEnabled, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<HtcmSummaryPayload>(payload);
        if (request == null)
        {
            _logger.LogWarning("HTCM summary notification had an unreadable payload");
            return;
        }

        var summary = await _summaryService.GetSummaryAsync(request.SessionDate, ct);
        if (summary == null)
        {
            _logger.LogWarning("No HTCM session data found for {Date}", request.SessionDate);
            return;
        }

        var embeds = new List<Embed>
        {
            BuildHeaderEmbed(summary, wallOfShameEnabled),
            BuildBurstEmbed(summary),
            BuildDragonsEmbed(summary),
        };

        // Send as one message when it fits the shared 6000-char budget, otherwise split.
        var running = 0;
        var batch = new List<Embed>();
        foreach (var embed in embeds)
        {
            var length = embed.Length;
            if (batch.Count > 0 && running + length > MessageCharBudget)
            {
                await channel.SendMessageAsync(embeds: batch.ToArray());
                batch.Clear();
                running = 0;
            }
            batch.Add(embed);
            running += length;
        }
        if (batch.Count > 0)
        {
            await channel.SendMessageAsync(embeds: batch.ToArray());
        }
    }

    private Embed BuildHeaderEmbed(HtcmSessionSummary s, bool wallOfShameEnabled)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"🐉 HTCM Progress — {s.Date:dddd, MMMM d}")
            .WithColor(Color.Purple)
            .WithCurrentTimestamp()
            .AddField("Pulls", s.PullCount.ToString(), inline: true)
            .AddField("Best Phase", s.BestPhase, inline: true)
            .AddField("Best HP", $"{s.BestBossHpRemaining:F1}%", inline: true)
            .AddField("Fight Time", FormatDuration(s.TotalTime), inline: true)
            .AddField("Avg Pull", FormatDuration(TimeSpan.FromSeconds(s.AverageFightDurationSeconds)), inline: true)
            .AddField("Deaths / Downs", $"{s.TotalDeaths} / {s.TotalDowns}", inline: true);

        if (!string.IsNullOrEmpty(_settings.AppUrl))
        {
            embed.WithUrl(_settings.AppUrl);
        }

        var squadLine = string.Join("  ·  ", s.BurstGroups
            .Where(g => g.SquadDps > 0)
            .Select(g => $"{g.Name} {FormatShort(g.SquadDps)}"));
        if (squadLine.Length > 0)
        {
            embed.AddField("⚔️ Squad Burst (avg DPS)", squadLine);
        }

        if (s.Mvdps != null)
        {
            var mvp = new StringBuilder();
            mvp.AppendLine($"**{s.Mvdps.AccountName}** — {s.Mvdps.Score:F1} pts");
            mvp.AppendLine($"burst {s.Mvdps.BurstPoints:F1} · dps {s.Mvdps.DpsPoints:F1} · " +
                           $"orbs {s.Mvdps.OrbPoints:F1} · rips {s.Mvdps.RipPoints:F1}");
            if (s.Mvdps.RunnerUp != null)
            {
                mvp.Append($"runner-up: {s.Mvdps.RunnerUp} ({s.Mvdps.RunnerUpScore:F1})");
            }
            embed.AddField("🏆 MVDPS", mvp.ToString());
        }

        if (wallOfShameEnabled)
        {
            var shameLines = new List<string>();
            if (s.Shame.DebilitatedPlayer != null)
            {
                var stacks = s.Shame.DebilitatedAvgStacks is { } st
                    ? $" ({st:F1} stacks avg)"
                    : "";
                shameLines.Add($"🥴 Debilitated into Giants: **{s.Shame.DebilitatedPlayer}**\n" +
                               $"   {s.Shame.DebilitatedUptimePct:F0}% uptime{stacks}");
            }
            if (s.Shame.ChompedPlayer != null)
            {
                shameLines.Add($"🦖 Chomped by Primo: **{s.Shame.ChompedPlayer}** ({s.Shame.ChompCount})");
            }
            if (shameLines.Count > 0)
            {
                embed.AddField("💀 Wall of Shame", string.Join("\n", shameLines));
            }
        }

        return embed.Build();
    }

    private static Embed BuildBurstEmbed(HtcmSessionSummary s)
    {
        var body = new StringBuilder();

        foreach (var group in s.BurstGroups)
        {
            if (group.Players.Count == 0) continue;

            var avgDuration = TimeSpan.FromMilliseconds(group.AverageDurationMs);
            body.AppendLine($"**{group.Name}** · {group.PullsReached} pulls · {avgDuration.TotalSeconds:F0}s avg");
            body.AppendLine(RenderTable(
                new[] { "avg", "top", "max" },
                group.Players.Take(MaxRows).Select(p => (
                    p.AccountName,
                    new[] { FormatShort(p.Avg), FormatShort(p.Top), FormatShort(p.Max) },
                    p.IsNewBest))));
            AppendOverflow(body, group.Players.Count);
        }

        if (body.Length == 0)
        {
            body.AppendLine("_No per-phase data for this session — run a rescan to backfill._");
        }
        else
        {
            // Marker is an asterisk rather than an emoji so it doesn't break the
            // fixed-width alignment inside the code block.
            body.Append("`*` = new best-ever");
        }

        return new EmbedBuilder()
            .WithTitle("Burst — Total Damage (avg | top | max)")
            .WithColor(Color.Purple)
            .WithDescription(body.ToString())
            .Build();
    }

    private static Embed BuildDragonsEmbed(HtcmSessionSummary s)
    {
        var body = new StringBuilder();

        if (s.Dragons.Count > 0)
        {
            body.AppendLine("**Combined Dragons** — Jormag · Kralk · Morde · Zhaitan · Soo-Won");
            body.AppendLine("_(Primordus excluded)_");
            body.AppendLine(RenderTable(
                new[] { "dmg avg", "dmg top", "dps avg", "dps max" },
                s.Dragons.Take(MaxRows).Select(d => (
                    d.AccountName,
                    new[]
                    {
                        FormatShort(d.DamageAvg), FormatShort(d.DamageTop),
                        FormatShort(d.DpsAvg), FormatShort(d.DpsMax)
                    },
                    d.IsNewBest))));
            AppendOverflow(body, s.Dragons.Count);
        }

        if (s.OrbPushes.Count > 0)
        {
            body.AppendLine("🔵 **Orb Pushes** (this session | best session)");
            body.AppendLine(RenderTable(
                new[] { "this", "best" },
                s.OrbPushes.Take(MaxRows).Select(o => (
                    o.AccountName,
                    new[] { o.SessionTotal.ToString(), o.BestSessionTotal.ToString() },
                    o.IsNewBest))));
            AppendOverflow(body, s.OrbPushes.Count);
        }

        if (s.BoonRips.Count > 0)
        {
            body.AppendLine("🌀 **Boon Rips** (avg | top | max)");
            body.AppendLine(RenderTable(
                new[] { "avg", "top", "max" },
                s.BoonRips.Take(MaxRows).Select(r => (
                    r.AccountName,
                    new[] { $"{r.Avg:F0}", $"{r.Top:F0}", $"{r.Max:F0}" },
                    r.IsNewBest))));
            AppendOverflow(body, s.BoonRips.Count);
        }

        if (body.Length == 0)
        {
            body.Append("_No dragon, orb or boon-rip data for this session._");
        }

        return new EmbedBuilder()
            .WithTitle("Dragons, Orbs & Rips")
            .WithColor(Color.Purple)
            .WithDescription(body.ToString())
            .Build();
    }

    private static void AppendOverflow(StringBuilder body, int totalRows)
    {
        if (totalRows > MaxRows)
        {
            body.AppendLine($"_+{totalRows - MaxRows} more_");
        }
    }

    // Fixed-width table in a code block. Columns are sized to the widest cell so the
    // whole thing stays under ~48 chars and doesn't wrap on mobile.
    private static string RenderTable(
        string[] headers,
        IEnumerable<(string Name, string[] Values, bool IsNewBest)> rows)
    {
        var rowList = rows.ToList();
        var widths = headers
            .Select((h, i) => Math.Max(h.Length, rowList.Max(r => r.Values[i].Length)))
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("```");
        sb.Append("Player".PadRight(NameWidth));
        for (var i = 0; i < headers.Length; i++)
        {
            sb.Append(' ').Append(headers[i].PadLeft(widths[i]));
        }
        sb.AppendLine();

        foreach (var row in rowList)
        {
            sb.Append(Truncate(row.Name, NameWidth).PadRight(NameWidth));
            for (var i = 0; i < row.Values.Length; i++)
            {
                sb.Append(' ').Append(row.Values[i].PadLeft(widths[i]));
            }
            if (row.IsNewBest) sb.Append(" *");
            sb.AppendLine();
        }

        sb.Append("```");
        return sb.ToString();
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    // 1.24M / 892k / 31.2k / 431 — at most 5 characters wide. Values under 100k keep a
    // decimal so DPS figures don't all collapse to the same rounded thousand.
    private static string FormatShort(double value)
    {
        if (value >= 1_000_000) return $"{value / 1_000_000:F2}M";
        if (value >= 100_000) return $"{value / 1_000:F0}k";
        if (value >= 1_000) return $"{value / 1_000:F1}k";
        return $"{value:F0}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{duration.Seconds}s";
    }
}

public record HtcmSummaryPayload(DateTime SessionDate);
