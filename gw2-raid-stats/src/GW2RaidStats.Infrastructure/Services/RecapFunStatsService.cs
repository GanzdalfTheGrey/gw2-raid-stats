using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services;

public class RecapFunStatsService
{
    private readonly RaidStatsDb _db;

    public RecapFunStatsService(RaidStatsDb db)
    {
        _db = db;
    }

    /// <summary>
    /// Get all distinct mechanics from the database with counts
    /// </summary>
    public async Task<List<AvailableMechanic>> GetAvailableMechanicsAsync(CancellationToken ct = default)
    {
        var mechanics = await _db.MechanicEvents
            .GroupBy(m => new { m.MechanicName, m.MechanicFullName, m.Description })
            .Select(g => new AvailableMechanic(
                g.Key.MechanicName,
                g.Key.MechanicFullName,
                g.Key.Description,
                g.Count()
            ))
            .OrderByDescending(m => m.Count)
            .ToListAsync(ct);

        return mechanics;
    }

    /// <summary>
    /// Get all configured fun stats
    /// </summary>
    public async Task<List<RecapFunStatDto>> GetAllAsync(CancellationToken ct = default)
    {
        var stats = await _db.RecapFunStats
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.CreatedAt)
            .ToListAsync(ct);

        return stats.Select(s => new RecapFunStatDto(
            s.Id,
            s.MechanicName,
            s.DisplayTitle,
            s.Description,
            s.IsPositive,
            s.DisplayOrder,
            s.IsEnabled,
            s.CreatedAt
        )).ToList();
    }

    /// <summary>
    /// Get only enabled fun stats for the recap
    /// </summary>
    public async Task<List<RecapFunStatDto>> GetEnabledAsync(CancellationToken ct = default)
    {
        var stats = await _db.RecapFunStats
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.DisplayOrder)
            .ToListAsync(ct);

        return stats.Select(s => new RecapFunStatDto(
            s.Id,
            s.MechanicName,
            s.DisplayTitle,
            s.Description,
            s.IsPositive,
            s.DisplayOrder,
            s.IsEnabled,
            s.CreatedAt
        )).ToList();
    }

    /// <summary>
    /// Add a new fun stat
    /// </summary>
    public async Task<RecapFunStatDto> AddAsync(
        string mechanicName,
        string displayTitle,
        string? description,
        bool isPositive,
        CancellationToken ct = default)
    {
        // Get max display order
        var maxOrder = await _db.RecapFunStats
            .Select(s => (int?)s.DisplayOrder)
            .MaxAsync(ct) ?? -1;

        var entity = new RecapFunStatEntity
        {
            Id = Guid.NewGuid(),
            MechanicName = mechanicName,
            DisplayTitle = displayTitle,
            Description = description,
            IsPositive = isPositive,
            DisplayOrder = maxOrder + 1,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(entity, token: ct);

        return new RecapFunStatDto(
            entity.Id,
            entity.MechanicName,
            entity.DisplayTitle,
            entity.Description,
            entity.IsPositive,
            entity.DisplayOrder,
            entity.IsEnabled,
            entity.CreatedAt
        );
    }

    /// <summary>
    /// Update an existing fun stat
    /// </summary>
    public async Task<bool> UpdateAsync(
        Guid id,
        string displayTitle,
        string? description,
        bool isPositive,
        bool isEnabled,
        CancellationToken ct = default)
    {
        var updated = await _db.RecapFunStats
            .Where(s => s.Id == id)
            .Set(s => s.DisplayTitle, displayTitle)
            .Set(s => s.Description, description)
            .Set(s => s.IsPositive, isPositive)
            .Set(s => s.IsEnabled, isEnabled)
            .Set(s => s.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(ct);

        return updated > 0;
    }

    /// <summary>
    /// Update display order of fun stats
    /// </summary>
    public async Task UpdateOrderAsync(List<Guid> orderedIds, CancellationToken ct = default)
    {
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[i];
            var order = i;
            await _db.RecapFunStats
                .Where(s => s.Id == id)
                .Set(s => s.DisplayOrder, order)
                .Set(s => s.UpdatedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);
        }
    }

    /// <summary>
    /// Remove a fun stat
    /// </summary>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var deleted = await _db.RecapFunStats
            .Where(s => s.Id == id)
            .DeleteAsync(ct);

        return deleted > 0;
    }

    /// <summary>
    /// Get leaderboard for a specific mechanic within a year
    /// </summary>
    public async Task<List<MechanicLeaderboardEntry>> GetMechanicLeaderboardAsync(
        string mechanicName,
        int year,
        HashSet<string> includedAccounts,
        int limit = 10,
        CancellationToken ct = default)
    {
        var yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var yearEnd = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Get player IDs for included accounts
        var playerIds = await _db.Players
            .Where(p => includedAccounts.Contains(p.AccountName))
            .Select(p => p.Id)
            .ToListAsync(ct);

        // Get mechanic counts per player for the year
        var leaderboard = await _db.MechanicEvents
            .InnerJoin(_db.Encounters, (m, e) => m.EncounterId == e.Id, (m, e) => new { m, e })
            .InnerJoin(_db.Players, (x, p) => x.m.PlayerId == p.Id, (x, p) => new { x.m, x.e, p })
            .Where(x => x.m.MechanicName == mechanicName
                && x.e.EncounterTime >= yearStart
                && x.e.EncounterTime < yearEnd
                && x.m.PlayerId != null
                && playerIds.Contains(x.m.PlayerId.Value))
            .GroupBy(x => new { x.p.Id, x.p.AccountName })
            .Select(g => new MechanicLeaderboardEntry(
                g.Key.AccountName,
                g.Count()
            ))
            .OrderByDescending(e => e.Count)
            .Take(limit)
            .ToListAsync(ct);

        return leaderboard;
    }
}

public record AvailableMechanic(
    string MechanicName,
    string? MechanicFullName,
    string? Description,
    int Count
);

public record RecapFunStatDto(
    Guid Id,
    string MechanicName,
    string DisplayTitle,
    string? Description,
    bool IsPositive,
    int DisplayOrder,
    bool IsEnabled,
    DateTimeOffset CreatedAt
);

public record MechanicLeaderboardEntry(
    string AccountName,
    int Count
);
