using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Infrastructure.Services;

public class BossStatsService
{
    private readonly RaidStatsDb _db;
    private readonly IgnoredBossService _ignoredBossService;
    private readonly IncludedPlayerService _includedPlayerService;

    public BossStatsService(RaidStatsDb db, IgnoredBossService ignoredBossService, IncludedPlayerService includedPlayerService)
    {
        _db = db;
        _ignoredBossService = ignoredBossService;
        _includedPlayerService = includedPlayerService;
    }

    public async Task<List<BossSummary>> GetAllBossesAsync(bool includeIgnored = false, CancellationToken ct = default)
    {
        var ignoredBosses = includeIgnored
            ? new HashSet<(int, bool)>()
            : await _ignoredBossService.GetIgnoredKeysAsync(ct);

        var encounters = await _db.Encounters.ToListAsync(ct);

        var bosses = encounters
            .Where(e => includeIgnored || !ignoredBosses.Contains((e.TriggerId, e.IsCM)))
            .GroupBy(e => new { e.TriggerId, e.BossName, e.IsCM, e.Wing })
            .Select(g => new BossSummary(
                g.Key.TriggerId,
                g.Key.BossName,
                g.Key.IsCM,
                g.Key.Wing,
                g.Count(),
                g.Count(e => e.Success),
                g.Count(e => !e.Success),
                g.Count() > 0 ? (decimal)g.Count(e => e.Success) / g.Count() * 100 : 0,
                g.Where(e => e.Success).Any()
                    ? g.Where(e => e.Success).Min(e => e.DurationMs) / 1000.0
                    : null,
                g.Where(e => e.Success).Any()
                    ? g.Where(e => e.Success).Average(e => e.DurationMs) / 1000.0
                    : null,
                g.Max(e => e.EncounterTime),
                g.First().IconUrl,
                ignoredBosses.Contains((g.Key.TriggerId, g.Key.IsCM))
            ))
            .OrderBy(b => b.Wing ?? 99)
            .ThenBy(b => b.BossName)
            .ThenBy(b => b.IsCM)
            .ToList();

        return bosses;
    }

    public async Task<BossDetail?> GetBossDetailAsync(int triggerId, bool isCM, CancellationToken ct = default)
    {
        var encounters = await _db.Encounters
            .Where(e => e.TriggerId == triggerId && e.IsCM == isCM)
            .OrderByDescending(e => e.EncounterTime)
            .ToListAsync(ct);

        if (encounters.Count == 0) return null;

        var first = encounters.First();

        var recentEncounters = encounters
            .Take(10)
            .Select(e => new BossEncounter(
                e.Id,
                e.Success,
                e.DurationMs / 1000.0,
                e.EncounterTime,
                e.RecordedBy,
                e.LogUrl
            ))
            .ToList();

        // Get top DPS for this boss (guild members only)
        var encounterIds = encounters.Where(e => e.Success).Select(e => e.Id).ToList();
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = includedAccounts.ToList();

        var topDpsQuery = _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId))
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p });

        // Filter to guild members only if there are included players configured
        if (includedList.Count > 0)
        {
            topDpsQuery = topDpsQuery.Where(x => includedList.Contains(x.p.AccountName));
        }

        var topDps = await topDpsQuery
            .OrderByDescending(x => x.pe.Dps)
            .Take(5)
            .Select(x => new BossTopDps(
                x.p.AccountName,
                x.pe.Dps,
                x.pe.Profession
            ))
            .ToListAsync(ct);

        return new BossDetail(
            triggerId,
            first.BossName,
            isCM,
            first.Wing,
            first.IconUrl,
            encounters.Count,
            encounters.Count(e => e.Success),
            encounters.Count(e => !e.Success),
            encounters.Count > 0 ? (decimal)encounters.Count(e => e.Success) / encounters.Count * 100 : 0,
            encounters.Where(e => e.Success).Any()
                ? encounters.Where(e => e.Success).Min(e => e.DurationMs) / 1000.0
                : null,
            encounters.Where(e => e.Success).Any()
                ? encounters.Where(e => e.Success).Average(e => e.DurationMs) / 1000.0
                : null,
            recentEncounters,
            topDps
        );
    }
}

public record BossSummary(
    int TriggerId,
    string BossName,
    bool IsCM,
    int? Wing,
    int TotalEncounters,
    int Kills,
    int Wipes,
    decimal SuccessRate,
    double? FastestKillSeconds,
    double? AverageKillSeconds,
    DateTimeOffset LastEncounter,
    string? IconUrl,
    bool IsIgnored
);

public record BossDetail(
    int TriggerId,
    string BossName,
    bool IsCM,
    int? Wing,
    string? IconUrl,
    int TotalEncounters,
    int Kills,
    int Wipes,
    decimal SuccessRate,
    double? FastestKillSeconds,
    double? AverageKillSeconds,
    List<BossEncounter> RecentEncounters,
    List<BossTopDps> TopDps
);

public record BossEncounter(
    Guid Id,
    bool Success,
    double DurationSeconds,
    DateTimeOffset EncounterTime,
    string? RecordedBy,
    string? LogUrl
);

public record BossTopDps(
    string AccountName,
    int Dps,
    string Profession
);
