using System.Text.Json;
using Discord;
using GW2RaidStats.DiscordBot.Services;
using GW2RaidStats.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GW2RaidStats.DiscordBot.Notifications;

public class SessionNotificationHandler : INotificationHandler
{
    private readonly StatsService _statsService;
    private readonly ILogger<SessionNotificationHandler> _logger;
    private readonly DiscordBotSettings _settings;

    public SessionNotificationHandler(
        StatsService statsService,
        IOptions<DiscordBotSettings> settings,
        ILogger<SessionNotificationHandler> logger)
    {
        _statsService = statsService;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendAsync(IMessageChannel channel, string payload, bool wallOfShameEnabled, CancellationToken ct)
    {
        var session = await _statsService.GetPreviousSessionAsync(ct);
        if (session == null)
        {
            _logger.LogWarning("No session data found for notification");
            return;
        }

        var highlights = await _statsService.GetSessionHighlightsAsync(ct);

        var kills = session.Encounters.Count(e => e.Success);
        var wipes = session.Encounters.Count(e => !e.Success);
        var successRate = session.Encounters.Count > 0
            ? (double)kills / session.Encounters.Count * 100
            : 0;

        var embed = new EmbedBuilder()
            .WithTitle("Raid Session Complete")
            .WithColor(Color.Teal)
            .WithTimestamp(session.SessionTime)
            .AddField("Results", $"{kills} kills, {wipes} wipes ({successRate:F0}% success)", inline: true)
            .AddField("Duration", FormatDuration(TimeSpan.FromSeconds(session.TotalTimeSeconds)), inline: true)
            .AddField("Downtime", FormatDuration(TimeSpan.FromSeconds(session.DowntimeSeconds)), inline: true);

        // Add app URL if configured
        if (!string.IsNullOrEmpty(_settings.AppUrl))
        {
            embed.WithUrl(_settings.AppUrl);
        }

        // Add boss list
        var bossLines = session.Encounters
            .Take(15)
            .Select(e => $"{(e.Success ? "✅" : "❌")} {e.BossName}{(e.IsCM ? " (CM)" : "")} - {FormatDuration(TimeSpan.FromMilliseconds(e.DurationMs))}");

        embed.AddField("Encounters", string.Join("\n", bossLines));

        // Records, grouped by type so DPS / Boon DPS / Kill Time are visually distinct.
        // Each group becomes its own embed field; if a single group would exceed Discord's
        // 1024-char field limit, it splits across "(2)", "(3)", ... fields.
        if (highlights.Records.Count > 0)
        {
            string PatchTag(RecordBroken r) => r.IsCurrentPatch ? " *(patch)*" : "";

            var killTimeLines = highlights.Records
                .Where(r => r.RecordType == "Kill Time")
                .Select(r => $"⏱️ **{r.BossName}**{(r.IsCM ? " (CM)" : "")} - {FormatDuration(TimeSpan.FromSeconds(r.NewValue))}{PatchTag(r)}")
                .ToList();
            if (killTimeLines.Count > 0)
            {
                AddRecordsFields(embed, "Kill Time Records!", killTimeLines);
            }

            var dpsLines = highlights.Records
                .Where(r => r.RecordType == "DPS")
                .Select(r => $"⚔️ **{r.BossName}**{(r.IsCM ? " (CM)" : "")} - {r.PlayerName} ({r.Profession}) - {r.NewValue:N0}{PatchTag(r)}")
                .ToList();
            if (dpsLines.Count > 0)
            {
                AddRecordsFields(embed, "📯 TOOT 📯 DPS Records!", dpsLines);
            }

            var boonDpsLines = highlights.Records
                .Where(r => r.RecordType == "Boon DPS")
                .Select(r => $"🛡️ **{r.BossName}**{(r.IsCM ? " (CM)" : "")} - {r.PlayerName} ({r.Profession}) - {r.NewValue:N0}{PatchTag(r)}")
                .ToList();
            if (boonDpsLines.Count > 0)
            {
                AddRecordsFields(embed, "Boon DPS Records!", boonDpsLines);
            }
        }

        // Add milestones if any
        if (highlights.Milestones.Count > 0)
        {
            var milestoneLines = highlights.Milestones.Select(m => $"🎉 {m.Description}");
            embed.AddField("Milestones", string.Join("\n", milestoneLines));
        }

        // Add MVP section
        var mvpStats = await _statsService.GetSessionMvpStatsAsync(ct);
        if (mvpStats != null)
        {
            var mvpLines = new List<string>();
            if (mvpStats.TopDpsPlayer != null)
            {
                mvpLines.Add($"⚔️ Top DPS: **{mvpStats.TopDpsPlayer}** ({mvpStats.TopDpsValue:N0} avg)");
            }
            if (mvpStats.BestBoonDpsPlayer != null)
            {
                mvpLines.Add($"🎵 Best Boon DPS: **{mvpStats.BestBoonDpsPlayer}** ({mvpStats.BestBoonDpsValue:N0} avg)");
            }
            if (mvpStats.BestCcPlayer != null && mvpStats.BestCcValue > 0)
            {
                mvpLines.Add($"💥 Best CC: **{mvpStats.BestCcPlayer}** ({mvpStats.BestCcValue:N0})");
            }
            if (mvpStats.MostRubTimePlayer != null && mvpStats.MostRubTimeSeconds > 0)
            {
                var rubTime = TimeSpan.FromSeconds(mvpStats.MostRubTimeSeconds.Value);
                var rubTimeStr = rubTime.TotalMinutes >= 1
                    ? $"{(int)rubTime.TotalMinutes}m {rubTime.Seconds}s"
                    : $"{rubTime.Seconds}s";
                mvpLines.Add($"🩹 Most Rubs: **{mvpStats.MostRubTimePlayer}** ({rubTimeStr})");
            }
            if (mvpStats.SurvivorPlayer != null)
            {
                mvpLines.Add($"💪 Survivor: **{mvpStats.SurvivorPlayer}** ({mvpStats.SurvivorDeaths} deaths)");
            }
            if (mvpLines.Count > 0)
            {
                embed.AddField("🏆 MVPs", string.Join("\n", mvpLines));
            }
        }

        // Add wall of shame if enabled
        if (wallOfShameEnabled)
        {
            var shameStats = await _statsService.GetSessionShameStatsAsync(ct);
            if (shameStats != null)
            {
                var shameLines = new List<string>();
                if (shameStats.MostFirstDeathsCount > 0)
                {
                    shameLines.Add($"💀 Most First Deaths: **{shameStats.MostFirstDeathsPlayer}** ({shameStats.MostFirstDeathsCount})");
                }
                if (shameStats.MostDownsCount > 0)
                {
                    shameLines.Add($"🦵 Most Downs: **{shameStats.MostDownsPlayer}** ({shameStats.MostDownsCount})");
                }
                if (!string.IsNullOrEmpty(shameStats.LeastCcPlayer))
                {
                    shameLines.Add($"🪶 Least CC: **{shameStats.LeastCcPlayer}** ({shameStats.LeastCcValue:N0})");
                }
                if (shameStats.MostDamageTakenValue > 0)
                {
                    shameLines.Add($"🎯 Most Damage Taken: **{shameStats.MostDamageTakenPlayer}** ({shameStats.MostDamageTakenValue:N0})");
                }
                if (shameLines.Count > 0)
                {
                    embed.AddField("Wall of Shame", string.Join("\n", shameLines));
                }
            }
        }

        await channel.SendMessageAsync(embed: embed.Build());
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{duration.Seconds}s";
    }

    /// <summary>
    /// Adds record lines to the embed as one or more fields, splitting whenever the next line
    /// would push a single field past Discord's 1024-char limit. First field uses baseTitle,
    /// subsequent fields append " (2)", " (3)", etc.
    /// </summary>
    private static void AddRecordsFields(EmbedBuilder embed, string baseTitle, List<string> lines)
    {
        const int FieldLimit = 1024;
        var current = new System.Text.StringBuilder();
        var fieldIndex = 0;

        void Flush()
        {
            if (current.Length == 0) return;
            var name = fieldIndex == 0 ? baseTitle : $"{baseTitle} ({fieldIndex + 1})";
            embed.AddField(name, current.ToString());
            current.Clear();
            fieldIndex++;
        }

        foreach (var line in lines)
        {
            // +1 for the newline separator if we already have content
            var addedLength = current.Length == 0 ? line.Length : line.Length + 1;
            if (current.Length > 0 && current.Length + addedLength > FieldLimit)
            {
                Flush();
            }
            if (current.Length > 0) current.Append('\n');
            current.Append(line);
        }
        Flush();
    }
}

public class RecordNotificationHandler : INotificationHandler
{
    public async Task SendAsync(IMessageChannel channel, string payload, bool wallOfShameEnabled, CancellationToken ct)
    {
        var record = JsonSerializer.Deserialize<RecordPayload>(payload);
        if (record == null) return;

        var title = record.IsCurrentPatch
            ? "📯 *TOOT* New Patch Record!"
            : "📯 *TOOT* New Record!";
        var color = record.IsCurrentPatch ? Color.Teal : Color.Gold;

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithColor(color)
            .WithCurrentTimestamp();

        if (record.RecordType == "Kill Time")
        {
            embed.WithDescription($"**{record.BossName}**{(record.IsCM ? " (CM)" : "")}");
            embed.AddField("New Time", FormatDuration(TimeSpan.FromSeconds(record.NewValue)), inline: true);
            if (record.PreviousValue.HasValue)
            {
                var improvement = record.PreviousValue.Value - record.NewValue;
                embed.AddField("Previous", FormatDuration(TimeSpan.FromSeconds(record.PreviousValue.Value)), inline: true);
                embed.AddField("Improved By", $"-{FormatDuration(TimeSpan.FromSeconds(improvement))}", inline: true);
            }
        }
        else
        {
            embed.WithDescription($"**{record.BossName}**{(record.IsCM ? " (CM)" : "")} - {record.RecordType}");
            embed.AddField("Player", $"{record.PlayerName} ({record.Profession})", inline: true);
            embed.AddField("DPS", $"{record.NewValue:N0}", inline: true);
            if (record.PreviousValue.HasValue)
            {
                var improvement = record.NewValue - record.PreviousValue.Value;
                embed.AddField("Previous", $"{record.PreviousValue.Value:N0}", inline: true);
                embed.AddField("Improved By", $"+{improvement:N0}", inline: true);
            }
        }

        // Add log link if available
        if (!string.IsNullOrEmpty(record.LogUrl))
        {
            embed.WithUrl(record.LogUrl);
        }

        await channel.SendMessageAsync(embed: embed.Build());
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}";
        return $"{duration.Seconds}s";
    }
}

