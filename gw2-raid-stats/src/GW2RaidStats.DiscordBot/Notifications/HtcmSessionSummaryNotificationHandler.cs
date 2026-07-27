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

    // Rows per table. A prog night with heavy subbing can produce more names than are
    // useful in a summary; anything dropped is reported rather than silently cut.
    private const int MaxRows = 12;

    private const int NameWidth = 12;

    // Marker is an asterisk rather than an emoji so it doesn't break the fixed-width
    // alignment inside the code block.
    private const string NewBestLegend = "`*` = new best-ever";

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

        // Collapsed by default: post just the header (squad totals, highlights, shame) with
        // a button. The per-player tables and deeper shame are heavy, so they're revealed
        // on demand — ephemerally to the clicker — by HtcmSummaryInteractionHandler, keyed
        // off the session date encoded in the button id.
        var button = new ComponentBuilder()
            .WithButton("Full breakdown", $"{ExpandCustomIdPrefix}{summary.Date:yyyy-MM-dd}",
                ButtonStyle.Secondary, new Emoji("📊"))
            .Build();

        await channel.SendMessageAsync(
            embed: BuildHeaderEmbed(summary, wallOfShameEnabled),
            components: button);
    }

    // Custom-id prefix routed to HtcmSummaryInteractionHandler; the remainder is the
    // session date (yyyy-MM-dd).
    public const string ExpandCustomIdPrefix = "htcm:expand:";

    // The per-player tables plus the deeper shame breakdown, for the expanded view. Shame
    // detail is gated on the guild's Wall of Shame toggle just like the collapsed header.
    public static IReadOnlyList<Embed> BuildDetailEmbeds(HtcmSessionSummary summary, bool wallOfShameEnabled)
    {
        var embeds = new List<Embed>
        {
            BuildBurstEmbed(summary),
        };
        var cookiesShames = BuildCookiesShamesEmbed(summary);
        if (cookiesShames != null) embeds.Add(cookiesShames);
        embeds.Add(BuildDragonsEmbed(summary));
        embeds.Add(BuildOrbsAndRipsEmbed(summary));
        if (wallOfShameEnabled)
        {
            var shameDetail = BuildShameDetailEmbed(summary.Shame);
            if (shameDetail != null) embeds.Add(shameDetail);
        }
        return embeds;
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

        // Squad average DPS for each burst group vs its target, plus the combined dragons
        // (which shows an all-time average instead of a target).
        var squadLines = s.BurstGroups
            .Where(g => g.SquadDps > 0)
            .Select(g => $"**{g.Name}** {FormatShort(g.SquadDps)} — target {FormatShort(g.SquadTarget)}")
            .ToList();
        if (s.DragonSquadDps > 0)
        {
            squadLines.Add($"**Dragons** {FormatShort(s.DragonSquadDps)} — all-time {FormatShort(s.DragonSquadDpsAllTime)}");
        }
        if (squadLines.Count > 0)
        {
            embed.AddField("⚔️ Squad DPS (avg)", string.Join("\n", squadLines));
        }

        var goodLines = BuildHighlightLines(s.Highlights);
        if (goodLines.Count > 0)
        {
            embed.AddField("✨ Doing It Right", string.Join("\n", goodLines));
        }

        if (wallOfShameEnabled)
        {
            var shameLines = new List<string>();
            ShameLeaderLine(shameLines, "First Death", s.Shame.FirstDeathRanking, "pull", "pulls");
            ShameLeaderLine(shameLines, "Debilitated in Giants", s.Shame.DebilRanking, "pull", "pulls");
            ShameLeaderLine(shameLines, "Chomped", s.Shame.ChompRanking);
            ShameLeaderLine(shameLines, "Shockwaved", s.Shame.ShockwaveRanking);
            ShameLeaderLine(shameLines, "Bad Reds", s.Shame.RedsRanking);
            if (s.Shame.GiantsMiss is { } miss)
            {
                shameLines.Add($"Giants Miss: **{FormatName(miss.AccountName)}** " +
                               $"({FormatShort(miss.AvgDps)}, {FormatMargin(miss.AvgDps - miss.TargetDps)})");
            }
            if (shameLines.Count > 0)
            {
                embed.AddField("💀 Wall of Shame", string.Join("\n", shameLines));
            }
        }

        return embed.Build();
    }

    // One Wall of Shame line for a category's leader. When several players tie for the top
    // count it reads "Multiple" instead of naming one arbitrarily. unit is optional — bare
    // count when omitted (e.g. "Chomped: X (4)").
    private static void ShameLeaderLine(
        List<string> lines, string label, List<HtcmShameRank> ranking,
        string? unitSingular = null, string? unitPlural = null)
    {
        if (ranking.Count == 0) return;

        var top = ranking[0].Count;
        var tied = ranking.Count(r => r.Count == top);
        var name = tied > 1 ? "Multiple" : FormatName(ranking[0].AccountName);
        var unit = unitSingular == null ? "" : " " + (top == 1 ? unitSingular : unitPlural);
        lines.Add($"{label}: **{name}** ({top}{unit})");
    }

    public static Embed BuildBurstEmbed(HtcmSessionSummary s)
    {
        var body = new StringBuilder();

        foreach (var group in s.BurstGroups)
        {
            if (group.Players.Count == 0) continue;

            // Only Giants carries per-player targets, so it gets a target column alongside
            // avg. Who beat / missed target is broken out into the Cookies & Shames section.
            var isGiants = group.Name == "Giants";
            var headers = isGiants ? new[] { "avg (dps)", "target" } : new[] { "avg (dps)" };

            var avgDuration = TimeSpan.FromMilliseconds(group.AverageDurationMs);
            var failedNote = group.FailedPulls > 0 ? $" · {group.FailedPulls} failed" : "";
            body.AppendLine($"**{group.Name}** · {group.PullsReached} pulls{failedNote} · {avgDuration.TotalSeconds:F0}s avg");
            body.AppendLine(RenderTable(
                headers,
                group.Players.Take(MaxRows).Select(p => (
                    p.AccountName,
                    isGiants
                        ? new[] { WithDps(p.Avg, p.DpsAvg), GiantsTargetCell(p) }
                        : new[] { WithDps(p.Avg, p.DpsAvg) },
                    p.IsNewBest))));
            AppendOverflow(body, group.Players.Count);

            // Repeat the header's squad-vs-target so it can be checked without scrolling up.
            var mark = group.SquadDps >= group.SquadTarget ? "✅" : "❌";
            body.AppendLine($"Squad {FormatShort(group.SquadDps)} / target {FormatShort(group.SquadTarget)} {mark}");
            body.AppendLine();
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
            .WithTitle("Burst — total damage, avg (dps)")
            .WithColor(Color.Purple)
            .WithDescription(body.ToString())
            .Build();
    }

    // Per burst group, a fixed-width table (one row per targeted player) so it lines up at a
    // glance: session-avg DPS, its status vs target (cookie / spec / shame), then how many
    // of tonight's pulls landed in each bucket. Rendered in a code block, so no emoji — they
    // break monospace alignment. Group-agnostic: any group with target rows renders, so
    // adding Timecaster/Saltspray targets later needs no change. Null when nothing has any.
    public static Embed? BuildCookiesShamesEmbed(HtcmSessionSummary s)
    {
        var body = new StringBuilder();

        foreach (var group in s.BurstGroups)
        {
            if (group.Targets.Count == 0) continue;

            // Counts are over completed-phase pulls only; failed = reached but wiped in the
            // phase. A player's ck+sp+sh sums to the completed pulls they were in.
            var failedNote = group.FailedPulls > 0 ? $" · {group.FailedPulls} failed" : "";
            body.AppendLine($"**{group.Name}** — {group.PullsReached} completed{failedNote} · target {FormatShort(group.SquadTarget)}");
            body.AppendLine(RenderTable(
                new[] { "avg", "target", "ck", "sp", "sh" },
                group.Targets.Select(r => (
                    r.AccountName,
                    new[]
                    {
                        FormatShort(r.AvgDps),
                        FormatShort(r.TargetDps),
                        r.CookiePulls.ToString(),
                        r.SpecPulls.ToString(),
                        r.ShamePulls.ToString(),
                    },
                    false))));
            body.AppendLine();
        }

        if (body.Length == 0) return null;

        body.Append("ck/sp/sh pulls = cookie/spec/shame (avg vs target ±10k)");

        return new EmbedBuilder()
            .WithTitle("🍪 Cookies & 💀 Shames")
            .WithColor(Color.Gold)
            .WithDescription(body.ToString())
            .Build();
    }

    public static Embed BuildDragonsEmbed(HtcmSessionSummary s)
    {
        var body = new StringBuilder();
        body.AppendLine("Jormag · Kralk · Morde · Zhaitan · Soo-Won _(Primordus excluded)_");

        if (s.Dragons.Count > 0)
        {
            body.AppendLine(RenderTable(
                new[] { "dmg avg", "dmg top", "dps avg" },
                s.Dragons.Take(MaxRows).Select(d => (
                    d.AccountName,
                    new[]
                    {
                        FormatShort(d.DamageAvg), FormatShort(d.DamageTop),
                        FormatShort(d.DpsAvg)
                    },
                    d.IsNewBest))));
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

    public static Embed BuildOrbsAndRipsEmbed(HtcmSessionSummary s)
    {
        var body = new StringBuilder();

        if (s.OrbPushes.Count > 0)
        {
            body.AppendLine("**Orb Pushes**");
            body.AppendLine(RenderTable(
                new[] { "pushes" },
                s.OrbPushes.Take(MaxRows).Select(o => (
                    o.AccountName,
                    new[] { o.SessionTotal.ToString() },
                    o.IsNewBest))));
            AppendOverflow(body, s.OrbPushes.Count);
        }

        if (s.BoonRips.Count > 0)
        {
            body.AppendLine("**Boon Rips**");
            body.AppendLine(RenderTable(
                new[] { "avg", "top" },
                s.BoonRips.Take(MaxRows).Select(r => (
                    r.AccountName,
                    new[] { $"{r.Avg:F0}", $"{r.Top:F0}" },
                    r.IsNewBest))));
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

    // The good-play callouts, one line each, dropping any category with no data. The
    // positive mirror of the Wall of Shame — who did the little things right tonight.
    private static List<string> BuildHighlightLines(HtcmSummaryHighlights h)
    {
        var lines = new List<string>();

        void Add(HighlightEntry? e, string emoji, string label, Func<HighlightEntry, string> value)
        {
            if (e != null) lines.Add($"{emoji} {label}: **{FormatName(e.AccountName)}** ({value(e)})");
        }

        Add(h.BurstKing, "🔥", "Burst", e => FormatShort(e.Value));
        Add(h.DragonDps, "🐉", "Dragon DPS", e => FormatShort(e.Value));
        Add(h.BoonRock, "🎵", "Boons", e => $"{e.Label} {e.Value:F0}%");
        Add(h.OrbMaster, "🔵", "Orbs", e => $"{e.Value:F0}");
        Add(h.FieldMedic, "🚑", "Medic", e => $"{e.Value:F0} rezzes");
        Add(h.BoonRips, "🌀", "Rips", e => $"{e.Value:F0}");
        Add(h.MostCc, "💥", "CC", e => FormatShort(e.Value));
        if (h.GiantsCookie is { } c)
        {
            lines.Add($"🍪 Giants: **{FormatName(c.AccountName)}** " +
                      $"({FormatShort(c.AvgDps)}, {FormatMargin(c.AvgDps - c.TargetDps)})");
        }

        return lines;
    }

    // Signed margin vs a Giants target, e.g. +17.0k / −12.0k.
    private static string FormatMargin(int delta) =>
        (delta >= 0 ? "+" : "−") + FormatShort(Math.Abs(delta));

    // Giants target cell — the player's DPS target, or "-" for healers (no target).
    private static string GiantsTargetCell(HtcmSummaryStatRow p) =>
        p.TargetDps is { } t ? FormatShort(t) : "-";

    // Full per-category shame rankings for the expanded view — the whole board, not just
    // the single worst. Null when every category is empty (nothing to show).
    private static Embed? BuildShameDetailEmbed(HtcmSummaryShame shame)
    {
        var body = new StringBuilder();

        void Section(string title, List<HtcmShameRank> ranking, string unitSingular, string unitPlural)
        {
            if (ranking.Count == 0) return;
            body.AppendLine($"**{title}**");
            foreach (var r in ranking.Take(MaxRows))
            {
                var unit = r.Count == 1 ? unitSingular : unitPlural;
                body.AppendLine($"{FormatName(r.AccountName)} — {r.Count} {unit}");
            }
            body.AppendLine();
        }

        Section("First Death", shame.FirstDeathRanking, "pull", "pulls");
        Section("Debilitated in Giants", shame.DebilRanking, "pull", "pulls");
        Section("Chomped", shame.ChompRanking, "hit", "hits");
        Section("Shockwaved", shame.ShockwaveRanking, "hit", "hits");
        Section("Bad Reds", shame.RedsRanking, "red", "reds");

        if (body.Length == 0) return null;

        return new EmbedBuilder()
            .WithTitle("💀 Wall of Shame — full breakdown")
            .WithColor(Color.DarkRed)
            .WithDescription(body.ToString().TrimEnd())
            .Build();
    }

    // Damage with its DPS in parentheses. DPS drops the decimal here purely to keep the
    // burst table inside a phone's code-block width.
    private static string WithDps(double damage, double? dps) =>
        dps is { } d ? $"{FormatShort(damage)} ({FormatDpsCompact(d)})" : FormatShort(damage);

    private static string FormatDpsCompact(double dps) =>
        dps >= 1_000 ? $"{dps / 1_000:F0}k" : $"{dps:F0}";

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
            sb.Append(FormatName(row.Name).PadRight(NameWidth));
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

    // GW2 account names carry a ".1234" discriminator that eats the column width and
    // pushes real names into mid-word truncation. Drop it — the name alone identifies
    // the player well enough in a guild-only table.
    private static string FormatName(string accountName)
    {
        var name = accountName;
        var dot = name.LastIndexOf('.');
        if (dot > 0 && name[(dot + 1)..].All(char.IsDigit))
        {
            name = name[..dot];
        }
        return name.Length <= NameWidth ? name : name[..NameWidth];
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
