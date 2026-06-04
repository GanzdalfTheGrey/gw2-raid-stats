using Discord.Interactions;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using LinqToDB.Async;

namespace GW2RaidStats.DiscordBot.Commands;

[Group("events", "Personal event preferences")]
public class EventCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly RaidStatsDb _db;

    public EventCommands(RaidStatsDb db)
    {
        _db = db;
    }

    [SlashCommand("reminders", "Get a DM 30 minutes before events you're signed up for")]
    public async Task RemindersAsync(
        [Summary("enabled", "Whether to receive reminder DMs")]
        bool enabled)
    {
        var userId = (long)Context.User.Id;
        var existing = await _db.EventReminderPreferences
            .FirstOrDefaultAsync(p => p.DiscordUserId == userId);
        var now = DateTimeOffset.UtcNow;
        if (existing == null)
        {
            await _db.InsertAsync(new EventReminderPreferenceEntity
            {
                DiscordUserId = userId,
                Enabled = enabled,
                UpdatedAt = now
            });
        }
        else
        {
            existing.Enabled = enabled;
            existing.UpdatedAt = now;
            await _db.UpdateAsync(existing);
        }

        var msg = enabled
            ? "Event reminder DMs **on** — you'll get a DM 30 minutes before each event you've accepted."
            : "Event reminder DMs **off**.";
        await RespondAsync(msg, ephemeral: true);
    }

    [SlashCommand("reminders-status", "Show your current event reminder preference")]
    public async Task RemindersStatusAsync()
    {
        var userId = (long)Context.User.Id;
        var prefs = await _db.EventReminderPreferences
            .FirstOrDefaultAsync(p => p.DiscordUserId == userId);
        var status = prefs == null || !prefs.Enabled
            ? "**Off**. Use `/events reminders enabled:true` to turn on."
            : "**On** — DM 30 minutes before each accepted event.";
        await RespondAsync($"Your event reminders: {status}", ephemeral: true);
    }
}