public class MilestoneNotificationHandler : INotificationHandler
{
    public async Task SendAsync(IMessageChannel channel, string payload, bool wallOfShameEnabled, CancellationToken ct)
    {
        var milestone = JsonSerializer.Deserialize<MilestonePayload>(payload);
        if (milestone == null) return;

        var embed = new EmbedBuilder()
            .WithTitle("🎉 Milestone Reached!")
            .WithDescription(milestone.Description)
            .WithColor(Color.Green)
            .WithCurrentTimestamp();

        await channel.SendMessageAsync(embed: embed.Build());
    }
}

public class HtcmProgressNotificationHandler : INotificationHandler
{
    public async Task SendAsync(IMessageChannel channel, string payload, bool wallOfShameEnabled, CancellationToken ct)
    {
        var progress = JsonSerializer.Deserialize<HtcmProgressPayload>(payload);
        if (progress == null) return;

        var embed = new EmbedBuilder()
            .WithTitle("📈 HTCM Progress!")
            .WithColor(Color.Purple)
            .WithCurrentTimestamp();

        if (progress.IsNewBestPhase)
        {
            embed.AddField("New Best Phase", progress.Phase, inline: true);
        }

        if (progress.IsNewBestHp)
        {
            embed.AddField("New Best HP%", $"{progress.BossHpRemaining:F1}%", inline: true);
        }

        embed.AddField("Pull #", progress.PullNumber.ToString(), inline: true);

        await channel.SendMessageAsync(embed: embed.Build());
    }
}

