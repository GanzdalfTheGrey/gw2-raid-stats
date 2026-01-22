using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Infrastructure.Services;

public class StatsService
{
    private readonly RaidStatsDb _db;
    private readonly IgnoredBossService _ignoredBossService;

    public StatsService(RaidStatsDb db, IgnoredBossService ignoredBossService)
    {
        _db = db;
        _ignoredBossService = ignoredBossService;
    }

    public async Task<DashboardStats> GetDashboardStatsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var startOfMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var startOfYear = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Get ignored bosses for filtering
        var ignoredKeys = await _ignoredBossService.GetIgnoredKeysAsync(ct);

        // Base query excluding ignored bosses
        var baseEncounters = _db.Encounters.AsQueryable();
        if (ignoredKeys.Count > 0)
        {
            // Filter out ignored bosses (can't use HashSet directly in LINQ to SQL)
            var ignoredList = ignoredKeys.ToList();
            baseEncounters = baseEncounters
                .Where(e => !ignoredList.Any(i => i.TriggerId == e.TriggerId && i.IsCM == e.IsCM));
        }

        // Total encounters (excluding ignored)
        var totalEncounters = await baseEncounters.CountAsync(ct);

        // Total kills (successful encounters, excluding ignored)
        var totalKills = await baseEncounters.CountAsync(e => e.Success, ct);

        // Success rate
        var successRate = totalEncounters > 0
            ? Math.Round((double)totalKills / totalEncounters * 100, 1)
            : 0;

        // Active raiders this month (distinct players with encounters this month)
        var activeRaidersQuery = _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.e.EncounterTime >= startOfMonth);

        if (ignoredKeys.Count > 0)
        {
            var ignoredList = ignoredKeys.ToList();
            activeRaidersQuery = activeRaidersQuery
                .Where(x => !ignoredList.Any(i => i.TriggerId == x.e.TriggerId && i.IsCM == x.e.IsCM));
        }

        var activeRaidersThisMonth = await activeRaidersQuery
            .Select(x => x.pe.PlayerId)
            .Distinct()
            .CountAsync(ct);

        // Total raid hours this year (excluding ignored)
        var hoursQuery = baseEncounters.Where(e => e.EncounterTime >= startOfYear);
        var totalDurationMsThisYear = await hoursQuery.SumAsync(e => (long)e.DurationMs, ct);
        var raidHoursThisYear = Math.Round(totalDurationMsThisYear / 1000.0 / 60.0 / 60.0, 1);

        // Total players
        var totalPlayers = await _db.Players.CountAsync(ct);

        return new DashboardStats(
            TotalEncounters: totalEncounters,
            TotalKills: totalKills,
            SuccessRate: successRate,
            ActiveRaidersThisMonth: activeRaidersThisMonth,
            RaidHoursThisYear: raidHoursThisYear,
            TotalPlayers: totalPlayers
        );
    }

    public async Task<List<RecentEncounter>> GetRecentEncountersAsync(int count = 10, CancellationToken ct = default)
    {
        var encounters = await _db.Encounters
            .OrderByDescending(e => e.EncounterTime)
            .Take(count)
            .Select(e => new RecentEncounter(
                e.Id,
                e.BossName,
                e.Success,
                e.IsCM,
                e.EncounterTime,
                e.DurationMs,
                e.LogUrl
            ))
            .ToListAsync(ct);

        return encounters;
    }

    public async Task<WeeklyHighlights> GetWeeklyHighlightsAsync(CancellationToken ct = default)
    {
        var oneWeekAgo = DateTimeOffset.UtcNow.AddDays(-7);

        // Get ignored bosses for filtering
        var ignoredKeys = await _ignoredBossService.GetIgnoredKeysAsync(ct);
        var ignoredList = ignoredKeys.ToList();

        // Get encounters from the past week (excluding ignored)
        var weeklyQuery = _db.Encounters.Where(e => e.EncounterTime >= oneWeekAgo);
        if (ignoredKeys.Count > 0)
        {
            weeklyQuery = weeklyQuery
                .Where(e => !ignoredList.Any(i => i.TriggerId == e.TriggerId && i.IsCM == e.IsCM));
        }

        var weeklyEncounters = await weeklyQuery.CountAsync(ct);
        var weeklyKills = await weeklyQuery.CountAsync(e => e.Success, ct);

        // Top DPS this week (excluding ignored)
        var topDpsQuery = _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.EncounterTime >= oneWeekAgo && x.e.Success);

        if (ignoredKeys.Count > 0)
        {
            topDpsQuery = topDpsQuery
                .Where(x => !ignoredList.Any(i => i.TriggerId == x.e.TriggerId && i.IsCM == x.e.IsCM));
        }

        var topDps = await topDpsQuery
            .OrderByDescending(x => x.pe.Dps)
            .Take(1)
            .Select(x => new TopPerformer(
                x.p.AccountName,
                x.pe.CharacterName,
                x.pe.Profession,
                x.pe.Dps,
                x.e.BossName
            ))
            .FirstOrDefaultAsync(ct);

        return new WeeklyHighlights(
            Encounters: weeklyEncounters,
            Kills: weeklyKills,
            TopDps: topDps
        );
    }
}

public record DashboardStats(
    int TotalEncounters,
    int TotalKills,
    double SuccessRate,
    int ActiveRaidersThisMonth,
    double RaidHoursThisYear,
    int TotalPlayers
);

public record RecentEncounter(
    Guid Id,
    string BossName,
    bool Success,
    bool IsCM,
    DateTimeOffset EncounterTime,
    int DurationMs,
    string? LogUrl
);

public record WeeklyHighlights(
    int Encounters,
    int Kills,
    TopPerformer? TopDps
);

public record TopPerformer(
    string AccountName,
    string CharacterName,
    string Profession,
    int Value,
    string BossName
);
