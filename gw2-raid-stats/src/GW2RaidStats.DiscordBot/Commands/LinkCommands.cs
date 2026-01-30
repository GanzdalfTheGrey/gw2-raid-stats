using Discord;
using Discord.Interactions;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using LinqToDB.Async;

namespace GW2RaidStats.DiscordBot.Commands;

public class LinkCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly RaidStatsDb _db;

    public LinkCommands(RaidStatsDb db)
    {
        _db = db;
    }

    [SlashCommand("link", "Link your Discord account to your GW2 account")]
    public async Task LinkAsync(
        [Summary("account", "Your GW2 account name (e.g., Player.1234)")] string accountName)
    {
        await DeferAsync(ephemeral: true);

        // Check if the GW2 account exists in our database
        var player = await _db.Players
            .FirstOrDefaultAsync(p => p.AccountName.ToLower() == accountName.ToLower());

        if (player == null)
        {
            await FollowupAsync(
                $"Account `{accountName}` not found in our raid logs. Make sure you've participated in at least one logged encounter.",
                ephemeral: true);
            return;
        }

        var discordUserId = (long)Context.User.Id;

        // Check if already linked
        var existingLink = await _db.DiscordUserLinks
            .FirstOrDefaultAsync(l => l.DiscordUserId == discordUserId);

        if (existingLink != null)
        {
            // Update existing link
            existingLink.PlayerId = player.Id;
            existingLink.LinkedAt = DateTimeOffset.UtcNow;
            await _db.UpdateAsync(existingLink);

            await FollowupAsync(
                $"✅ Updated your link to `{player.AccountName}`!",
                ephemeral: true);
        }
        else
        {
            // Create new link
            var link = new DiscordUserLinkEntity
            {
                Id = Guid.NewGuid(),
                DiscordUserId = discordUserId,
                PlayerId = player.Id,
                PersonalBestDmsEnabled = false,
                WallOfShameOptedIn = true,
                LinkedAt = DateTimeOffset.UtcNow
            };

            await _db.InsertAsync(link);

            await FollowupAsync(
                $"✅ Successfully linked your Discord to `{player.AccountName}`!",
                ephemeral: true);
        }
    }

    [SlashCommand("unlink", "Unlink your Discord account from your GW2 account")]
    public async Task UnlinkAsync()
    {
        await DeferAsync(ephemeral: true);

        var discordUserId = (long)Context.User.Id;

        var deleted = await _db.DiscordUserLinks
            .Where(l => l.DiscordUserId == discordUserId)
            .DeleteAsync();

        if (deleted > 0)
        {
            await FollowupAsync("✅ Your Discord account has been unlinked.", ephemeral: true);
        }
        else
        {
            await FollowupAsync("You don't have a linked GW2 account.", ephemeral: true);
        }
    }

    [SlashCommand("whoami", "Check which GW2 account is linked to your Discord")]
    public async Task WhoAmIAsync()
    {
        await DeferAsync(ephemeral: true);

        var discordUserId = (long)Context.User.Id;

        var link = await _db.DiscordUserLinks
            .InnerJoin(_db.Players, (l, p) => l.PlayerId == p.Id, (l, p) => new { l, p })
            .Where(x => x.l.DiscordUserId == discordUserId)
            .FirstOrDefaultAsync();

        if (link == null)
        {
            await FollowupAsync(
                "You haven't linked a GW2 account yet. Use `/link <account>` to link one.",
                ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("🔗 Linked Account")
            .WithColor(Color.Green)
            .AddField("GW2 Account", link.p.AccountName, inline: true)
            .AddField("Linked Since", link.l.LinkedAt.ToString("MMM dd, yyyy"), inline: true)
            .WithCurrentTimestamp();

        await FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    [SlashCommand("mystats", "Show your personal raid stats")]
    public async Task MyStatsAsync()
    {
        await DeferAsync();

        var discordUserId = (long)Context.User.Id;

        // Get linked account
        var link = await _db.DiscordUserLinks
            .InnerJoin(_db.Players, (l, p) => l.PlayerId == p.Id, (l, p) => new { l, p })
            .Where(x => x.l.DiscordUserId == discordUserId)
            .FirstOrDefaultAsync();

        if (link == null)
        {
            await FollowupAsync(
                "You haven't linked a GW2 account yet. Use `/link <account>` to link one.",
                ephemeral: true);
            return;
        }

        // Get player stats
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == link.p.Id)
            .ToListAsync();

        if (playerEncounters.Count == 0)
        {
            await FollowupAsync("No encounters found for your account.", ephemeral: true);
            return;
        }

        var totalEncounters = playerEncounters.Count;
        var totalKills = playerEncounters.Count(x => x.e.Success);
        var avgDps = (int)playerEncounters.Average(x => x.pe.Dps);
        var maxDps = playerEncounters.Max(x => x.pe.Dps);
        var totalDeaths = playerEncounters.Sum(x => x.pe.Deaths);
        var totalDowns = playerEncounters.Sum(x => x.pe.Downs);

        // Get best DPS encounter
        var bestDpsEncounter = playerEncounters
            .OrderByDescending(x => x.pe.Dps)
            .First();

        // Get most played profession
        var topProfession = playerEncounters
            .GroupBy(x => x.pe.Profession)
            .OrderByDescending(g => g.Count())
            .First();

        var embed = new EmbedBuilder()
            .WithTitle($"📊 Stats for {link.p.AccountName}")
            .WithColor(Color.Teal)
            .WithCurrentTimestamp()
            .AddField("Total Encounters", totalEncounters.ToString("N0"), inline: true)
            .AddField("Total Kills", totalKills.ToString("N0"), inline: true)
            .AddField("Kill Rate", $"{(double)totalKills / totalEncounters * 100:F1}%", inline: true)
            .AddField("Average DPS", avgDps.ToString("N0"), inline: true)
            .AddField("Best DPS", $"{maxDps:N0} on {bestDpsEncounter.e.BossName}", inline: true)
            .AddField("Most Played", $"{topProfession.Key} ({topProfession.Count()})", inline: true)
            .AddField("Total Deaths", totalDeaths.ToString("N0"), inline: true)
            .AddField("Total Downs", totalDowns.ToString("N0"), inline: true);

        await FollowupAsync(embed: embed.Build());
    }
}
