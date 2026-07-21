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

    private const string NewBestMarker = "⭐";

    private const string NewBestLegend = "⭐ = new best-ever";

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

        // One table per embed (burst gets its three, which share a layout). Embeds give
        // each table its own visual break, which reads far better than stacking several
        // differently-shaped code blocks inside one description.
        var embeds = new List<Embed>
        {
            BuildHeaderEmbed(summary, wallOfShameEnabled),
            BuildBurstEmbed(summary),
            BuildDragonsEmbed(summary),
            BuildOrbsAndRipsEmbed(summary),
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
            .AddField("Best HP", $"{s.BestBossHpRemaining:F1}%", inline: true);

        if (!string.IsNullOrEmpty(_settings.AppUrl))
        {
            embed.WithUrl(_settings.AppUrl);
        }

        var squadLines = s.BurstGroups
            .Where(g => g.SquadDps > 0)
            .Select(g => $"**{g.Name}** {FormatShort(g.SquadDps)} — all-time {FormatShort(g.SquadDpsAllTime)}")
            .ToList();
        if (squadLines.Count > 0)
        {
            embed.AddField("⚔️ Squad Burst (avg DPS)", string.Join("\n", squadLines));
        }

        if (s.Mvdps != null)
        {
            var mvp = new StringBuilder();
            mvp.AppendLine($"**{FormatName(s.Mvdps.AccountName)}** — {s.Mvdps.Score:F1} pts");
            mvp.AppendLine($"burst {s.Mvdps.BurstPoints:F1} · dps {s.Mvdps.DpsPoints:F1} · " +
                           $"orbs {s.Mvdps.OrbPoints:F1} · rips {s.Mvdps.RipPoints:F1}");
            if (s.Mvdps.RunnerUp != null)
            {
                mvp.Append($"runner-up: {FormatName(s.Mvdps.RunnerUp)} ({s.Mvdps.RunnerUpScore:F1})");
            }
            embed.AddField("🏆 MVDPS", mvp.ToString());
        }

        if (wallOfShameEnabled)
        {
            var shameLines = new List<string>();
            if (s.Shame.DebilitatedPlayer != null)
            {
                var pulls = s.Shame.DebilitatedPulls == 1 ? "pull" : "pulls";
                shameLines.Add($"Debilitated in Giants: **{FormatName(s.Shame.DebilitatedPlayer)}** " +
                               $"({s.Shame.DebilitatedPulls} {pulls})");
            }
            if (s.Shame.ChompedPlayer != null)
            {
                shameLines.Add($"Chomped: **{FormatName(s.Shame.ChompedPlayer)}** ({s.Shame.ChompCount})");
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
            body.AppendLine(RenderLines(group.Players, p =>
                $"{WithDps(p.Avg, p.DpsAvg)} avg · top {WithDps(p.Top, p.DpsTop)} · " +
                $"best {WithDps(p.Max, p.DpsMax)}"));
            AppendOverflow(body, group.Players.Count);
        }

        if (body.Length == 0)
        {
            body.Append("_No per-phase data for this session — run a rescan to backfill._");
        }
        else
        {
            body.Append(NewBestLegend);
        }

        return new EmbedBuilder()
            .WithTitle("Burst — total damage, avg | top | max")
            .WithColor(Color.Purple)
            .WithDescription(body.ToString())
            .Build();
    }

    private static Embed BuildDragonsEmbed(HtcmSessionSummary s)
    {
        var body = new StringBuilder();
        body.AppendLine("Jormag · Kralk · Morde · Zhaitan · Soo-Won _(Primordus excluded)_");

        if (s.Dragons.Count > 0)
        {
            body.AppendLine(RenderLines(
                s.Dragons,
                d => $"{FormatShort(d.DamageAvg)} avg · top {FormatShort(d.DamageTop)} · " +
                     $"{FormatShort(d.DpsAvg)} dps · best {FormatShort(d.DpsMax)} dps",
                d => d.AccountName,
                d => d.IsNewBest));
            AppendOverflow(body, s.Dragons.Count);
            body.Append(NewBestLegend);
        }
        else
        {
            body.Append("_No dragon-phase data for this session._");
        }

        return new EmbedBuilder()
            .WithTitle("Combined Dragons — damage & dps")
            .WithColor(Color.Purple)
            .WithDescription(body.ToString())
            .Build();
    }

    private static Embed BuildOrbsAndRipsEmbed(HtcmSessionSummary s)
    {
        var body = new StringBuilder();

        if (s.OrbPushes.Count > 0)
        {
            body.AppendLine("**Orb Pushes**");
            body.AppendLine(RenderLines(
                s.OrbPushes,
                o => $"{o.SessionTotal} tonight · best session {o.BestSessionTotal}",
                o => o.AccountName,
                o => o.IsNewBest));
            AppendOverflow(body, s.OrbPushes.Count);
        }

        if (s.BoonRips.Count > 0)
        {
            body.AppendLine("**Boon Rips**");
            body.AppendLine(RenderLines(s.BoonRips,
                r => $"{r.Avg:F0} avg · top {r.Top:F0} · best {r.Max:F0}"));
            AppendOverflow(body, s.BoonRips.Count);
        }

        if (body.Length == 0)
        {
            body.Append("_No orb-push or boon-rip data for this session._");
        }
        else
        {
            body.Append(NewBestLegend);
        }

        return new EmbedBuilder()
            .WithTitle("Orbs & Rips")
            .WithColor(Color.Purple)
            .WithDescription(body.ToString())
            .Build();
    }

    // Damage with its DPS in parentheses, as a read on how long that burst window ran.
    private static string WithDps(double damage, double? dps) =>
        dps is { } d ? $"{FormatShort(damage)} ({FormatShort(d)})" : FormatShort(damage);

    private static void AppendOverflow(StringBuilder body, int totalRows)
    {
        if (totalRows > MaxRows)
        {
            body.AppendLine($"_+{totalRows - MaxRows} more_");
        }
    }

    // One ranked line per player, in Discord's normal proportional font. Fixed-width
    // code blocks align neatly but render in a monospace face that's noticeably harder
    // to read inside an embed, so the numbers are labelled inline instead of by column.
    private static string RenderLines<T>(
        IReadOnlyList<T> rows,
        Func<T, string> values,
        Func<T, string> accountName,
        Func<T, bool> isNewBest)
    {
        // Rows arrive sorted by their headline metric, so position conveys rank without
        // numbering — and without any inline-code markup, which would reintroduce the
        // monospace face this format exists to avoid.
        var lines = rows
            .Take(MaxRows)
            .Select(r =>
                $"**{FormatName(accountName(r))}** — {values(r)}" +
                (isNewBest(r) ? $" {NewBestMarker}" : ""));

        return string.Join("\n", lines);
    }

    private static string RenderLines(
        IReadOnlyList<HtcmSummaryStatRow> rows, Func<HtcmSummaryStatRow, string> values) =>
        RenderLines(rows, values, r => r.AccountName, r => r.IsNewBest);

    // GW2 account names carry a ".1234" discriminator that adds noise without
    // distinguishing anyone in a guild-only list.
    private static string FormatName(string accountName)
    {
        var dot = accountName.LastIndexOf('.');
        return dot > 0 && accountName[(dot + 1)..].All(char.IsDigit)
            ? accountName[..dot]
            : accountName;
    }

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