public class Top5NotificationHandler : INotificationHandler
{
    public async Task SendAsync(IMessageChannel channel, string payload, bool wallOfShameEnabled, CancellationToken ct)
    {
        var top5 = JsonSerializer.Deserialize<Top5Payload>(payload);
        if (top5 == null) return;

        var rankEmoji = top5.Rank switch
        {
            2 => "🥈",
            3 => "🥉",
            4 => "4️⃣",
            5 => "5️⃣",
            _ => "🏅"
        };

        var embed = new EmbedBuilder()
            .WithTitle($"{rankEmoji} *toot* Top {top5.Rank}!")
            .WithDescription($"**{top5.BossName}**{(top5.IsCM ? " (CM)" : "")} - {top5.RecordType}")
            .WithColor(Color.LightOrange)
            .WithCurrentTimestamp()
            .AddField("Player", $"{top5.PlayerName} ({top5.Profession})", inline: true)
            .AddField("DPS", $"{top5.Dps:N0}", inline: true)
            .AddField("Rank", $"#{top5.Rank}", inline: true);

        // Add log link if available
        if (!string.IsNullOrEmpty(top5.LogUrl))
        {
            embed.WithUrl(top5.LogUrl);
        }

        await channel.SendMessageAsync(embed: embed.Build());
    }
}

