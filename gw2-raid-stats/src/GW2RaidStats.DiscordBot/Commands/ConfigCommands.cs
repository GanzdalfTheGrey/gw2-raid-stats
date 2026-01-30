using Discord;
using Discord.Interactions;
using GW2RaidStats.DiscordBot.Services;

namespace GW2RaidStats.DiscordBot.Commands;

[Group("config", "Configure GW2 Raid Stats bot settings")]
[RequireUserPermission(GuildPermission.ManageGuild)]
public class ConfigCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly DiscordConfigService _configService;

    public ConfigCommands(DiscordConfigService configService)
    {
        _configService = configService;
    }

    [SlashCommand("notifications", "Set the channel for raid notifications")]
    public async Task SetNotificationsAsync(
        [Summary("channel", "The channel to send notifications to (leave empty to disable)")]
        ITextChannel? channel = null)
    {
        if (channel == null)
        {
            await _configService.DisableNotificationsAsync(Context.Guild.Id);
            await RespondAsync("Notifications have been disabled for this server.", ephemeral: true);
        }
        else
        {
            await _configService.SetNotificationChannelAsync(Context.Guild.Id, channel.Id);
            await RespondAsync($"Notifications will be sent to {channel.Mention}.", ephemeral: true);
        }
    }

    [SlashCommand("shame", "Enable or disable the wall of shame feature")]
    public async Task SetWallOfShameAsync(
        [Summary("enabled", "Whether to enable the wall of shame")]
        bool enabled)
    {
        await _configService.SetWallOfShameAsync(Context.Guild.Id, enabled);
        var status = enabled ? "enabled" : "disabled";
        await RespondAsync($"Wall of shame has been {status}.", ephemeral: true);
    }

    [SlashCommand("status", "Show current bot configuration for this server")]
    public async Task ShowStatusAsync()
    {
        var config = await _configService.GetConfigAsync(Context.Guild.Id);

        var embed = new EmbedBuilder()
            .WithTitle("GW2 Raid Stats Bot Configuration")
            .WithColor(Color.Teal)
            .WithCurrentTimestamp();

        if (config == null)
        {
            embed.WithDescription("No configuration set. Use `/config notifications #channel` to get started.");
        }
        else
        {
            var notificationStatus = config.NotificationsEnabled && config.NotificationChannelId.HasValue
                ? $"Enabled in <#{config.NotificationChannelId}>"
                : "Disabled";

            embed.AddField("Notifications", notificationStatus, inline: true);
            embed.AddField("Wall of Shame", config.WallOfShameEnabled ? "Enabled" : "Disabled", inline: true);
        }

        await RespondAsync(embed: embed.Build(), ephemeral: true);
    }
}
