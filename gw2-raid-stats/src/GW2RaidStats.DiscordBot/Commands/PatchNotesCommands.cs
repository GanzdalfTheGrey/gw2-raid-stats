using Discord;
using Discord.Interactions;
using GW2RaidStats.DiscordBot.Services;

namespace GW2RaidStats.DiscordBot.Commands;

public class PatchNotesCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly PatchNotesService _patchNotesService;

    public PatchNotesCommands(PatchNotesService patchNotesService)
    {
        _patchNotesService = patchNotesService;
    }

    [SlashCommand("patchnotes", "View the latest patch notes")]
    public async Task ViewPatchNotesAsync(
        [Summary("version", "Specific version to view (e.g., 1.0.0). Leave empty for latest.")]
        string? version = null)
    {
        VersionEntry? entry;

        if (string.IsNullOrWhiteSpace(version))
        {
            entry = _patchNotesService.GetCurrentVersion();
        }
        else
        {
            entry = _patchNotesService.GetVersion(version);
        }

        if (entry == null)
        {
            await RespondAsync(
                version == null
                    ? "No patch notes found."
                    : $"Version {version} not found.",
                ephemeral: true);
            return;
        }

        var embed = _patchNotesService.BuildPatchNotesEmbed(entry);
        await RespondAsync(embed: embed);
    }

    [SlashCommand("versions", "View version history")]
    public async Task ViewVersionHistoryAsync()
    {
        var versions = _patchNotesService.ParseChangelog();

        if (versions.Count == 0)
        {
            await RespondAsync("No versions found.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("GW2 Raid Stats Version History")
            .WithColor(Color.Blue)
            .WithCurrentTimestamp();

        var description = string.Join("\n", versions.Take(10).Select(v =>
            $"**v{v.Version}** - {v.Date}"));

        embed.WithDescription(description);

        if (versions.Count > 10)
        {
            embed.WithFooter($"Showing 10 of {versions.Count} versions. Use /patchnotes <version> to view details.");
        }

        await RespondAsync(embed: embed.Build());
    }
}
