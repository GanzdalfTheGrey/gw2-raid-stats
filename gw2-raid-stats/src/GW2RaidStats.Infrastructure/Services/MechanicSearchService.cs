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
    /// Get all distinct mechanics from the database with counts and boss info
    /// </summary>
    public async Task<List<MechanicInfo>> GetAllMechanicsAsync(CancellationToken ct = default)
    {
        // Get mechanics with their most common boss
        var mechanicsWithBoss = await _db.MechanicEvents
            .InnerJoin(_db.Encounters, (m, e) => m.EncounterId == e.Id, (m, e) => new { m, e })
            .GroupBy(x => new { x.m.MechanicName, x.m.MechanicFullName, x.m.Description, x.e.BossName })
            .Select(g => new
            {
                g.Key.MechanicName,
                g.Key.MechanicFullName,
                g.Key.Description,
                g.Key.BossName,
                Count = g.Count()
            })
            .ToListAsync(ct);

        // Group by mechanic and pick the boss with the most occurrences
        var mechanics = mechanicsWithBoss
            .GroupBy(m => new { m.MechanicName, m.MechanicFullName, m.Description })
            .Select(g =>
            {
                var topBoss = g.OrderByDescending(x => x.Count).First();
                return new MechanicInfo(
                    g.Key.MechanicName,
                    g.Key.MechanicFullName,
                    g.Key.Description,
                    g.Sum(x => x.Count),
                    topBoss.BossName
                );
            })
            .OrderByDescending(m => m.TotalCount)
            .ToList();

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
        // Get mechanic info with most common boss
        var mechanicWithBoss = await _db.MechanicEvents
            .InnerJoin(_db.Encounters, (m, e) => m.EncounterId == e.Id, (m, e) => new { m, e })
            .Where(x => x.m.MechanicName == mechanicName)
            .GroupBy(x => new { x.m.MechanicName, x.m.MechanicFullName, x.m.Description, x.e.BossName })
            .Select(g => new
            {
                g.Key.MechanicName,
                g.Key.MechanicFullName,
                g.Key.Description,
                g.Key.BossName,
                Count = g.Count()
            })
            .ToListAsync(ct);

        if (mechanicWithBoss.Count == 0)
        {
            return new MechanicSearchResult(
                mechanicName,
                null,
                null,
                null,
                fromDate,
                toDate,
                0,
                new List<MechanicPlayerStat>()
            );
        }

        var topBossEntry = mechanicWithBoss.OrderByDescending(x => x.Count).First();
        var mechanicInfo = new
        {
            topBossEntry.MechanicName,
            topBossEntry.MechanicFullName,
            topBossEntry.Description,
            topBossEntry.BossName
        };

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
            mechanicInfo.BossName,
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
    int TotalCount,
    string? BossName
);

public record MechanicSearchResult(
    string MechanicName,
    string? MechanicFullName,
    string? Description,
    string? BossName,
    DateTimeOffset? FromDate,
    DateTimeOffset? ToDate,
    int TotalCount,
    List<MechanicPlayerStat> PlayerStats
);

public record MechanicPlayerStat(
    string AccountName,
    int Count
);
