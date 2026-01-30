using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.DiscordBot.Services;

public class DiscordConfigService
{
    private readonly RaidStatsDb _db;
    private readonly ILogger<DiscordConfigService> _logger;

    public DiscordConfigService(RaidStatsDb db, ILogger<DiscordConfigService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<DiscordConfigEntity?> GetConfigAsync(ulong guildId, CancellationToken ct = default)
    {
        return await _db.DiscordConfigs
            .FirstOrDefaultAsync(c => c.GuildId == (long)guildId, ct);
    }

    public async Task<DiscordConfigEntity> GetOrCreateConfigAsync(ulong guildId, string? guildName = null, CancellationToken ct = default)
    {
        var existing = await GetConfigAsync(guildId, ct);
        if (existing != null)
        {
            // Update guild name if changed
            if (guildName != null && existing.GuildName != guildName)
            {
                existing.GuildName = guildName;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.UpdateAsync(existing, token: ct);
            }
            return existing;
        }

        var config = new DiscordConfigEntity
        {
            Id = Guid.NewGuid(),
            GuildId = (long)guildId,
            GuildName = guildName,
            NotificationsEnabled = false,
            WallOfShameEnabled = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(config, token: ct);
        _logger.LogInformation("Created config for guild {GuildId} ({GuildName})", guildId, guildName);

        return config;
    }

    public async Task SetNotificationChannelAsync(ulong guildId, ulong channelId, CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigAsync(guildId, ct: ct);
        config.NotificationChannelId = (long)channelId;
        config.NotificationsEnabled = true;
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.UpdateAsync(config, token: ct);

        _logger.LogInformation("Set notification channel for guild {GuildId} to {ChannelId}", guildId, channelId);
    }

    public async Task DisableNotificationsAsync(ulong guildId, CancellationToken ct = default)
    {
        var config = await GetConfigAsync(guildId, ct);
        if (config != null)
        {
            config.NotificationsEnabled = false;
            config.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.UpdateAsync(config, token: ct);

            _logger.LogInformation("Disabled notifications for guild {GuildId}", guildId);
        }
    }

    public async Task SetWallOfShameAsync(ulong guildId, bool enabled, CancellationToken ct = default)
    {
        var config = await GetOrCreateConfigAsync(guildId, ct: ct);
        config.WallOfShameEnabled = enabled;
        config.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.UpdateAsync(config, token: ct);

        _logger.LogInformation("Set wall of shame for guild {GuildId} to {Enabled}", guildId, enabled);
    }

    public async Task<List<DiscordConfigEntity>> GetAllEnabledConfigsAsync(CancellationToken ct = default)
    {
        return await _db.DiscordConfigs
            .Where(c => c.NotificationsEnabled && c.NotificationChannelId != null)
            .ToListAsync(ct);
    }
}