// Payload models for JSON deserialization
public record RecordPayload(
    string RecordType,
    string BossName,
    bool IsCM,
    string? PlayerName,
    string? Profession,
    double NewValue,
    double? PreviousValue,
    string? LogUrl,
    bool IsCurrentPatch = false
);

public record MilestonePayload(
    string Type,
    int Value,
    string Description
);

public record HtcmProgressPayload(
    int PullNumber,
    string Phase,
    decimal BossHpRemaining,
    bool IsNewBestPhase,
    bool IsNewBestHp
);

public record Top5Payload(
    string RecordType,
    string BossName,
    bool IsCM,
    string PlayerName,
    string Profession,
    int Dps,
    int Rank,
    string? LogUrl
);

public class AchievementNotificationHandler : INotificationHandler
{
    public async Task SendAsync(IMessageChannel channel, string payload, bool wallOfShameEnabled, CancellationToken ct)
    {
        var achievement = JsonSerializer.Deserialize<AchievementPayload>(payload);
        if (achievement == null) return;

        var embed = new EmbedBuilder()
            .WithCurrentTimestamp();

        if (achievement.IsGuild)
        {
            embed.WithTitle("🏆 Guild Achievement Unlocked!")
                .WithDescription($"**{achievement.Name}**")
                .WithColor(Color.Purple);
        }
        else
        {
            embed.WithTitle("🎖️ Achievement Unlocked!")
                .WithDescription($"**{achievement.PlayerName}** earned **{achievement.Name}**")
                .WithColor(Color.Gold);
        }

        embed.AddField("Description", achievement.Description, inline: false);

        await channel.SendMessageAsync(embed: embed.Build());
    }
}

public record AchievementPayload(
    string? PlayerName,
    string Code,
    string Name,
    string Description,
    bool IsGuild
);
