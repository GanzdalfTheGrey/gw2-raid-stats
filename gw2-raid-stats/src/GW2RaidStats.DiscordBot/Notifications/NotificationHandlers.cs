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

        // Add records if any
        if (highlights.Records.Count > 0)
        {
            var recordLines = highlights.Records
                .Take(3)
                .Select(r => r.RecordType == "Kill Time"
                    ? $"⏱️ **{r.BossName}** - {FormatDuration(TimeSpan.FromSeconds(r.NewValue))}"
                    : $"⚔️ **{r.BossName}** - {r.PlayerName} ({r.Profession}) - {r.NewValue:N0} DPS");

            embed.AddField("New Records!", string.Join("\n", recordLines));
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
            if (mvpStats.TopSupportPlayer != null)
            {
                mvpLines.Add($"🛡️ Top Support: **{mvpStats.TopSupportPlayer}** ({mvpStats.TopSupportDps:N0} avg)");
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
                if (shameStats.MostDeathsCount > 0)
                {
                    shameLines.Add($"💀 Most Deaths: **{shameStats.MostDeathsPlayer}** ({shameStats.MostDeathsCount})");
                }
                if (shameStats.MostDownsCount > 0)
                {
                    shameLines.Add($"🦵 Most Downs: **{shameStats.MostDownsPlayer}** ({shameStats.MostDownsCount})");
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
}

public class RecordNotificationHandler : INotificationHandler
{
    public async Task SendAsync(IMessageChannel channel, string payload, bool wallOfShameEnabled, CancellationToken ct)
    {
        var record = JsonSerializer.Deserialize<RecordPayload>(payload);
        if (record == null) return;

        var embed = new EmbedBuilder()
            .WithTitle("📯 *TOOT* New Record!")
            .WithColor(Color.Gold)
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
    string? LogUrl
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
