using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Core;

namespace GW2RaidStats.Infrastructure.Services;

public class PlayerProfileService
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;
    private readonly IgnoredBossService _ignoredBossService;

    // Thresholds for role classification
    private const decimal BoonThreshold = 10m;          // 10% boon generation = boon provider
    private const decimal HealBoonDpsRatio = 0.25m;     // Below 25% of avg DPS with boons = heal boon

    public PlayerProfileService(
        RaidStatsDb db,
        IncludedPlayerService includedPlayerService,
        IgnoredBossService ignoredBossService)
    {
        _db = db;
        _includedPlayerService = includedPlayerService;
        _ignoredBossService = ignoredBossService;
    }

    /// <summary>
    /// Get a player's profile by account name
    /// </summary>
    public async Task<PlayerProfile?> GetProfileAsync(string accountName, CancellationToken ct = default)
    {
        var player = await _db.Players
            .FirstOrDefaultAsync(p => p.AccountName == accountName, ct);

        if (player == null) return null;

        // Get ignored bosses to filter them out
        var ignoredBosses = await _ignoredBossService.GetIgnoredKeysAsync(ct);

        // Get all player encounters with encounter data
        var allEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == player.Id)
            .Select(x => new
            {
                x.pe.Id,
                x.pe.EncounterId,
                x.pe.CharacterName,
                x.pe.Profession,
                x.pe.Dps,
                x.pe.QuicknessGeneration,
                x.pe.AlacracityGeneration,
                x.pe.Deaths,
                x.e.TriggerId,
                x.e.BossName,
                x.e.IsCM,
                x.e.Success,
                x.e.EncounterTime,
                x.e.LogUrl
            })
            .ToListAsync(ct);

        // Filter out ignored bosses
        var encounters = allEncounters
            .Where(e => !ignoredBosses.Contains((e.TriggerId, e.IsCM)))
            .ToList();

        // Get average DPS for each encounter (for role classification)
        var encounterIds = encounters.Select(e => e.EncounterId).Distinct().ToList();
        var encounterAvgDps = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId))
            .GroupBy(pe => pe.EncounterId)
            .Select(g => new { EncounterId = g.Key, AvgDps = g.Average(pe => (decimal)pe.Dps) })
            .ToListAsync(ct);
        var avgDpsLookup = encounterAvgDps.ToDictionary(x => x.EncounterId, x => x.AvgDps);

        if (encounters.Count == 0)
        {
            return new PlayerProfile(
                player.Id,
                player.AccountName,
                player.FirstSeen,
                TotalEncounters: 0,
                TotalKills: 0,
                SuccessRate: 0,
                FavoriteClass: null,
                FavoriteRole: null,
                ClassBreakdown: [],
                RoleBreakdown: [],
                PersonalBests: [],
                RecentActivity: []
            );
        }

        // Calculate totals
        var totalEncounters = encounters.Count;
        var totalKills = encounters.Count(e => e.Success);
        var successRate = totalEncounters > 0 ? (decimal)totalKills / totalEncounters * 100 : 0;

        // Calculate class breakdown
        var classBreakdown = encounters
            .GroupBy(e => e.Profession)
            .Select(g => new ClassStats(g.Key, g.Count(), (decimal)g.Count() / totalEncounters * 100))
            .OrderByDescending(c => c.Count)
            .ToList();

        var favoriteClass = classBreakdown.FirstOrDefault()?.Profession;

        // Calculate role breakdown
        var roleBreakdown = new List<RoleStats>();
        var dpsCount = 0;
        var boonDpsCount = 0;
        var healBoonCount = 0;

        foreach (var e in encounters)
        {
            var avgDps = avgDpsLookup.GetValueOrDefault(e.EncounterId, 10000m);
            var role = ClassifyRole(e.QuicknessGeneration, e.AlacracityGeneration, e.Dps, avgDps);
            switch (role)
            {
                case "DPS": dpsCount++; break;
                case "Boon DPS": boonDpsCount++; break;
                case "Heal Boon": healBoonCount++; break;
            }
        }

        if (dpsCount > 0) roleBreakdown.Add(new RoleStats("DPS", dpsCount, (decimal)dpsCount / totalEncounters * 100));
        if (boonDpsCount > 0) roleBreakdown.Add(new RoleStats("Boon DPS", boonDpsCount, (decimal)boonDpsCount / totalEncounters * 100));
        if (healBoonCount > 0) roleBreakdown.Add(new RoleStats("Heal Boon", healBoonCount, (decimal)healBoonCount / totalEncounters * 100));

        roleBreakdown = roleBreakdown.OrderByDescending(r => r.Count).ToList();
        var favoriteRole = roleBreakdown.FirstOrDefault()?.Role;

        // Get personal bests (top DPS per boss/mode, only for kills)
        var personalBests = encounters
            .Where(e => e.Success)
            .GroupBy(e => new { e.TriggerId, e.BossName, e.IsCM })
            .Select(g =>
            {
                var best = g.OrderByDescending(e => e.Dps).First();
                var wing = WingMapping.GetWing(g.Key.TriggerId);
                var encounterOrder = WingMapping.GetEncounterOrder(g.Key.TriggerId);
                return new PersonalBest(
                    best.EncounterId,
                    g.Key.TriggerId,
                    g.Key.BossName,
                    g.Key.IsCM,
                    best.Dps,
                    best.Profession,
                    best.CharacterName,
                    best.EncounterTime,
                    best.LogUrl,
                    g.Count(),
                    wing,
                    encounterOrder
                );
            })
            .OrderBy(pb => pb.Wing ?? 99)
            .ThenBy(pb => pb.EncounterOrder)
            .ThenBy(pb => pb.IsCM)
            .ToList();

        // Get recent activity (last 20 encounters)
        var recentActivity = encounters
            .OrderByDescending(e => e.EncounterTime)
            .Take(20)
            .Select(e => new PlayerRecentEncounter(
                e.EncounterId,
                e.BossName,
                e.IsCM,
                e.Success,
                e.Dps,
                e.Profession,
                e.CharacterName,
                ClassifyRole(e.QuicknessGeneration, e.AlacracityGeneration, e.Dps, avgDpsLookup.GetValueOrDefault(e.EncounterId, 10000m)),
                e.EncounterTime,
                e.LogUrl
            ))
            .ToList();

        return new PlayerProfile(
            player.Id,
            player.AccountName,
            player.FirstSeen,
            totalEncounters,
            totalKills,
            successRate,
            favoriteClass,
            favoriteRole,
            classBreakdown,
            roleBreakdown,
            personalBests,
            recentActivity
        );
    }

    /// <summary>
    /// Search for included players by account name
    /// </summary>
    public async Task<List<PlayerSearchResult>> SearchPlayersAsync(
        string? searchTerm,
        int limit = 50,
        CancellationToken ct = default)
    {
        // Get included players only
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);

        var query = _db.Players.AsQueryable();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.ToLower();
            query = query.Where(p => p.AccountName.ToLower().Contains(term));
        }

        var players = await query
            .OrderBy(p => p.AccountName)
            .ToListAsync(ct);

        // Filter to only included players
        var includedPlayers = players.Where(p => includedAccounts.Contains(p.AccountName)).ToList();

        var results = new List<PlayerSearchResult>();
        foreach (var player in includedPlayers.Take(limit))
        {
            var encounterCount = await _db.PlayerEncounters
                .Where(pe => pe.PlayerId == player.Id)
                .CountAsync(ct);

            results.Add(new PlayerSearchResult(
                player.Id,
                player.AccountName,
                player.FirstSeen,
                encounterCount
            ));
        }

        return results.OrderByDescending(r => r.EncounterCount).ToList();
    }

    /// <summary>
    /// Get all included players sorted by encounter count
    /// </summary>
    public async Task<List<PlayerSearchResult>> GetAllPlayersAsync(
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        // Get included players only
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);

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

        // Filter to only included players
        return players
            .Where(p => includedAccounts.Contains(p.AccountName))
            .Skip(offset)
            .Take(limit)
            .Select(p => new PlayerSearchResult(
                p.Id,
                p.AccountName,
                p.FirstSeen,
                p.EncounterCount
            )).ToList();
    }

    /// <summary>
    /// Classify a player's role based on boon generation and DPS relative to encounter average
    /// Roles: DPS, Boon DPS, Heal Boon
    /// - DPS: No significant boons
    /// - Boon DPS: Has boons + DPS >= 25% of encounter average
    /// - Heal Boon: Has boons + DPS < 25% of encounter average (likely a healer)
    /// </summary>
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

