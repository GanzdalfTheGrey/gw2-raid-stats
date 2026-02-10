using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.DiscordBot.Services;

public partial class PatchNotesService
{
    private readonly RaidStatsDb _db;
    private readonly DiscordSocketClient _client;
    private readonly DiscordConfigService _configService;
    private readonly ILogger<PatchNotesService> _logger;

    // Path to changelog relative to the app
    private const string ChangelogPath = "CHANGELOG.md";

    public PatchNotesService(
        RaidStatsDb db,
        DiscordSocketClient client,
        DiscordConfigService configService,
        ILogger<PatchNotesService> logger)
    {
        _db = db;
        _client = client;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Parse the changelog and get all versions
    /// </summary>
    public List<VersionEntry> ParseChangelog()
    {
        var versions = new List<VersionEntry>();

        if (!File.Exists(ChangelogPath))
        {
            _logger.LogWarning("Changelog file not found at {Path}", ChangelogPath);
            return versions;
        }

        var content = File.ReadAllText(ChangelogPath);
        var versionMatches = VersionHeaderRegex().Matches(content);

        for (int i = 0; i < versionMatches.Count; i++)
        {
            var match = versionMatches[i];
            var version = match.Groups[1].Value;
            var date = match.Groups[2].Value;

            // Get content between this version and the next (or end of file)
            var startIndex = match.Index + match.Length;
            var endIndex = i + 1 < versionMatches.Count
                ? versionMatches[i + 1].Index
                : content.Length;

            var notes = content[startIndex..endIndex].Trim();

            versions.Add(new VersionEntry(version, date, notes));
        }

        return versions;
    }

    /// <summary>
    /// Get the current (latest) version from the changelog
    /// </summary>
    public VersionEntry? GetCurrentVersion()
    {
        return ParseChangelog().FirstOrDefault();
    }

    /// <summary>
    /// Get a specific version's notes
    /// </summary>
    public VersionEntry? GetVersion(string version)
    {
        return ParseChangelog().FirstOrDefault(v =>
            v.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get the last broadcast version from the database
    /// </summary>
    public async Task<string?> GetLastBroadcastVersionAsync(CancellationToken ct = default)
    {
        var last = await _db.AppVersions
            .OrderByDescending(v => v.BroadcastAt)
            .FirstOrDefaultAsync(ct);

        return last?.Version;
    }

    /// <summary>
    /// Check if there's a new version and broadcast it
    /// </summary>
    public async Task<bool> CheckAndBroadcastNewVersionAsync(CancellationToken ct = default)
    {
        var currentVersion = GetCurrentVersion();
        if (currentVersion == null)
        {
            _logger.LogWarning("No versions found in changelog");
            return false;
        }

        var lastBroadcast = await GetLastBroadcastVersionAsync(ct);

        if (lastBroadcast != null && lastBroadcast == currentVersion.Version)
        {
            _logger.LogDebug("Version {Version} already broadcast", currentVersion.Version);
            return false;
        }

        _logger.LogInformation("New version detected: {Version} (last broadcast: {LastBroadcast})",
            currentVersion.Version, lastBroadcast ?? "none");

        await BroadcastPatchNotesAsync(currentVersion, ct);
        return true;
    }

    /// <summary>
    /// Broadcast patch notes to all configured guilds
    /// </summary>
    public async Task BroadcastPatchNotesAsync(VersionEntry version, CancellationToken ct = default)
    {
        var configs = await _configService.GetAllEnabledConfigsAsync(ct);

        if (configs.Count == 0)
        {
            _logger.LogInformation("No guilds configured for notifications, skipping broadcast");
            return;
        }

        var embed = BuildPatchNotesEmbed(version);
        var successCount = 0;

        foreach (var config in configs)
        {
            try
            {
                if (config.NotificationChannelId == null) continue;

                var channel = await _client.GetChannelAsync((ulong)config.NotificationChannelId);
                if (channel is ITextChannel textChannel)
                {
                    await textChannel.SendMessageAsync(embed: embed);
                    successCount++;
                    _logger.LogDebug("Sent patch notes to guild {GuildId}", config.GuildId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send patch notes to guild {GuildId}", config.GuildId);
            }
        }

        // Record the broadcast
        await _db.InsertAsync(new AppVersionEntity
        {
            Id = Guid.NewGuid(),
            Version = version.Version,
            BroadcastAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        }, token: ct);

        _logger.LogInformation("Broadcast patch notes v{Version} to {Count} guilds", version.Version, successCount);
    }

    /// <summary>
    /// Build a Discord embed for patch notes
    /// </summary>
    public Embed BuildPatchNotesEmbed(VersionEntry version)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"GW2 Raid Stats v{version.Version}")
            .WithDescription(FormatNotesForDiscord(version.Notes))
            .WithColor(Color.Blue)
            .WithFooter($"Released {version.Date}")
            .WithCurrentTimestamp();

        return embed.Build();
    }

    /// <summary>
    /// Format changelog markdown for Discord
    /// </summary>
    private static string FormatNotesForDiscord(string notes)
    {
        // Discord supports most markdown, but we may need to adjust some things
        // Limit length for embed description (4096 max)
        if (notes.Length > 4000)
        {
            notes = notes[..3997] + "...";
        }

        return notes;
    }

    /// <summary>
    /// Get version history (all broadcast versions)
    /// </summary>
    public async Task<List<AppVersionEntity>> GetVersionHistoryAsync(int limit = 10, CancellationToken ct = default)
    {
        return await _db.AppVersions
            .OrderByDescending(v => v.BroadcastAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    [GeneratedRegex(@"## \[(\d+\.\d+\.\d+)\] - (\d{4}-\d{2}-\d{2})", RegexOptions.Multiline)]
    private static partial Regex VersionHeaderRegex();
}

public record VersionEntry(string Version, string Date, string Notes);
