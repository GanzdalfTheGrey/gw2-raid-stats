using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Services.Achievements.Progress;

namespace GW2RaidStats.Infrastructure.Services.Achievements.Checkers;

/// <summary>
/// Checks guild milestone achievements that need historical data:
/// - Record Breakers (DPS + boon DPS records broken in same encounter)
/// - The Comeback (kill after 5+ wipes in same session)
/// - Synchronized (3+ players set personal best in same encounter)
/// - Full Clear (clear all 8 wings in a single session)
/// </summary>
public class GuildMilestoneChecker : IAchievementChecker
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;

    // Threshold for boon support
    private const decimal BoonSupportThreshold = 10m;

    public GuildMilestoneChecker(
        RaidStatsDb db,
        IncludedPlayerService includedPlayerService)
    {
        _db = db;
        _includedPlayerService = includedPlayerService;
    }

    public async Task<List<AchievementUnlock>> CheckAsync(AchievementCheckContext context, CancellationToken ct)
    {
        var unlocks = new List<AchievementUnlock>();

        // Only check for successful kills
        if (!context.Encounter.Success) return unlocks;

        // Require at least 5 guild members for guild achievements
        if (context.GuildMemberCount < 5) return unlocks;

        var encounter = context.Encounter;

        // Record Breakers
        var recordBreakerUnlock = await CheckRecordBreakersAsync(encounter, context, ct);
        if (recordBreakerUnlock != null) unlocks.Add(recordBreakerUnlock);

        // The Comeback
        var comebackUnlock = await CheckTheComebackAsync(encounter, ct);
        if (comebackUnlock != null) unlocks.Add(comebackUnlock);

        // Synchronized - check if 3+ guild members set personal bests
        var synchronizedUnlock = await CheckSynchronizedAsync(encounter, context, ct);
        if (synchronizedUnlock != null) unlocks.Add(synchronizedUnlock);

        // Full Clear - all 8 wings in a single session
        var fullClearUnlock = await CheckFullClearAsync(encounter, ct);
        if (fullClearUnlock != null) unlocks.Add(fullClearUnlock);

        // Musical Chairs - different boon providers each boss in a wing
        var musicalChairsUnlocks = await CheckMusicalChairsAsync(encounter, ct);
        unlocks.AddRange(musicalChairsUnlocks);

        return unlocks;
    }

    private async Task<AchievementUnlock?> CheckRecordBreakersAsync(
        Database.Entities.EncounterEntity encounter,
        AchievementCheckContext context,
        CancellationToken ct)
    {
        var guildMembers = context.GuildMembers.ToList();

        // Find top DPS among guild members
        var topDps = guildMembers.OrderByDescending(p => p.Dps).FirstOrDefault();
        if (topDps == null) return null;

        // Check previous DPS record
        var previousDpsRecord = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == encounter.TriggerId && x.e.IsCM == encounter.IsCM && x.e.Success)
            .Where(x => x.e.EncounterTime < encounter.EncounterTime)
            .Where(x => context.IncludedAccounts.Contains(x.p.AccountName))
            .MaxAsync(x => (int?)x.pe.Dps, ct) ?? 0;

        var dpsRecordBroken = topDps.Dps > previousDpsRecord;

        // Find top boon DPS among guild members
        var boonPlayers = guildMembers
            .Where(p => EncounterStatsCalculator.IsBoonSupport(p))
            .OrderByDescending(p => p.Dps)
            .ToList();

        if (boonPlayers.Count == 0) return null;

        var topBoonDps = boonPlayers.First();

        // Check previous boon DPS record
        var previousBoonDpsRecord = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == encounter.TriggerId && x.e.IsCM == encounter.IsCM && x.e.Success)
            .Where(x => x.e.EncounterTime < encounter.EncounterTime)
            .Where(x => context.IncludedAccounts.Contains(x.p.AccountName))
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                       (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .MaxAsync(x => (int?)x.pe.Dps, ct) ?? 0;

        var boonDpsRecordBroken = topBoonDps.Dps > previousBoonDpsRecord;

        // Award if both records broken
        if (dpsRecordBroken && boonDpsRecordBroken)
        {
            return new AchievementUnlock(
                "record_breakers",
                null,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    dps_player = topDps.AccountName,
                    dps = topDps.Dps,
                    boon_dps_player = topBoonDps.AccountName,
                    boon_dps = topBoonDps.Dps
                },
                encounter.EncounterTime
            );
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckTheComebackAsync(
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        // Get all encounters for this boss on this day
        var sessionDate = encounter.EncounterTime.Date;
        var sessionStart = new DateTimeOffset(sessionDate, encounter.EncounterTime.Offset);
        var sessionEnd = sessionStart.AddDays(1);

        var bossEncountersToday = await _db.Encounters
            .Where(e => e.TriggerId == encounter.TriggerId)
            .Where(e => e.IsCM == encounter.IsCM)
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime < sessionEnd)
            .Where(e => e.EncounterTime <= encounter.EncounterTime)
            .OrderBy(e => e.EncounterTime)
            .ToListAsync(ct);

        // Count wipes before this kill
        var wipesBeforeKill = bossEncountersToday
            .TakeWhile(e => e.Id != encounter.Id)
            .Count(e => !e.Success);

        if (wipesBeforeKill >= 5)
        {
            return new AchievementUnlock(
                "the_comeback",
                null,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    wipes = wipesBeforeKill
                },
                encounter.EncounterTime
            );
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckSynchronizedAsync(
        Database.Entities.EncounterEntity encounter,
        AchievementCheckContext context,
        CancellationToken ct)
    {
        // Count guild members who set personal bests in this encounter
        var personalBestCount = 0;
        var playerNames = new List<string>();

        foreach (var player in context.GuildMembers)
        {
            // Get previous best for this boss/CM combo
            var previousBest = await _db.PlayerEncounters
                .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
                .Where(x => x.pe.PlayerId == player.PlayerId)
                .Where(x => x.e.TriggerId == encounter.TriggerId && x.e.IsCM == encounter.IsCM && x.e.Success)
                .Where(x => x.e.EncounterTime < encounter.EncounterTime)
                .MaxAsync(x => (int?)x.pe.Dps, ct) ?? 0;

            if (player.Dps > previousBest && previousBest > 0) // Only count if they had a previous best
            {
                personalBestCount++;
                playerNames.Add(player.AccountName);
            }
        }

        if (personalBestCount >= 3)
        {
            return new AchievementUnlock(
                "synchronized",
                null,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    player_count = personalBestCount,
                    players = playerNames
                },
                encounter.EncounterTime
            );
        }

        return null;
    }

    /// <summary>
    /// Full Clear - Clear all 8 wings in a single session (within 8 hours)
    /// </summary>
    private async Task<AchievementUnlock?> CheckFullClearAsync(
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        // Get all boss trigger IDs for wings 1-8
        var allWingBosses = AchievementDefinitions.WingMasterBosses
            .SelectMany(kvp => kvp.Value)
            .Select(AchievementDefinitions.NormalizeTriggerId)
            .Distinct()
            .ToHashSet();

        // Look for kills within 8 hours of this encounter (long raid session)
        var sessionStart = encounter.EncounterTime.AddHours(-8);
        var sessionEnd = encounter.EncounterTime;

        // Get all successful kills in this session window
        var sessionKills = await _db.Encounters
            .Where(e => e.Success)
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime <= sessionEnd)
            .Select(e => new { e.TriggerId, e.EncounterTime })
            .ToListAsync(ct);

        // Normalize trigger IDs and get unique bosses killed
        var bossesKilled = sessionKills
            .Select(k => AchievementDefinitions.NormalizeTriggerId(k.TriggerId))
            .Distinct()
            .ToHashSet();

        // Check if all wing bosses were killed
        if (allWingBosses.All(b => bossesKilled.Contains(b)))
        {
            // Find the time when the last boss was killed to complete the clear
            var lastBossTime = sessionKills.Max(k => k.EncounterTime);
            return new AchievementUnlock(
                "full_clear",
                null, // Guild achievement - no specific player
                new
                {
                    session_date = lastBossTime.Date,
                    bosses_killed = bossesKilled.Count
                },
                lastBossTime
            );
        }

        return null;
    }

    /// <summary>
    /// Musical Chairs - Complete a wing with different boon providers on each boss.
    /// Boon roles tracked: heal_alac, heal_quick, dps_alac, dps_quick (not pure_dps).
    /// No player can repeat the same boon role across different bosses in the wing.
    /// </summary>
    private async Task<List<AchievementUnlock>> CheckMusicalChairsAsync(
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        var unlocks = new List<AchievementUnlock>();

        // Only check for raid encounters (wings 1-8)
        if (!encounter.Wing.HasValue || encounter.Wing < 1 || encounter.Wing > 8) return unlocks;

        var wing = encounter.Wing.Value;
        var wingBosses = AchievementDefinitions.WingMasterBosses.GetValueOrDefault(wing);
        if (wingBosses == null) return unlocks;

        // Look for kills within 8 hours (same session window as full clear)
        var sessionStart = encounter.EncounterTime.AddHours(-8);
        var sessionEnd = encounter.EncounterTime;

        // Get all successful kills for this wing's bosses in the session
        var wingBossSet = wingBosses.Select(AchievementDefinitions.NormalizeTriggerId).ToHashSet();

        var sessionEncounters = await _db.Encounters
            .Where(e => e.Success)
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime <= sessionEnd)
            .Where(e => e.Wing == wing)
            .Select(e => new { e.Id, e.TriggerId, e.BossName, e.EncounterTime })
            .ToListAsync(ct);

        // Normalize and filter to wing bosses, take most recent kill per boss
        var bossKills = sessionEncounters
            .Select(e => new { e.Id, TriggerId = AchievementDefinitions.NormalizeTriggerId(e.TriggerId), e.BossName, e.EncounterTime })
            .Where(e => wingBossSet.Contains(e.TriggerId))
            .GroupBy(e => e.TriggerId)
            .Select(g => g.OrderByDescending(e => e.EncounterTime).First())
            .ToList();

        // Need all bosses in the wing killed
        if (bossKills.Count != wingBosses.Length) return unlocks;

        // Get player roles for each encounter
        var encounterIds = bossKills.Select(k => k.Id).ToList();
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => encounterIds.Contains(x.pe.EncounterId))
            .Select(x => new
            {
                x.pe.EncounterId,
                x.p.AccountName,
                x.pe.Role
            })
            .ToListAsync(ct);

        // Group roles into general categories:
        // - Healers (heal_alac OR heal_quick) - same player can't heal on multiple bosses
        // - Boon DPS (dps_alac OR dps_quick) - same player can't boon DPS on multiple bosses
        var roleGroups = new Dictionary<string, string[]>
        {
            { "healer", new[] { "heal_alac", "heal_quick" } },
            { "boon_dps", new[] { "dps_alac", "dps_quick" } }
        };

        // For each role group, check that no player repeats across bosses
        var isMusicalChairs = true;
        foreach (var (groupName, roles) in roleGroups)
        {
            var playersInRoleGroup = playerEncounters
                .Where(pe => roles.Contains(pe.Role))
                .GroupBy(pe => pe.EncounterId)
                .Select(g => g.Select(pe => pe.AccountName).ToList())
                .ToList();

            // No player should appear more than once across all bosses in this role group
            var allPlayersInGroup = playersInRoleGroup.SelectMany(p => p).ToList();
            if (allPlayersInGroup.Count != allPlayersInGroup.Distinct().Count())
            {
                // Someone repeated this role group across bosses
                isMusicalChairs = false;
                break;
            }
        }

        if (isMusicalChairs)
        {
            var achievementCode = $"musical_chairs_w{wing}";
            var lastBossTime = bossKills.Max(k => k.EncounterTime);

            unlocks.Add(new AchievementUnlock(
                achievementCode,
                null, // Guild achievement
                new
                {
                    wing,
                    bosses = bossKills.Select(b => b.BossName).ToList(),
                    session_date = lastBossTime.Date
                },
                lastBossTime
            ));
        }

        return unlocks;
    }
}