public record PlayerProfile(
    Guid Id,
    string AccountName,
    DateTimeOffset FirstSeen,
    int TotalEncounters,
    int TotalKills,
    decimal SuccessRate,
    string? FavoriteClass,
    string? FavoriteRole,
    List<ClassStats> ClassBreakdown,
    List<RoleStats> RoleBreakdown,
    List<PersonalBest> PersonalBests,
    List<PlayerRecentEncounter> RecentActivity
);

public record ClassStats(string Profession, int Count, decimal Percentage);

public record RoleStats(string Role, int Count, decimal Percentage);

public record PersonalBest(
    Guid EncounterId,
    int TriggerId,
    string BossName,
    bool IsCM,
    int Dps,
    string Profession,
    string CharacterName,
    DateTimeOffset EncounterTime,
    string? LogUrl,
    int KillCount,
    int? Wing,
    int EncounterOrder
);

public record PlayerRecentEncounter(
    Guid EncounterId,
    string BossName,
    bool IsCM,
    bool Success,
    int Dps,
    string Profession,
    string CharacterName,
    string Role,
    DateTimeOffset EncounterTime,
    string? LogUrl
);

public record PlayerSearchResult(
    Guid Id,
    string AccountName,
    DateTimeOffset FirstSeen,
    int EncounterCount
);
