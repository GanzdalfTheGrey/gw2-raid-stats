using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services;

/// <summary>
/// Per-player improvement dashboard data: time-series of DPS / Boon DPS / Heals over time
/// for a chosen boss, with personal best, last-5-vs-prev-5 trend, and guild median overlay.
/// Role classification (DPS / Boon DPS / Heal Boon) mirrors PlayerProfileService.ClassifyRole.
/// </summary>
public class PlayerTrendsService
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;

    private const decimal BoonThreshold = 10m;
    private const decimal HealBoonDpsRatio = 0.25m;

    public PlayerTrendsService(RaidStatsDb db, IncludedPlayerService includedPlayerService)
    {
        _db = db;
        _includedPlayerService = includedPlayerService;
    }

    public async Task<PlayerTrendsDto?> GetTrendsAsync(
        string accountName,
        int triggerId,
        bool isCm,
        string role,
        string range,
        CancellationToken ct = default)
    {
        // Resolve player
        var player = await _db.Players
            .FirstOrDefaultAsync(p => p.AccountName == accountName, ct);
        if (player == null) return null;

        // Determine the start of the range (null = all time)
        var rangeStart = await ResolveRangeStartAsync(range, ct);

        // Pull this player's encounter rows for the boss
        var playerQuery = _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == player.Id
                     && x.e.TriggerId == triggerId
                     && x.e.IsCM == isCm
                     && x.e.Success);
        if (rangeStart.HasValue)
        {
            playerQuery = playerQuery.Where(x => x.e.EncounterTime >= rangeStart.Value);
        }
        var playerRows = await playerQuery
            .OrderBy(x => x.e.EncounterTime)
            .ToListAsync(ct);

        // Build avg-DPS-per-encounter lookup so we can classify roles correctly
        var encounterIds = playerRows.Select(x => x.pe.EncounterId).Distinct().ToList();
        var avgDpsLookup = await BuildAvgDpsLookupAsync(encounterIds, ct);

        // Filter player rows by classified role
        var filteredPlayerRows = playerRows
            .Where(x => ClassifyRole(
                x.pe.QuicknessGeneration,
                x.pe.AlacracityGeneration,
                x.pe.Dps,
                avgDpsLookup.GetValueOrDefault(x.pe.EncounterId, 10000m)) == role)
            .ToList();

        var useHps = role == "Heal Boon";
        var points = filteredPlayerRows
            .Select(x => new TrendPoint(x.e.EncounterTime, useHps ? x.pe.Hps : x.pe.Dps))
            .ToList();

        // Personal best within the range
        TrendPoint? pb = points.Count == 0 ? null : points.OrderByDescending(p => p.Value).First();

        // Guild median across all included guild members in this role/boss/range
        var guildMedian = await ComputeGuildMedianAsync(triggerId, isCm, role, useHps, rangeStart, ct);

        // Last-5 vs prior-5 trend (percentage change)
        decimal trendPct = 0;
        if (points.Count >= 6)
        {
            var last5 = points.TakeLast(5).Average(p => (decimal)p.Value);
            var prev5 = points.SkipLast(5).TakeLast(5).Average(p => (decimal)p.Value);
            trendPct = prev5 == 0 ? 0 : (last5 - prev5) / prev5 * 100;
        }

        return new PlayerTrendsDto(
            points,
            pb,
            guildMedian,
            trendPct,
            points.Count);
    }

    /// <summary>
    /// Bosses the player has killed within the given range (patch / 90d / all),
    /// with kill counts scoped to that range. Returned alphabetically (NM before CM)
    /// for the autocomplete UI; the page picks the highest-Count entry as the default.
    /// </summary>
    public async Task<List<BossEncounterCountDto>> GetBossEncounterCountsAsync(
        string accountName,
        string range = "all",
        CancellationToken ct = default)
    {
        var player = await _db.Players
            .FirstOrDefaultAsync(p => p.AccountName == accountName, ct);
        if (player == null) return new();

        var rangeStart = await ResolveRangeStartAsync(range, ct);

        var query = _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == player.Id && x.e.Success);
        if (rangeStart.HasValue)
        {
            query = query.Where(x => x.e.EncounterTime >= rangeStart.Value);
        }

        var counts = await query
            .GroupBy(x => new { x.e.TriggerId, x.e.BossName, x.e.IsCM })
            .Select(g => new
            {
                g.Key.TriggerId,
                g.Key.BossName,
                g.Key.IsCM,
                Count = g.Count()
            })
            .ToListAsync(ct);

        return counts
            .Select(c => new BossEncounterCountDto(c.TriggerId, c.BossName, c.IsCM, c.Count))
            .OrderBy(c => c.BossName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.IsCM)
            .ToList();
    }

    private async Task<DateTimeOffset?> ResolveRangeStartAsync(string range, CancellationToken ct)
    {
        if (range == "patch")
        {
            var asOf = DateTimeOffset.UtcNow;
            var patch = await _db.LeaderboardPatches
                .Where(p => p.StartDate <= asOf)
                .OrderByDescending(p => p.StartDate)
                .FirstOrDefaultAsync(ct);
            return patch?.StartDate;
        }
        if (range == "90d") return DateTimeOffset.UtcNow.AddDays(-90);
        return null; // all time
    }

    private async Task<Dictionary<Guid, decimal>> BuildAvgDpsLookupAsync(
        List<Guid> encounterIds,
        CancellationToken ct)
    {
        if (encounterIds.Count == 0) return new();
        var rows = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId))
            .GroupBy(pe => pe.EncounterId)
            .Select(g => new { EncounterId = g.Key, AvgDps = g.Average(pe => (decimal)pe.Dps) })
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.EncounterId, x => x.AvgDps);
    }

    private async Task<int> ComputeGuildMedianAsync(
        int triggerId,
        bool isCm,
        string role,
        bool useHps,
        DateTimeOffset? rangeStart,
        CancellationToken ct)
    {
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        if (includedAccounts.Count == 0) return 0;

        var query = _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == triggerId
                     && x.e.IsCM == isCm
                     && x.e.Success
                     && includedAccounts.Contains(x.p.AccountName));
        if (rangeStart.HasValue)
        {
            query = query.Where(x => x.e.EncounterTime >= rangeStart.Value);
        }

        var rows = await query.ToListAsync(ct);
        if (rows.Count == 0) return 0;

        // Reuse avg-DPS lookup so role classification is consistent
        var encIds = rows.Select(x => x.pe.EncounterId).Distinct().ToList();
        var avgDpsLookup = await BuildAvgDpsLookupAsync(encIds, ct);

        var values = rows
            .Where(x => ClassifyRole(
                x.pe.QuicknessGeneration,
                x.pe.AlacracityGeneration,
                x.pe.Dps,
                avgDpsLookup.GetValueOrDefault(x.pe.EncounterId, 10000m)) == role)
            .Select(x => useHps ? x.pe.Hps : x.pe.Dps)
            .OrderBy(v => v)
            .ToList();

        if (values.Count == 0) return 0;

        // Median: midpoint for odd count, average of the two middles for even count
        if (values.Count % 2 == 1) return values[values.Count / 2];
        var lo = values[values.Count / 2 - 1];
        var hi = values[values.Count / 2];
        return (lo + hi) / 2;
    }

    private static string ClassifyRole(decimal? quickness, decimal? alacrity, int dps, decimal avgDps)
    {
        var isBoonProvider = (quickness ?? 0) >= BoonThreshold || (alacrity ?? 0) >= BoonThreshold;
        if (isBoonProvider)
        {
            var dpsRatio = avgDps > 0 ? dps / avgDps : 1;
            return dpsRatio < HealBoonDpsRatio ? "Heal Boon" : "Boon DPS";
        }
        return "DPS";
    }
}

public record TrendPoint(DateTimeOffset EncounterTime, int Value);

public record PlayerTrendsDto(
    List<TrendPoint> Points,
    TrendPoint? PersonalBest,
    int GuildMedian,
    decimal TrendPct,
    int TotalKills);

public record BossEncounterCountDto(
    int TriggerId,
    string BossName,
    bool IsCM,
    int Count);
