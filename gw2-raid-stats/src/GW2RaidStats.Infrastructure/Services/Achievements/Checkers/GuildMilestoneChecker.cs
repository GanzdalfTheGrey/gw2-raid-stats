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
}
