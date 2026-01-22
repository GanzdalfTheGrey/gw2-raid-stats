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

    // Filter out incomplete "late start" encounters
    private const string LateStartFilter = "Late start";

    // Ignored encounter names (non-boss events)
    private static readonly string[] IgnoredEncounters = ["Spirit Race", "Twisted Castle", "River of Souls", "Statues of Grenth"];

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
            .Where(e => !IgnoredEncounters.Any(i => e.BossName.Contains(i))) // Exclude non-boss events
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
            .Where(x => !IgnoredEncounters.Any(i => x.e.BossName.Contains(i))); // Exclude non-boss events

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
                x.e.LogUrl
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
                entry.AccountName,
                entry.CharacterName,
                entry.Profession,
                entry.Dps,
                entry.EncounterTime,
                entry.BossName,
                entry.LogUrl,
                supports
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
            .Where(x => !IgnoredEncounters.Any(i => x.e.BossName.Contains(i))) // Exclude non-boss events
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
            .Where(e => !IgnoredEncounters.Any(i => e.BossName.Contains(i)))
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

            results.Add(new BossRecord(
                boss.TriggerId,
                boss.BossName,
                boss.IsCM,
                boss.KillCount,
                topDps.FirstOrDefault(),
                topBoonDps.FirstOrDefault(),
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
            .Where(e => !IgnoredEncounters.Any(i => e.BossName.Contains(i)))
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

        return new BossLeaderboard(
            triggerId,
            bossName,
            isCM,
            topDps,
            topBoonDps
        );
    }
}

public record BossInfo(int TriggerId, string BossName, int KillCount);

public record LeaderboardEntry(
    Guid Id,
    string AccountName,
    string CharacterName,
    string Profession,
    int Dps,
    DateTimeOffset EncounterTime,
    string BossName,
    string? LogUrl,
    List<BoonSupport> BoonSupports,
    string? BoonType = null
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
    List<LeaderboardEntry> TopBoonDps
);

public record BossRecord(
    int TriggerId,
    string BossName,
    bool IsCM,
    int KillCount,
    LeaderboardEntry? TopDps,
    LeaderboardEntry? TopBoonDps,
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
