using Discord;
using Discord.Interactions;
using GW2RaidStats.Infrastructure.Services;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;

namespace GW2RaidStats.DiscordBot.Commands;

public class StatsCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly StatsService _statsService;
    private readonly LeaderboardService _leaderboardService;
    private readonly HtcmProgService _htcmService;
    private readonly IServiceProvider _serviceProvider;

    public StatsCommands(
        StatsService statsService,
        LeaderboardService leaderboardService,
        HtcmProgService htcmService,
        IServiceProvider serviceProvider)
    {
        _statsService = statsService;
        _leaderboardService = leaderboardService;
        _htcmService = htcmService;
        _serviceProvider = serviceProvider;
    }

    [SlashCommand("help", "Show available commands")]
    public async Task HelpAsync()
    {
        var embed = new EmbedBuilder()
            .WithTitle("📖 Available Commands")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp();

        embed.AddField("📊 General Stats",
            "`/stats` - Overall raid statistics\n" +
            "`/session` - Most recent raid session summary\n" +
            "`/recent [count]` - Recent encounters\n" +
            "`/htcm` - Harvest Temple CM progression",
            inline: false);

        embed.AddField("🏆 Leaderboards",
            "`/leaderboard <boss> [cm]` - Top DPS for a boss\n" +
            "`/leaderboard-unique <boss> [cm]` - Top DPS (one per player)\n" +
            "`/boss <boss>` - Stats for a specific boss",
            inline: false);

        embed.AddField("👤 Personal Stats",
            "`/link <account>` - Link your GW2 account\n" +
            "`/unlink` - Unlink your account\n" +
            "`/whoami` - Show your linked account\n" +
            "`/mystats` - Your personal statistics\n" +
            "`/mybossrecords` - Your top DPS on each boss\n" +
            "`/myboonrecords` - Your top boon DPS on each boss",
            inline: false);

        await RespondAsync(embed: embed.Build());
    }

    [SlashCommand("stats", "Show overall raid statistics")]
    public async Task StatsAsync()
    {
        await DeferAsync();

        var stats = await _statsService.GetDashboardStatsAsync();
        var weekly = await _statsService.GetWeeklyHighlightsAsync();

        var embed = new EmbedBuilder()
            .WithTitle("📊 Raid Statistics")
            .WithColor(Color.Teal)
            .WithCurrentTimestamp()
            .AddField("Total Encounters", stats.TotalEncounters.ToString("N0"), inline: true)
            .AddField("Total Kills", stats.TotalKills.ToString("N0"), inline: true)
            .AddField("Success Rate", $"{stats.SuccessRate:F1}%", inline: true)
            .AddField("Active Raiders (Month)", stats.ActiveRaidersThisMonth.ToString(), inline: true)
            .AddField("Raid Hours (Year)", $"{stats.RaidHoursThisYear:F1}h", inline: true)
            .AddField("Total Players", stats.TotalPlayers.ToString(), inline: true);

        // Add weekly highlights
        embed.AddField("\u200B", "**This Week**", inline: false);
        embed.AddField("Encounters", weekly.Encounters.ToString(), inline: true);
        embed.AddField("Kills", weekly.Kills.ToString(), inline: true);

        if (weekly.TopDps != null)
        {
            embed.AddField("Top DPS",
                $"{weekly.TopDps.AccountName} ({weekly.TopDps.Profession})\n{weekly.TopDps.Value:N0} on {weekly.TopDps.BossName}",
                inline: true);
        }

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("session", "Show the most recent raid session summary")]
    public async Task SessionAsync()
    {
        await DeferAsync();

        var session = await _statsService.GetPreviousSessionAsync();
        if (session == null)
        {
            await FollowupAsync("No session data found.", ephemeral: true);
            return;
        }

        var kills = session.Encounters.Count(e => e.Success);
        var wipes = session.Encounters.Count(e => !e.Success);
        var successRate = session.Encounters.Count > 0
            ? (double)kills / session.Encounters.Count * 100
            : 0;

        var embed = new EmbedBuilder()
            .WithTitle("🎮 Last Raid Session")
            .WithColor(Color.Teal)
            .WithTimestamp(session.SessionTime)
            .AddField("Results", $"{kills} kills, {wipes} wipes ({successRate:F0}% success)", inline: true)
            .AddField("Duration", FormatDuration(TimeSpan.FromSeconds(session.TotalTimeSeconds)), inline: true)
            .AddField("Downtime", FormatDuration(TimeSpan.FromSeconds(session.DowntimeSeconds)), inline: true);

        // Add boss list (limit to 15)
        var bossLines = session.Encounters
            .Take(15)
            .Select(e => $"{(e.Success ? "✅" : "❌")} {e.BossName}{(e.IsCM ? " (CM)" : "")} - {FormatDuration(TimeSpan.FromMilliseconds(e.DurationMs))}");

        embed.AddField("Encounters", string.Join("\n", bossLines));

        if (session.Encounters.Count > 15)
        {
            embed.WithFooter($"...and {session.Encounters.Count - 15} more encounters");
        }

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("leaderboard", "Show top DPS for a boss")]
    public async Task LeaderboardAsync(
        [Summary("boss", "Boss name to search for")] string bossSearch,
        [Summary("cm", "Challenge Mode?")] bool isCM = false)
    {
        await DeferAsync();

        // Find matching boss
        var bosses = await _leaderboardService.GetBossListAsync();
        var matchingBoss = bosses
            .FirstOrDefault(b => b.BossName.Contains(bossSearch, StringComparison.OrdinalIgnoreCase));

        if (matchingBoss == null)
        {
            var suggestions = bosses
                .Where(b => b.BossName.Contains(bossSearch, StringComparison.OrdinalIgnoreCase) ||
                           bossSearch.Split(' ').Any(word => b.BossName.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Take(5)
                .Select(b => b.BossName);

            var message = suggestions.Any()
                ? $"Boss not found. Did you mean: {string.Join(", ", suggestions)}?"
                : "Boss not found. Try a different search term.";

            await FollowupAsync(message, ephemeral: true);
            return;
        }

        var leaderboard = await _leaderboardService.GetBossLeaderboardAsync(matchingBoss.TriggerId, isCM, 10);

        var embed = new EmbedBuilder()
            .WithTitle($"🏆 {leaderboard.BossName}{(isCM ? " (CM)" : "")} Leaderboard")
            .WithColor(Color.Gold)
            .WithCurrentTimestamp();

        if (leaderboard.TopDps.Count == 0)
        {
            embed.WithDescription("No kills recorded for this boss yet.");
        }
        else
        {
            var dpsLines = leaderboard.TopDps
                .Select((entry, i) => $"**{i + 1}.** {entry.AccountName} ({entry.Profession}) - **{entry.Dps:N0}** DPS");
            embed.AddField("Top DPS", string.Join("\n", dpsLines));
        }

        if (leaderboard.TopBoonDps.Count > 0)
        {
            var boonLines = leaderboard.TopBoonDps
                .Take(5)
                .Select((entry, i) => $"**{i + 1}.** {entry.AccountName} ({entry.Profession}) - **{entry.Dps:N0}** DPS");
            embed.AddField("Top Boon DPS", string.Join("\n", boonLines));
        }

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("boss", "Show stats for a specific boss")]
    public async Task BossAsync(
        [Summary("boss", "Boss name to search for")] string bossSearch)
    {
        await DeferAsync();

        // Find matching boss
        var bosses = await _leaderboardService.GetBossListAsync();
        var matchingBosses = bosses
            .Where(b => b.BossName.Contains(bossSearch, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingBosses.Count == 0)
        {
            await FollowupAsync("Boss not found. Try a different search term.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"📋 Boss Stats: {bossSearch}")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp();

        foreach (var boss in matchingBosses.Take(5))
        {
            // Get top record for this boss
            var topDps = await _leaderboardService.GetTopDpsForBossAsync(boss.TriggerId, false, 1);
            var topDpsCM = await _leaderboardService.GetTopDpsForBossAsync(boss.TriggerId, true, 1);

            var nmRecord = topDps.FirstOrDefault();
            var cmRecord = topDpsCM.FirstOrDefault();

            var lines = new List<string> { $"**Kills:** {boss.KillCount}" };

            if (nmRecord != null)
            {
                lines.Add($"**NM Record:** {nmRecord.AccountName} - {nmRecord.Dps:N0} DPS");
            }
            if (cmRecord != null)
            {
                lines.Add($"**CM Record:** {cmRecord.AccountName} - {cmRecord.Dps:N0} DPS");
            }

            embed.AddField(boss.BossName, string.Join("\n", lines), inline: true);
        }

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("htcm", "Show Harvest Temple CM progression")]
    public async Task HtcmAsync()
    {
        await DeferAsync();

        var progression = await _htcmService.GetProgressionDataAsync();

        if (progression.TotalPulls == 0)
        {
            await FollowupAsync("No HTCM attempts recorded yet.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("📈 Harvest Temple CM Progression")
            .WithColor(Color.Purple)
            .WithCurrentTimestamp()
            .AddField("Total Pulls", progression.TotalPulls.ToString(), inline: true)
            .AddField("Best Phase", progression.BestPhase ?? "N/A", inline: true)
            .AddField("Best HP%", $"{progression.BestBossHpRemaining:F1}%", inline: true);

        if (progression.FirstAttempt.HasValue)
        {
            embed.AddField("First Attempt", progression.FirstAttempt.Value.ToString("MMM dd, yyyy"), inline: true);
        }

        if (progression.FirstKill.HasValue)
        {
            embed.AddField("🎉 First Kill", progression.FirstKill.Value.ToString("MMM dd, yyyy"), inline: true);
        }

        // Show last few sessions
        var sessions = await _htcmService.GetAvailableSessionsAsync();
        if (sessions.Count > 0)
        {
            var sessionLines = sessions
                .Take(5)
                .Select(s => $"**{s.Date:MMM dd}** - {s.PullCount} pulls, {s.BestPhase} ({s.BestBossHpRemaining:F1}% HP){(s.HasKill ? " ✅" : "")}");

            embed.AddField("Recent Sessions", string.Join("\n", sessionLines));
        }

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("recent", "Show recent encounters")]
    public async Task RecentAsync(
        [Summary("count", "Number of encounters to show (max 15)")] int count = 10)
    {
        await DeferAsync();

        count = Math.Clamp(count, 1, 15);
        var encounters = await _statsService.GetRecentEncountersAsync(count);

        if (encounters.Count == 0)
        {
            await FollowupAsync("No encounters found.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"🕐 Recent Encounters")
            .WithColor(Color.Teal)
            .WithCurrentTimestamp();

        var lines = encounters.Select(e =>
        {
            var status = e.Success ? "✅" : "❌";
            var cm = e.IsCM ? " (CM)" : "";
            var duration = FormatDuration(TimeSpan.FromMilliseconds(e.DurationMs));
            var link = !string.IsNullOrEmpty(e.LogUrl) ? $" [Log]({e.LogUrl})" : "";
            return $"{status} **{e.BossName}**{cm} - {duration}{link}";
        });

        embed.WithDescription(string.Join("\n", lines));

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("leaderboard-unique", "Show top DPS for a boss (one entry per player)")]
    public async Task LeaderboardUniqueAsync(
        [Summary("boss", "Boss name to search for")] string bossSearch,
        [Summary("cm", "Challenge Mode?")] bool isCM = false)
    {
        await DeferAsync();

        // Find matching boss
        var bosses = await _leaderboardService.GetBossListAsync();
        var matchingBoss = bosses
            .FirstOrDefault(b => b.BossName.Contains(bossSearch, StringComparison.OrdinalIgnoreCase));

        if (matchingBoss == null)
        {
            var suggestions = bosses
                .Where(b => b.BossName.Contains(bossSearch, StringComparison.OrdinalIgnoreCase) ||
                           bossSearch.Split(' ').Any(word => b.BossName.Contains(word, StringComparison.OrdinalIgnoreCase)))
                .Take(5)
                .Select(b => b.BossName);

            var message = suggestions.Any()
                ? $"Boss not found. Did you mean: {string.Join(", ", suggestions)}?"
                : "Boss not found. Try a different search term.";

            await FollowupAsync(message, ephemeral: true);
            return;
        }

        var topDps = await _leaderboardService.GetTopDpsForBossUniqueAsync(matchingBoss.TriggerId, isCM, 10);
        var topBoonDps = await _leaderboardService.GetTopBoonDpsForBossAsync(matchingBoss.TriggerId, isCM, 10);

        // Also make boon DPS unique
        var uniqueBoonDps = topBoonDps
            .GroupBy(x => x.AccountName)
            .Select(g => g.First())
            .Take(5)
            .ToList();

        var embed = new EmbedBuilder()
            .WithTitle($"🏆 {matchingBoss.BossName}{(isCM ? " (CM)" : "")} Leaderboard (Unique)")
            .WithColor(Color.Gold)
            .WithCurrentTimestamp()
            .WithFooter("One entry per player");

        if (topDps.Count == 0)
        {
            embed.WithDescription("No kills recorded for this boss yet.");
        }
        else
        {
            var dpsLines = topDps
                .Select((entry, i) => $"**{i + 1}.** {entry.AccountName} ({entry.Profession}) - **{entry.Dps:N0}** DPS");
            embed.AddField("Top DPS", string.Join("\n", dpsLines));
        }

        if (uniqueBoonDps.Count > 0)
        {
            var boonLines = uniqueBoonDps
                .Select((entry, i) => $"**{i + 1}.** {entry.AccountName} ({entry.Profession}) - **{entry.Dps:N0}** DPS");
            embed.AddField("Top Boon DPS", string.Join("\n", boonLines));
        }

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("mybossrecords", "Show your top DPS on each boss")]
    public async Task MyBossRecordsAsync()
    {
        await DeferAsync();

        // Get linked account
        var userId = Context.User.Id;
        var link = await GetUserLinkAsync(userId);

        if (link == null)
        {
            await FollowupAsync("You haven't linked your GW2 account yet. Use `/link <account_name>` first.", ephemeral: true);
            return;
        }

        var records = await _leaderboardService.GetPlayerBossRecordsAsync(link);

        if (records.Count == 0)
        {
            await FollowupAsync("No boss records found for your account.", ephemeral: true);
            return;
        }

        // Group by wing for display
        var byWing = records
            .Where(r => r.Wing.HasValue)
            .GroupBy(r => r.Wing!.Value)
            .OrderBy(g => g.Key);

        var embed = new EmbedBuilder()
            .WithTitle($"🎯 {link}'s Boss Records")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp();

        foreach (var wingGroup in byWing)
        {
            var lines = wingGroup
                .Take(10) // Limit per wing to avoid embed limits
                .Select(r =>
                {
                    var cm = r.IsCM ? " (CM)" : "";
                    return $"**{r.BossName}**{cm}: {r.Dps:N0} DPS ({r.Profession})";
                });

            embed.AddField($"Wing {wingGroup.Key}", string.Join("\n", lines), inline: false);
        }

        // Add non-raid bosses if any
        var nonRaid = records.Where(r => !r.Wing.HasValue).ToList();
        if (nonRaid.Count > 0)
        {
            var lines = nonRaid
                .Take(10)
                .Select(r =>
                {
                    var cm = r.IsCM ? " (CM)" : "";
                    return $"**{r.BossName}**{cm}: {r.Dps:N0} DPS ({r.Profession})";
                });
            embed.AddField("Other", string.Join("\n", lines), inline: false);
        }

        await FollowupAsync(embed: embed.Build());
    }

    [SlashCommand("myboonrecords", "Show your top boon DPS on each boss")]
    public async Task MyBoonRecordsAsync()
    {
        await DeferAsync();

        // Get linked account
        var userId = Context.User.Id;
        var link = await GetUserLinkAsync(userId);

        if (link == null)
        {
            await FollowupAsync("You haven't linked your GW2 account yet. Use `/link <account_name>` first.", ephemeral: true);
            return;
        }

        var records = await _leaderboardService.GetPlayerAllBossRecordsAsync(link, boonDpsOnly: true);

        if (records.Count == 0)
        {
            await FollowupAsync("No boss data found.", ephemeral: true);
            return;
        }

        // Group by wing for display
        var byWing = records
            .Where(r => r.Wing.HasValue)
            .GroupBy(r => r.Wing!.Value)
            .OrderBy(g => g.Key);

        var embed = new EmbedBuilder()
            .WithTitle($"🛡️ {link}'s Boon DPS Records")
            .WithColor(Color.Green)
            .WithCurrentTimestamp();

        foreach (var wingGroup in byWing)
        {
            var lines = wingGroup
                .Take(10)
                .Select(r =>
                {
                    var cm = r.IsCM ? " (CM)" : "";
                    if (!r.HasRecord)
                    {
                        return $"**{r.BossName}**{cm}: *No record*";
                    }
                    return $"**{r.BossName}**{cm}: {r.Dps:N0} DPS ({r.Profession})";
                });

            embed.AddField($"Wing {wingGroup.Key}", string.Join("\n", lines), inline: false);
        }

        await FollowupAsync(embed: embed.Build());
    }

    private async Task<string?> GetUserLinkAsync(ulong discordUserId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GW2RaidStats.Infrastructure.Database.RaidStatsDb>();
        var accountName = await db.DiscordUserLinks
            .InnerJoin(db.Players, (l, p) => l.PlayerId == p.Id, (l, p) => new { l, p })
            .Where(x => x.l.DiscordUserId == (long)discordUserId)
            .Select(x => x.p.AccountName)
            .FirstOrDefaultAsync();

        return accountName;
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
