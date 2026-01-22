using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services;

public class IncludedPlayerService
{
    private readonly RaidStatsDb _db;
    private readonly SettingsService _settingsService;

    public IncludedPlayerService(RaidStatsDb db, SettingsService settingsService)
    {
        _db = db;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Get all manually included players
    /// </summary>
    public async Task<List<IncludedPlayerDto>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.IncludedPlayers
            .OrderBy(p => p.AccountName)
            .Select(p => new IncludedPlayerDto(
                p.Id,
                p.AccountName,
                p.Reason,
                p.CreatedAt
            ))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Get set of included account names (both manually included and auto-included by threshold)
    /// </summary>
    public async Task<HashSet<string>> GetIncludedAccountNamesAsync(CancellationToken ct = default)
    {
        // Get manually included players
        var manuallyIncluded = await _db.IncludedPlayers
            .Select(p => p.AccountName)
            .ToListAsync(ct);

        // Get auto-include threshold
        var threshold = await _settingsService.GetAutoIncludeThresholdAsync(ct);

        // Get players with encounters >= threshold
        var autoIncluded = await _db.PlayerEncounters
            .GroupBy(pe => pe.PlayerId)
            .Where(g => g.Count() >= threshold)
            .Select(g => g.Key)
            .ToListAsync(ct);

        // Get the account names for auto-included players
        var autoIncludedNames = await _db.Players
            .Where(p => autoIncluded.Contains(p.Id))
            .Select(p => p.AccountName)
            .ToListAsync(ct);

        // Combine both lists
        var allIncluded = new HashSet<string>(manuallyIncluded, StringComparer.OrdinalIgnoreCase);
        foreach (var name in autoIncludedNames)
        {
            allIncluded.Add(name);
        }

        return allIncluded;
    }

    /// <summary>
    /// Get all players with their encounter counts, sorted by encounters descending
    /// Players already manually included are marked
    /// </summary>
    public async Task<List<AvailablePlayerDto>> GetAvailablePlayersAsync(CancellationToken ct = default)
    {
        var manuallyIncluded = (await _db.IncludedPlayers
            .Select(p => p.AccountName)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var threshold = await _settingsService.GetAutoIncludeThresholdAsync(ct);

        // Get all players with their encounter counts
        var players = await _db.Players
            .LeftJoin(_db.PlayerEncounters, (p, pe) => p.Id == pe.PlayerId, (p, pe) => new { p, pe })
            .GroupBy(x => new { x.p.Id, x.p.AccountName, x.p.FirstSeen })
            .Select(g => new
            {
                g.Key.Id,
                g.Key.AccountName,
                g.Key.FirstSeen,
                EncounterCount = g.Count()
            })
            .OrderByDescending(p => p.EncounterCount)
            .ToListAsync(ct);

        return players
            .Where(p => !manuallyIncluded.Contains(p.AccountName))
            .Select(p => new AvailablePlayerDto(
                p.Id,
                p.AccountName,
                p.FirstSeen,
                p.EncounterCount,
                p.EncounterCount >= threshold // Auto-included based on threshold
            ))
            .ToList();
    }

    /// <summary>
    /// Add a player to the included list manually
    /// </summary>
    public async Task<IncludedPlayerDto> AddAsync(string accountName, string? reason, CancellationToken ct = default)
    {
        var entity = new IncludedPlayerEntity
        {
            Id = Guid.NewGuid(),
            AccountName = accountName,
            Reason = reason,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(entity, token: ct);

        return new IncludedPlayerDto(
            entity.Id,
            entity.AccountName,
            entity.Reason,
            entity.CreatedAt
        );
    }

    /// <summary>
    /// Remove a player from the included list
    /// </summary>
    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var deleted = await _db.IncludedPlayers
            .Where(p => p.Id == id)
            .DeleteAsync(ct);

        return deleted > 0;
    }

    /// <summary>
    /// Check if an account is included (either manually or by threshold)
    /// </summary>
    public async Task<bool> IsIncludedAsync(string accountName, CancellationToken ct = default)
    {
        var included = await GetIncludedAccountNamesAsync(ct);
        return included.Contains(accountName);
    }
}

public record IncludedPlayerDto(
    Guid Id,
    string AccountName,
    string? Reason,
    DateTimeOffset CreatedAt
);

public record AvailablePlayerDto(
    Guid Id,
    string AccountName,
    DateTimeOffset FirstSeen,
    int EncounterCount,
    bool IsAutoIncluded
);
