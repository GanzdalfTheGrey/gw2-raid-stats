using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Core;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services;

public class LeaderboardService
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;

    // Threshold for considering someone a boon support (generation % to squad)
    private const decimal BoonSupportThreshold = 10m;

    // Threshold for considering someone a healer (healing power stat)
    private const int HealerThreshold = 1000;

    // Filter out incomplete "late start" encounters
    private const string LateStartFilter = "Late start";

    // Ignored encounter names (non-boss events)
    private static readonly string[] IgnoredEncounters = ["Spirit Race", "Twisted Castle", "River of Souls", "Statues of Grenth", "Bandit Trio"];

    // Encounters that should ALWAYS be shown (never filtered) - for multi-target fights
    private static readonly string[] AlwaysAllowedEncounters = [
        "Nikare", "Kenut",                          // Twin Largos
        "Harvest Temple",                            // Harvest Temple (EoD)
        "Captain Mai Trin", "Echo of Scarlet Briar" // Aetherblade Hideout
    ];

    // Display name for non-included players (pugs)
    private const string PugDisplayName = "Pug";

    public LeaderboardService(RaidStatsDb db, IncludedPlayerService includedPlayerService)
    {
        _db = db;
        _includedPlayerService = includedPlayerService;
    }

    /// <summary>
    /// Get all unique bosses with their trigger IDs
    /// </summary>
    public async Task<List<BossInfo>> GetBossListAsync(CancellationToken ct = default)
    {
        var bosses = await _db.Encounters
            .Where(e => e.Success) // Only successful encounters
            .Where(e => !e.BossName.Contains(LateStartFilter)) // Exclude late start
            .Where(e => AlwaysAllowedEncounters.Any(a => e.BossName.Contains(a)) || !IgnoredEncounters.Any(i => e.BossName.Contains(i))) // Exclude non-boss events, but always allow Twin Largos etc.
            .GroupBy(e => new { e.TriggerId, e.BossName })
            .Select(g => new BossInfo(
                g.Key.TriggerId,
                g.Key.BossName,
                g.Count()
            ))
            .OrderBy(b => b.BossName)
            .ToListAsync(ct);

        return bosses;
    }

    /// <summary>
    /// Get top DPS records for a specific boss
    /// </summary>
    public async Task<List<LeaderboardEntry>> GetTopDpsForBossAsync(
        int triggerId,
        bool isCM,
        int limit = 10,
        CancellationToken ct = default)
    {
        // Get included players (guild members) - only they can claim leaderboard spots
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = includedAccounts.ToList();

        // Get top DPS player_encounters for this boss (include SquadGroup for subgroup filtering)
        var query = _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == triggerId && x.e.IsCM == isCM && x.e.Success)
            .Where(x => !x.e.BossName.Contains(LateStartFilter)) // Exclude late start
            .Where(x => AlwaysAllowedEncounters.Any(a => x.e.BossName.Contains(a)) || !IgnoredEncounters.Any(i => x.e.BossName.Contains(i))); // Exclude non-boss events, but always allow Twin Largos etc.

        // Only included players can claim leaderboard spots
        if (includedList.Count > 0)
        {
            query = query.Where(x => includedList.Contains(x.p.AccountName));
        }

        var topEntries = await query
            .OrderByDescending(x => x.pe.Dps)
            .Take(limit)
            .Select(x => new
            {
                x.pe.Id,
                x.pe.EncounterId,
                x.pe.Dps,
                x.pe.CharacterName,
                x.pe.Profession,
                x.pe.SquadGroup,
                x.p.AccountName,
                x.e.EncounterTime,
                x.e.BossName,
                x.e.LogUrl,
                x.pe.QuicknessGeneration,
                x.pe.AlacracityGeneration
            })
            .ToListAsync(ct);

        // For each top entry, get the boon supports from their subgroup
        var results = new List<LeaderboardEntry>();
        foreach (var entry in topEntries)
        {
            var supports = await GetBoonSupportsForEncounterAsync(
                entry.EncounterId, entry.SquadGroup ?? 1, includedAccounts, ct);

            var wasProvidingBoons = (entry.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                                    (entry.AlacracityGeneration ?? 0) >= BoonSupportThreshold;

            results.Add(new LeaderboardEntry(
                entry.Id,
                entry.EncounterId,
                entry.AccountName,
                entry.CharacterName,
                entry.Profession,
                entry.Dps,
                entry.EncounterTime,
                entry.BossName,
                entry.LogUrl,
                supports,
                WasProvidingBoons: wasProvidingBoons
            ));
        }

        return results;
    }

    /// <summary>
    /// Get top DPS records for boon DPS players (those providing quickness or alacrity)
    /// </summary>
    public async Task<List<LeaderboardEntry>> GetTopBoonDpsForBossAsync(
        int triggerId,
        bool isCM,
        int limit = 10,
        CancellationToken ct = default)
    {
        // Get included players (guild members) - only they can claim leaderboard spots
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = includedAccounts.ToList();

        // Get top DPS player_encounters where the player was providing boons (include SquadGroup)
        var query = _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == triggerId && x.e.IsCM == isCM && x.e.Success)
            .Where(x => !x.e.BossName.Contains(LateStartFilter)) // Exclude late start
            .Where(x => AlwaysAllowedEncounters.Any(a => x.e.BossName.Contains(a)) || !IgnoredEncounters.Any(i => x.e.BossName.Contains(i))) // Exclude non-boss events, but always allow Twin Largos etc.
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                        (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold);

        // Only included players can claim leaderboard spots
        if (includedList.Count > 0)
        {
            query = query.Where(x => includedList.Contains(x.p.AccountName));
        }

        var topEntries = await query
            .OrderByDescending(x => x.pe.Dps)
            .Take(limit)
            .Select(x => new
            {
                x.pe.Id,
                x.pe.EncounterId,
                x.pe.Dps,
                x.pe.CharacterName,
                x.pe.Profession,
                x.pe.SquadGroup,
                x.p.AccountName,
                x.e.EncounterTime,
                x.e.BossName,
                x.e.LogUrl,
                x.pe.QuicknessGeneration,
                x.pe.AlacracityGeneration
            })
            .ToListAsync(ct);

        // For each top entry, get the other boon supports from their subgroup
        var results = new List<LeaderboardEntry>();
        foreach (var entry in topEntries)
        {
            var supports = await GetBoonSupportsForEncounterAsync(
                entry.EncounterId, entry.SquadGroup ?? 1, includedAccounts, ct, excludePlayerId: entry.Id);

            var boonType = (entry.QuicknessGeneration ?? 0) >= BoonSupportThreshold ? "Quickness" : "Alacrity";

            results.Add(new LeaderboardEntry(
                entry.Id,
                entry.EncounterId,
                entry.AccountName,
                entry.CharacterName,
                entry.Profession,
                entry.Dps,
                entry.EncounterTime,
                entry.BossName,
                entry.LogUrl,
                supports,
                boonType
            ));
        }

        return results;
    }

    /// <summary>
    /// Get top DPS records for healer players (those with 1000+ healing power)
    /// </summary>
    public async Task<List<LeaderboardEntry>> GetTopHealerDpsForBossAsync(
        int triggerId,
        bool isCM,
        int limit = 10,
        CancellationToken ct = default)
    {
        // Get included players (guild members) - only they can claim leaderboard spots
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = includedAccounts.ToList();

        // Get top DPS player_encounters where the player was a healer (1000+ healing power)
        var query = _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == triggerId && x.e.IsCM == isCM && x.e.Success)
            .Where(x => !x.e.BossName.Contains(LateStartFilter)) // Exclude late start
            .Where(x => AlwaysAllowedEncounters.Any(a => x.e.BossName.Contains(a)) || !IgnoredEncounters.Any(i => x.e.BossName.Contains(i)))
            .Where(x => x.pe.HealingPowerStat >= HealerThreshold);

        // Only included players can claim leaderboard spots
        if (includedList.Count > 0)
        {
            query = query.Where(x => includedList.Contains(x.p.AccountName));
        }

        var topEntries = await query
            .OrderByDescending(x => x.pe.Dps)
            .Take(limit)
            .Select(x => new
            {
                x.pe.Id,
                x.pe.EncounterId,
                x.pe.Dps,
                x.pe.CharacterName,
                x.pe.Profession,
                x.pe.SquadGroup,
                x.p.AccountName,
                x.e.EncounterTime,
                x.e.BossName,
                x.e.LogUrl,
                x.pe.HealingPowerStat
            })
            .ToListAsync(ct);

        // For each top entry, get the boon supports from their subgroup
        var results = new List<LeaderboardEntry>();
        foreach (var entry in topEntries)
        {
            var supports = await GetBoonSupportsForEncounterAsync(
                entry.EncounterId, entry.SquadGroup ?? 1, includedAccounts, ct);

            results.Add(new LeaderboardEntry(
                entry.Id,
                entry.EncounterId,
                entry.AccountName,
                entry.CharacterName,
                entry.Profession,
                entry.Dps,
                entry.EncounterTime,
                entry.BossName,
                entry.LogUrl,
                supports,
                BoonType: null,
                WasProvidingBoons: false
            ));
        }

        return results;
    }

    /// <summary>
    /// Get boon supports (quickness and alacrity providers) for a player's subgroup in an encounter
    /// Non-included players (pugs) are anonymized as "Pug"
    /// </summary>
    private async Task<List<BoonSupport>> GetBoonSupportsForEncounterAsync(
        Guid encounterId,
        int squadGroup,
        HashSet<string> includedAccounts,
        CancellationToken ct,
        Guid? excludePlayerId = null)
    {
        var query = _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => x.pe.EncounterId == encounterId)
            .Where(x => x.pe.SquadGroup == squadGroup) // Filter by same subgroup
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                        (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold);

        if (excludePlayerId.HasValue)
        {
            query = query.Where(x => x.pe.Id != excludePlayerId.Value);
        }

        var allSupports = await query
            .Select(x => new
            {
                x.p.AccountName,
                x.pe.CharacterName,
                x.pe.Profession,
                QuicknessGeneration = x.pe.QuicknessGeneration ?? 0,
                AlacracityGeneration = x.pe.AlacracityGeneration ?? 0
            })
            .ToListAsync(ct);

        // Convert to BoonSupport, anonymizing non-included players as "Pug"
        var supports = allSupports.Select(s =>
        {
            var isIncluded = includedAccounts.Contains(s.AccountName);
            return new BoonSupport(
                isIncluded ? s.AccountName : PugDisplayName,
                isIncluded ? s.CharacterName : PugDisplayName,
                s.Profession,
                s.QuicknessGeneration,
                s.AlacracityGeneration
            );
        }).ToList();

        // Return one quickness and one alacrity provider (the highest of each)
        var result = new List<BoonSupport>();

        var quicknessProvider = supports
            .Where(s => s.QuicknessGeneration >= BoonSupportThreshold)
            .OrderByDescending(s => s.QuicknessGeneration)
            .FirstOrDefault();

        var alacrityProvider = supports
            .Where(s => s.AlacracityGeneration >= BoonSupportThreshold)
            .OrderByDescending(s => s.AlacracityGeneration)
            .FirstOrDefault();

        if (quicknessProvider != null)
            result.Add(quicknessProvider);

        if (alacrityProvider != null && alacrityProvider != quicknessProvider)
            result.Add(alacrityProvider);

        return result;
    }

    /// <summary>
    /// Get all boss records with top DPS for each (NM and CM separate)
    /// </summary>
    public async Task<List<BossRecord>> GetAllBossRecordsAsync(CancellationToken ct = default)
    {
        // Get all unique boss/mode combinations with kill counts (excluding late start and non-boss events)
        var bossGroups = await _db.Encounters
            .Where(e => e.Success)
            .Where(e => !e.BossName.Contains(LateStartFilter))
            .Where(e => AlwaysAllowedEncounters.Any(a => e.BossName.Contains(a)) || !IgnoredEncounters.Any(i => e.BossName.Contains(i)))
            .GroupBy(e => new { e.TriggerId, e.BossName, e.IsCM })
            .Select(g => new
            {
                g.Key.TriggerId,
                g.Key.BossName,
                g.Key.IsCM,
                KillCount = g.Count()
            })
            .ToListAsync(ct);

        var results = new List<BossRecord>();

        foreach (var boss in bossGroups)
        {
            // Get wing from trigger ID, fallback to boss name matching
            var wing = WingMapping.GetWing(boss.TriggerId)
                       ?? WingMapping.GetWingByBossName(boss.BossName);
            var encounterOrder = WingMapping.GetEncounterOrder(boss.TriggerId);

            // Get top DPS for this boss/mode
            var topDps = await GetTopDpsForBossAsync(boss.TriggerId, boss.IsCM, 1, ct);
            var topBoonDps = await GetTopBoonDpsForBossAsync(boss.TriggerId, boss.IsCM, 1, ct);
            var topHealerDps = await GetTopHealerDpsForBossAsync(boss.TriggerId, boss.IsCM, 1, ct);

            results.Add(new BossRecord(
                boss.TriggerId,
                boss.BossName,
                boss.IsCM,
                boss.KillCount,
                topDps.FirstOrDefault(),
                topBoonDps.FirstOrDefault(),
                topHealerDps.FirstOrDefault(),
                IsRaid: wing.HasValue,
                Wing: wing,
                EncounterOrder: encounterOrder
            ));
        }

        // Sort by Wing, then EncounterOrder, then CM status
        return results
            .OrderBy(b => b.Wing ?? 999)
            .ThenBy(b => b.EncounterOrder)
            .ThenBy(b => b.IsCM)
            .ToList();
    }

    /// <summary>
    /// Debug: Get all unique trigger IDs with boss names
    /// </summary>
    public async Task<List<TriggerIdInfo>> GetAllTriggerIdsAsync(CancellationToken ct = default)
    {
        var triggerIds = await _db.Encounters
            .Where(e => e.Success)
            .Where(e => !e.BossName.Contains(LateStartFilter))
            .Where(e => AlwaysAllowedEncounters.Any(a => e.BossName.Contains(a)) || !IgnoredEncounters.Any(i => e.BossName.Contains(i)))
            .GroupBy(e => new { e.TriggerId, e.BossName, e.Wing })
            .Select(g => new TriggerIdInfo(
                g.Key.TriggerId,
                g.Key.BossName,
                g.Key.Wing,
                g.Count()
            ))
            .OrderBy(t => t.Wing ?? 999)
            .ThenBy(t => t.BossName)
            .ToListAsync(ct);

        return triggerIds;
    }

    /// <summary>
    /// Get top DPS records for a specific boss with unique players only (one entry per player)
    /// </summary>
    public async Task<List<LeaderboardEntry>> GetTopDpsForBossUniqueAsync(
        int triggerId,
        bool isCM,
        int limit = 10,
        CancellationToken ct = default)
    {
        // Get included players (guild members)
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = includedAccounts.ToList();

        // Get all player encounters for this boss, grouped by player, taking best DPS per player
        var query = _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == triggerId && x.e.IsCM == isCM && x.e.Success)
            .Where(x => !x.e.BossName.Contains(LateStartFilter))
            .Where(x => AlwaysAllowedEncounters.Any(a => x.e.BossName.Contains(a)) || !IgnoredEncounters.Any(i => x.e.BossName.Contains(i)));

        if (includedList.Count > 0)
        {
            query = query.Where(x => includedList.Contains(x.p.AccountName));
        }

        // Get all entries then group in memory for best per player
        var allEntries = await query
            .Select(x => new
            {
                x.pe.Id,
                x.pe.EncounterId,
                x.pe.Dps,
                x.pe.CharacterName,
                x.pe.Profession,
                x.pe.SquadGroup,
                x.p.AccountName,
                x.e.EncounterTime,
                x.e.BossName,
                x.e.LogUrl,
                x.pe.QuicknessGeneration,
                x.pe.AlacracityGeneration
            })
            .ToListAsync(ct);

        var topUnique = allEntries
            .GroupBy(x => x.AccountName)
            .Select(g => g.OrderByDescending(x => x.Dps).First())
            .OrderByDescending(x => x.Dps)
            .Take(limit)
            .ToList();

        var results = new List<LeaderboardEntry>();
        foreach (var entry in topUnique)
        {
            var supports = await GetBoonSupportsForEncounterAsync(
                entry.EncounterId, entry.SquadGroup ?? 1, includedAccounts, ct);

            var wasProvidingBoons = (entry.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                                    (entry.AlacracityGeneration ?? 0) >= BoonSupportThreshold;

            results.Add(new LeaderboardEntry(
                entry.Id,
                entry.EncounterId,
                entry.AccountName,
                entry.CharacterName,
                entry.Profession,
                entry.Dps,
                entry.EncounterTime,
                entry.BossName,
                entry.LogUrl,
                supports,
                WasProvidingBoons: wasProvidingBoons
            ));
        }

        return results;
    }

    /// <summary>
    /// Get a player's best DPS on each boss, ordered by wing/encounter
    /// </summary>
    public async Task<List<PlayerBossRecord>> GetPlayerBossRecordsAsync(
        string accountName,
        CancellationToken ct = default)
    {
        // Get all successful encounters with player data
        var playerRecords = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.p.AccountName == accountName && x.e.Success)
            .Where(x => !x.e.BossName.Contains(LateStartFilter))
            .Where(x => AlwaysAllowedEncounters.Any(a => x.e.BossName.Contains(a)) || !IgnoredEncounters.Any(i => x.e.BossName.Contains(i)))
            .Select(x => new
            {
                x.e.TriggerId,
                x.e.BossName,
                x.e.IsCM,
                x.pe.Dps,
                x.pe.Profession,
                x.pe.CharacterName,
                x.e.EncounterTime,
                x.e.LogUrl
            })
            .ToListAsync(ct);

        // Group by boss/mode and get best DPS for each
        var bestPerBoss = playerRecords
            .GroupBy(x => new { x.TriggerId, x.IsCM })
            .Select(g =>
            {
                var best = g.OrderByDescending(x => x.Dps).First();
                var wing = WingMapping.GetWing(best.TriggerId) ?? WingMapping.GetWingByBossName(best.BossName);
                var encounterOrder = WingMapping.GetEncounterOrder(best.TriggerId);

                return new PlayerBossRecord(
                    best.TriggerId,
                    best.BossName,
                    best.IsCM,
                    best.Dps,
                    best.Profession,
                    best.CharacterName,
                    best.EncounterTime,
                    best.LogUrl,
                    wing,
                    encounterOrder
                );
            })
            .OrderBy(x => x.Wing ?? 999)
            .ThenBy(x => x.EncounterOrder)
            .ThenBy(x => x.IsCM)
            .ToList();

        return bestPerBoss;
    }

    /// <summary>
    /// Get a player's best boon DPS on each boss, ordered by wing/encounter
    /// </summary>
    public async Task<List<PlayerBossRecord>> GetPlayerBoonBossRecordsAsync(
        string accountName,
        CancellationToken ct = default)
    {
        // Get all successful encounters where player was providing boons
        var playerRecords = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.p.AccountName == accountName && x.e.Success)
            .Where(x => !x.e.BossName.Contains(LateStartFilter))
            .Where(x => AlwaysAllowedEncounters.Any(a => x.e.BossName.Contains(a)) || !IgnoredEncounters.Any(i => x.e.BossName.Contains(i)))
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                        (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .Select(x => new
            {
                x.e.TriggerId,
                x.e.BossName,
                x.e.IsCM,
                x.pe.Dps,
                x.pe.Profession,
                x.pe.CharacterName,
                x.e.EncounterTime,
                x.e.LogUrl,
                x.pe.QuicknessGeneration,
                x.pe.AlacracityGeneration
            })
            .ToListAsync(ct);

        // Group by boss/mode and get best boon DPS for each
        var bestPerBoss = playerRecords
            .GroupBy(x => new { x.TriggerId, x.IsCM })
            .Select(g =>
            {
                var best = g.OrderByDescending(x => x.Dps).First();
                var wing = WingMapping.GetWing(best.TriggerId) ?? WingMapping.GetWingByBossName(best.BossName);
                var encounterOrder = WingMapping.GetEncounterOrder(best.TriggerId);
                var boonType = (best.QuicknessGeneration ?? 0) >= BoonSupportThreshold ? "Quickness" : "Alacrity";

                return new PlayerBossRecord(
                    best.TriggerId,
                    best.BossName,
                    best.IsCM,
                    best.Dps,
                    best.Profession,
                    best.CharacterName,
                    best.EncounterTime,
                    best.LogUrl,
                    wing,
                    encounterOrder,
                    boonType
                );
            })
            .OrderBy(x => x.Wing ?? 999)
            .ThenBy(x => x.EncounterOrder)
            .ThenBy(x => x.IsCM)
            .ToList();

        return bestPerBoss;
    }

    /// <summary>
    /// Get all bosses with a player's records (including "no record" entries)
    /// </summary>
    public async Task<List<PlayerBossRecord>> GetPlayerAllBossRecordsAsync(
        string accountName,
        bool boonDpsOnly = false,
        CancellationToken ct = default)
    {
        // Get all unique boss/mode combinations
        var allBosses = await _db.Encounters
            .Where(e => e.Success)
            .Where(e => !e.BossName.Contains(LateStartFilter))
            .Where(e => AlwaysAllowedEncounters.Any(a => e.BossName.Contains(a)) || !IgnoredEncounters.Any(i => e.BossName.Contains(i)))
            .GroupBy(e => new { e.TriggerId, e.BossName, e.IsCM })
            .Select(g => new { g.Key.TriggerId, g.Key.BossName, g.Key.IsCM })
            .ToListAsync(ct);

        // Get player's records
        var playerRecords = boonDpsOnly
            ? await GetPlayerBoonBossRecordsAsync(accountName, ct)
            : await GetPlayerBossRecordsAsync(accountName, ct);

        var playerRecordLookup = playerRecords.ToDictionary(r => (r.TriggerId, r.IsCM));

        // Build complete list with "no record" entries
        var results = allBosses.Select(boss =>
        {
            var wing = WingMapping.GetWing(boss.TriggerId) ?? WingMapping.GetWingByBossName(boss.BossName);
            var encounterOrder = WingMapping.GetEncounterOrder(boss.TriggerId);

            if (playerRecordLookup.TryGetValue((boss.TriggerId, boss.IsCM), out var record))
            {
                return record;
            }

            // No record for this boss
            return new PlayerBossRecord(
                boss.TriggerId,
                boss.BossName,
                boss.IsCM,
                null, // No DPS
                null,
                null,
                null,
                null,
                wing,
                encounterOrder,
                null
            );
        })
        .OrderBy(x => x.Wing ?? 999)
        .ThenBy(x => x.EncounterOrder)
        .ThenBy(x => x.IsCM)
        .ToList();

        return results;
    }

    /// <summary>
    /// Get full leaderboard data for a boss (both regular and boon DPS)
    /// </summary>
    public async Task<BossLeaderboard> GetBossLeaderboardAsync(
        int triggerId,
        bool isCM,
        int limit = 10,
        CancellationToken ct = default)
    {
        var bossName = await _db.Encounters
            .Where(e => e.TriggerId == triggerId)
            .Select(e => e.BossName)
            .FirstOrDefaultAsync(ct) ?? "Unknown";

        var topDps = await GetTopDpsForBossAsync(triggerId, isCM, limit, ct);
        var topBoonDps = await GetTopBoonDpsForBossAsync(triggerId, isCM, limit, ct);
        var topHealerDps = await GetTopHealerDpsForBossAsync(triggerId, isCM, limit, ct);

        return new BossLeaderboard(
            triggerId,
            bossName,
            isCM,
            topDps,
            topBoonDps,
            topHealerDps
        );
    }
}

