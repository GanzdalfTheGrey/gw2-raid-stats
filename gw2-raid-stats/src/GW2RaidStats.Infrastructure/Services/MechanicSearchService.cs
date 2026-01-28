using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Infrastructure.Services;

public class MechanicSearchService
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;

    public MechanicSearchService(RaidStatsDb db, IncludedPlayerService includedPlayerService)
    {
        _db = db;
        _includedPlayerService = includedPlayerService;
    }

    /// <summary>
    /// Get all distinct mechanics from the database with counts
    /// </summary>
    public async Task<List<MechanicInfo>> GetAllMechanicsAsync(CancellationToken ct = default)
    {
        var mechanics = await _db.MechanicEvents
            .GroupBy(m => new { m.MechanicName, m.MechanicFullName, m.Description })
            .Select(g => new MechanicInfo(
                g.Key.MechanicName,
                g.Key.MechanicFullName,
                g.Key.Description,
                g.Count()
            ))
            .OrderByDescending(m => m.TotalCount)
            .ToListAsync(ct);

        return mechanics;
    }

    /// <summary>
    /// Get player leaderboard for a specific mechanic within a date range
    /// Only shows guild members (included players)
    /// </summary>
    public async Task<MechanicSearchResult> SearchMechanicAsync(
        string mechanicName,
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        CancellationToken ct = default)
    {
        // Get mechanic info
        var mechanicInfo = await _db.MechanicEvents
            .Where(m => m.MechanicName == mechanicName)
            .Select(m => new { m.MechanicName, m.MechanicFullName, m.Description })
            .FirstOrDefaultAsync(ct);

        if (mechanicInfo == null)
        {
            return new MechanicSearchResult(
                mechanicName,
                null,
                null,
                fromDate,
                toDate,
                0,
                new List<MechanicPlayerStat>()
            );
        }

        // Get included players (guild members) - only they appear in mechanics stats
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = includedAccounts.ToList();

        // Build query with optional date filters
        var query = _db.MechanicEvents
            .InnerJoin(_db.Encounters, (m, e) => m.EncounterId == e.Id, (m, e) => new { m, e })
            .InnerJoin(_db.Players, (x, p) => x.m.PlayerId == p.Id, (x, p) => new { x.m, x.e, p })
            .Where(x => x.m.MechanicName == mechanicName && x.m.PlayerId != null);

        // Only show guild members
        if (includedList.Count > 0)
        {
            query = query.Where(x => includedList.Contains(x.p.AccountName));
        }

        if (fromDate.HasValue)
        {
            query = query.Where(x => x.e.EncounterTime >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            // Add one day to include the entire end date
            var endDate = toDate.Value.AddDays(1);
            query = query.Where(x => x.e.EncounterTime < endDate);
        }

        // Check if this mechanic has a known ICD for grouping
        var icd = MechanicIcdHelper.GetIcd(mechanicName);

        List<MechanicPlayerStat> playerStats;
        int totalCount;

        if (icd > 0)
        {
            // Fetch individual events and apply ICD grouping in memory
            var events = await query
                .Select(x => new { x.p.Id, x.p.AccountName, x.m.EventTimeMs })
                .ToListAsync(ct);

            // Group by player and apply ICD counting
            var playerCounts = events
                .GroupBy(e => new { e.Id, e.AccountName })
                .Select(g =>
                {
                    var times = g.Select(e => e.EventTimeMs).ToList();
                    return new MechanicPlayerStat(
                        g.Key.AccountName,
                        MechanicIcdHelper.CountWithIcd(times, icd)
                    );
                })
                .OrderByDescending(p => p.Count)
                .ToList();

            playerStats = playerCounts;
            totalCount = playerStats.Sum(p => p.Count);
        }
        else
        {
            // No ICD - use simple count at database level
            playerStats = await query
                .GroupBy(x => new { x.p.Id, x.p.AccountName })
                .Select(g => new MechanicPlayerStat(
                    g.Key.AccountName,
                    g.Count()
                ))
                .OrderByDescending(p => p.Count)
                .ToListAsync(ct);

            totalCount = playerStats.Sum(p => p.Count);
        }

        return new MechanicSearchResult(
            mechanicInfo.MechanicName,
            mechanicInfo.MechanicFullName,
            mechanicInfo.Description,
            fromDate,
            toDate,
            totalCount,
            playerStats
        );
    }
}

public record MechanicInfo(
    string MechanicName,
    string? MechanicFullName,
    string? Description,
    int TotalCount
);

public record MechanicSearchResult(
    string MechanicName,
    string? MechanicFullName,
    string? Description,
    DateTimeOffset? FromDate,
    DateTimeOffset? ToDate,
    int TotalCount,
    List<MechanicPlayerStat> PlayerStats
);

public record MechanicPlayerStat(
    string AccountName,
    int Count
);
