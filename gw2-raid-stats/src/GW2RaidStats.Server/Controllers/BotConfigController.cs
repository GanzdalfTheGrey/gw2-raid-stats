using GW2RaidStats.Infrastructure.Database;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace GW2RaidStats.Server.Controllers;

// Web admin surface for the per-guild Discord bot config row. Mirrors the /config
// slash commands so commanders can edit channel routing and toggles from the
// dashboard. Guild rows are created by the bot itself on first /config use — this
// controller only edits existing rows.
[ApiController]
[Route("api/admin/bot-config")]
public class BotConfigController : ControllerBase
{
    private readonly RaidStatsDb _db;

    public BotConfigController(RaidStatsDb db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<BotConfigDto>>> GetAll(CancellationToken ct)
    {
        var rows = await _db.DiscordConfigs
            .OrderBy(c => c.GuildName ?? "")
            .ToListAsync(ct);

        return Ok(rows.Select(c => new BotConfigDto(
            c.GuildId,
            c.GuildName,
            c.NotificationChannelId,
            c.NotificationsEnabled,
            c.SquadBuilderChannelId,
            c.EventsChannelId,
            c.WallOfShameEnabled)).ToList());
    }

    [HttpPut("{guildId:long}")]
    public async Task<ActionResult> Update(
        long guildId,
        [FromBody] BotConfigUpdate body,
        CancellationToken ct)
    {
        var config = await _db.DiscordConfigs.FirstOrDefaultAsync(c => c.GuildId == guildId, ct);
        if (config == null) return NotFound();

        config.NotificationChannelId = body.NotificationChannelId;
        config.NotificationsEnabled = body.NotificationsEnabled;
        config.SquadBuilderChannelId = body.SquadBuilderChannelId;
        config.EventsChannelId = body.EventsChannelId;
        config.WallOfShameEnabled = body.WallOfShameEnabled;
        config.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.UpdateAsync(config, token: ct);
        return NoContent();
    }
}

public record BotConfigDto(
    long GuildId,
    string? GuildName,
    long? NotificationChannelId,
    bool NotificationsEnabled,
    long? SquadBuilderChannelId,
    long? EventsChannelId,
    bool WallOfShameEnabled);

public record BotConfigUpdate(
    long? NotificationChannelId,
    bool NotificationsEnabled,
    long? SquadBuilderChannelId,
    long? EventsChannelId,
    bool WallOfShameEnabled);
