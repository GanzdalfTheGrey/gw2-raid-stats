using System.Globalization;
using Discord;
using Discord.WebSocket;
using GW2RaidStats.DiscordBot.Notifications;
using GW2RaidStats.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.DiscordBot.Services;

/// <summary>
/// Handles the "Full breakdown" button on an HTCM summary. Custom id is
/// <c>htcm:expand:{yyyy-MM-dd}</c>. Rebuilds the summary for that session date and replies
/// with the per-player detail tables plus the deeper shame breakdown — ephemerally, so the
/// clicker gets the depth without cluttering the channel. Stateless: everything reconstructs
/// from the date, so the button keeps working across bot restarts.
/// </summary>
public class HtcmSummaryInteractionHandler
{
    private readonly HtcmSessionSummaryService _summaryService;
    private readonly DiscordConfigService _configService;
    private readonly ILogger<HtcmSummaryInteractionHandler> _logger;

    public HtcmSummaryInteractionHandler(
        HtcmSessionSummaryService summaryService,
        DiscordConfigService configService,
        ILogger<HtcmSummaryInteractionHandler> logger)
    {
        _summaryService = summaryService;
        _configService = configService;
        _logger = logger;
    }

    public bool CanHandle(string customId) =>
        customId.StartsWith(HtcmSessionSummaryNotificationHandler.ExpandCustomIdPrefix);

    public async Task HandleAsync(SocketMessageComponent interaction, CancellationToken ct)
    {
        var dateText = interaction.Data.CustomId
            .Substring(HtcmSessionSummaryNotificationHandler.ExpandCustomIdPrefix.Length);

        if (!DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var sessionDate))
        {
            await interaction.RespondAsync("Bad breakdown id.", ephemeral: true);
            return;
        }

        // Rebuilding queries the DB, which can exceed Discord's 3-second ack window, so
        // defer first and follow up.
        await interaction.DeferAsync(ephemeral: true);

        var summary = await _summaryService.GetSummaryAsync(sessionDate, ct);
        if (summary == null)
        {
            await interaction.FollowupAsync("That session's data is no longer available.", ephemeral: true);
            return;
        }

        // Respect the guild's Wall of Shame toggle for the deeper shame breakdown, same as
        // the collapsed post. Absent config = disabled.
        var wallOfShame = interaction.GuildId is { } guildId
            && (await _configService.GetConfigAsync(guildId, ct))?.WallOfShameEnabled == true;

        var embeds = HtcmSessionSummaryNotificationHandler.BuildDetailEmbeds(summary, wallOfShame);
        await interaction.FollowupAsync(embeds: embeds.ToArray(), ephemeral: true);

        _logger.LogDebug("Expanded HTCM breakdown for {Date} to {User}", sessionDate, interaction.User.Id);
    }
}
