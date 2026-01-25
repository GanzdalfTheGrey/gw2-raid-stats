using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services;

public class IgnoredBossService
{
    private readonly RaidStatsDb _db;

    public IgnoredBossService(RaidStatsDb db)
    {
        _db = db;
    }

    /// <summary>
    /// Get all ignored bosses
    /// </summary>
    public async Task<List<IgnoredBossDto>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.IgnoredBosses
            .OrderBy(b => b.BossName)
            .ThenBy(b => b.IsCM)
            .Select(b => new IgnoredBossDto(
                b.Id,
                b.TriggerId,
                b.BossName,
                b.IsCM,
                b.Reason,
                b.CreatedAt
            ))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Get all ignored trigger ID + CM combinations for filtering
    /// </summary>
    public async Task<HashSet<(int TriggerId, bool IsCM)>> GetIgnoredKeysAsync(CancellationToken ct = default)
    {
        var ignored = await _db.IgnoredBosses
            .Select(b => new { b.TriggerId, b.IsCM })
            .ToListAsync(ct);

        return ignored.Select(b => (b.TriggerId, b.IsCM)).ToHashSet();
    }

    /// <summary>
    /// Get available bosses that can be ignored (from encounters table)
    /// </summary>
    public async Task<List<AvailableBossDto>> GetAvailableBossesAsync(CancellationToken ct = default)
    {
        var ignored = await GetIgnoredKeysAsync(ct);

        var bosses = await _db.Encounters
            .GroupBy(e => new { e.TriggerId, e.BossName, e.IsCM })
            .Select(g => new
            {
                g.Key.TriggerId,
                g.Key.BossName,
                g.Key.IsCM,
                TotalCount = g.Count(),
                SuccessCount = g.Count(e => e.Success)
            })
            .OrderBy(b => b.BossName)
            .ThenBy(b => b.IsCM)
            .ToListAsync(ct);

        return bosses
            .Where(b => !ignored.Contains((b.TriggerId, b.IsCM)))
            .Select(b => new AvailableBossDto(
                b.TriggerId,
                b.BossName,
                b.IsCM,
                b.TotalCount,
                b.SuccessCount
            ))
            .ToList();
    }

    /// <summary>
    /// Add a boss to the ignored list
    /// </summary>
    public async Task<IgnoredBossDto> AddAsync(int triggerId, string bossName, bool isCM, string? reason, CancellationToken ct = default)
    {
        var entity = new IgnoredBossEntity
        {
            Id = Guid.NewGuid(),
            TriggerId = triggerId,
            BossName = bossName,
            IsCM = isCM,
            Reason = reason,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(entity, token: ct);

        return new IgnoredBossDto(
            entity.Id,
            entity.TriggerId,
            entity.BossName,
            entity.IsCM,
            entity.Reason,
            entity.CreatedAt
        );
    }

    /// <summary>
    /// Remove a boss from the ignored list
    /// </summary>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var deleted = await _db.IgnoredBosses
            .Where(b => b.Id == id)
            .DeleteAsync(ct);

        return deleted > 0;
    }
}

public record IgnoredBossDto(
    Guid Id,
    int TriggerId,
    string BossName,
    bool IsCM,
    string? Reason,
    DateTimeOffset CreatedAt
);

public record AvailableBossDto(
    int TriggerId,
    string BossName,
    bool IsCM,
    int TotalCount,
    int SuccessCount
)
{
    public decimal SuccessRate => TotalCount > 0 ? (decimal)SuccessCount / TotalCount * 100 : 0;
}