public record BossInfo(int TriggerId, string BossName, int KillCount);

public record LeaderboardEntry(
    Guid Id,
    Guid EncounterId,
    string AccountName,
    string CharacterName,
    string Profession,
    int Dps,
    DateTimeOffset EncounterTime,
    string BossName,
    string? LogUrl,
    List<BoonSupport> BoonSupports,
    string? BoonType = null,
    bool WasProvidingBoons = false
);

public record BoonSupport(
    string AccountName,
    string CharacterName,
    string Profession,
    decimal QuicknessGeneration,
    decimal AlacracityGeneration
)
{
    public string BoonType => QuicknessGeneration >= 10 ? "Quickness" : "Alacrity";
}

public record BossLeaderboard(
    int TriggerId,
    string BossName,
    bool IsCM,
    List<LeaderboardEntry> TopDps,
    List<LeaderboardEntry> TopBoonDps,
    List<LeaderboardEntry> TopHealerDps
);

public record BossRecord(
    int TriggerId,
    string BossName,
    bool IsCM,
    int KillCount,
    LeaderboardEntry? TopDps,
    LeaderboardEntry? TopBoonDps,
    LeaderboardEntry? TopHealerDps,
    bool IsRaid,
    int? Wing = null,
    int EncounterOrder = 999
);

public record TriggerIdInfo(
    int TriggerId,
    string BossName,
    int? Wing,
    int KillCount
);

public record PlayerBossRecord(
    int TriggerId,
    string BossName,
    bool IsCM,
    int? Dps,
    string? Profession,
    string? CharacterName,
    DateTimeOffset? EncounterTime,
    string? LogUrl,
    int? Wing,
    int EncounterOrder,
    string? BoonType = null
)
{
    public bool HasRecord => Dps.HasValue;
};
