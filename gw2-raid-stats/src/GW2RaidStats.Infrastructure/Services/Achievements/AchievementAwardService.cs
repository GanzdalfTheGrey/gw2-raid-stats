using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using GW2RaidStats.Infrastructure.Services.Achievements.Checkers;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.Infrastructure.Services.Achievements;

/// <summary>
/// Service for awarding achievements and sending notifications.
/// Handles both player and guild achievements.
/// </summary>
public class AchievementAwardService
{
    private readonly RaidStatsDb _db;
    private readonly ILogger<AchievementAwardService> _logger;

    public AchievementAwardService(
        RaidStatsDb db,
        ILogger<AchievementAwardService> logger)
    {
        _db = db;
        _logger = logger;
    }

    #region Check if Already Awarded

    /// <summary>
    /// Check if a player already has a specific achievement
    /// </summary>
    public async Task<bool> HasAchievementAsync(Guid playerId, string code, CancellationToken ct)
    {
        return await _db.PlayerAchievements
            .AnyAsync(pa => pa.PlayerId == playerId && pa.AchievementCode == code, ct);
    }

    /// <summary>
    /// Check if a guild achievement already exists
    /// </summary>
    public async Task<bool> HasGuildAchievementAsync(string code, CancellationToken ct)
    {
        return await _db.GuildAchievements
            .AnyAsync(ga => ga.AchievementCode == code, ct);
    }

    /// <summary>
    /// Get all earned achievement codes for a player
    /// </summary>
    public async Task<HashSet<string>> GetEarnedCodesAsync(Guid playerId, CancellationToken ct)
    {
        var codes = await _db.PlayerAchievements
            .Where(pa => pa.PlayerId == playerId)
            .Select(pa => pa.AchievementCode)
            .ToListAsync(ct);
        return codes.ToHashSet();
    }

    #endregion

    #region Award Achievements

    /// <summary>
    /// Award a player achievement
    /// </summary>
    /// <param name="playerId">The player to award the achievement to</param>
    /// <param name="code">The achievement code</param>
    /// <param name="context">Optional context data to store with the achievement</param>
    /// <param name="notify">Whether to send a Discord notification</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="achievedAt">When the achievement was earned (defaults to now)</param>
    public async Task AwardAchievementAsync(
        Guid playerId,
        string code,
        object? context,
        bool notify,
        CancellationToken ct,
        DateTimeOffset? achievedAt = null)
    {
        // Double-check we don't already have it
        if (await HasAchievementAsync(playerId, code, ct)) return;

        var achievement = new PlayerAchievementEntity
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            AchievementCode = code,
            AchievedAt = achievedAt ?? DateTimeOffset.UtcNow,
            Context = context != null ? JsonSerializer.Serialize(context) : null,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(achievement, token: ct);

        _logger.LogInformation("Awarded achievement {Code} to player {PlayerId}", code, playerId);

        if (notify)
        {
            await QueueAchievementNotificationAsync(playerId, code, false, ct);
        }
    }

    /// <summary>
    /// Award a guild achievement (or increment count if already awarded)
    /// </summary>
    /// <param name="code">The achievement code</param>
    /// <param name="context">Optional context data to store with the achievement</param>
    /// <param name="notify">Whether to send a Discord notification (only for first time)</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="achievedAt">When the achievement was earned (defaults to now)</param>
    public async Task AwardGuildAchievementAsync(
        string code,
        object? context,
        bool notify,
        CancellationToken ct,
        DateTimeOffset? achievedAt = null)
    {
        var effectiveAchievedAt = achievedAt ?? DateTimeOffset.UtcNow;
        var contextJson = context != null ? JsonSerializer.Serialize(context) : null;

        // Check if achievement already exists
        var existing = await _db.GuildAchievements
            .FirstOrDefaultAsync(ga => ga.AchievementCode == code, ct);

        if (existing != null)
        {
            // Increment count and update last achieved
            await _db.GuildAchievements
                .Where(ga => ga.Id == existing.Id)
                .Set(ga => ga.CompletionCount, existing.CompletionCount + 1)
                .Set(ga => ga.LastAchievedAt, effectiveAchievedAt)
                .Set(ga => ga.LastContext, contextJson)
                .UpdateAsync(ct);

            _logger.LogInformation("Guild achievement {Code} completed again (count: {Count})", code, existing.CompletionCount + 1);
        }
        else
        {
            // First time earning this achievement
            var achievement = new GuildAchievementEntity
            {
                Id = Guid.NewGuid(),
                AchievementCode = code,
                AchievedAt = effectiveAchievedAt,
                Context = contextJson,
                CreatedAt = DateTimeOffset.UtcNow,
                CompletionCount = 1,
                LastAchievedAt = effectiveAchievedAt,
                LastContext = contextJson
            };

            await _db.InsertAsync(achievement, token: ct);

            _logger.LogInformation("Awarded guild achievement {Code}", code);

            if (notify)
            {
                await QueueAchievementNotificationAsync(Guid.Empty, code, true, ct);
            }
        }
    }

    /// <summary>
    /// Process a list of achievement unlocks from checkers
    /// </summary>
    /// <param name="unlocks">List of achievements to award</param>
    /// <param name="notify">Whether to send Discord notifications</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of new achievements awarded</returns>
    public async Task<int> ProcessUnlocksAsync(
        List<AchievementUnlock> unlocks,
        bool notify,
        CancellationToken ct)
    {
        var awarded = 0;

        foreach (var unlock in unlocks)
        {
            if (unlock.IsGuildAchievement)
            {
                // For guild achievements, check if this is the first time
                var isFirst = !await HasGuildAchievementAsync(unlock.Code, ct);
                await AwardGuildAchievementAsync(unlock.Code, unlock.Context, notify && isFirst, ct, unlock.AchievedAt);
                if (isFirst) awarded++;
            }
            else
            {
                // For player achievements, only award if not already earned
                if (!await HasAchievementAsync(unlock.PlayerId!.Value, unlock.Code, ct))
                {
                    await AwardAchievementAsync(unlock.PlayerId!.Value, unlock.Code, unlock.Context, notify, ct, unlock.AchievedAt);
                    awarded++;
                }
            }
        }

        return awarded;
    }

    #endregion

    #region Notifications

    private async Task QueueAchievementNotificationAsync(
        Guid playerId,
        string code,
        bool isGuild,
        CancellationToken ct)
    {
        string? playerName = null;
        if (!isGuild)
        {
            playerName = await _db.Players
                .Where(p => p.Id == playerId)
                .Select(p => p.AccountName)
                .FirstOrDefaultAsync(ct);
        }

        var definition = isGuild
            ? (object?)AchievementDefinitions.Guild.FirstOrDefault(a => a.Code == code)
            : AchievementDefinitions.Personal.FirstOrDefault(a => a.Code == code);

        if (definition == null) return;

        var (name, description) = definition switch
        {
            AchievementDefinition a => (a.Name, a.Description),
            GuildAchievementDefinition g => (g.Name, g.Description),
            _ => ("Unknown", "Unknown achievement")
        };

        var payload = new AchievementPayload(
            playerName,
            code,
            name,
            description,
            isGuild
        );

        var notification = new NotificationQueueEntity
        {
            Id = Guid.NewGuid(),
            NotificationType = "achievement_unlocked",
            Payload = JsonSerializer.Serialize(payload),
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(notification, token: ct);
    }

    #endregion
}

/// <summary>
/// Payload for achievement notification messages
/// </summary>
public record AchievementPayload(
    string? PlayerName,
    string Code,
    string Name,
    string Description,
    bool IsGuild
);
