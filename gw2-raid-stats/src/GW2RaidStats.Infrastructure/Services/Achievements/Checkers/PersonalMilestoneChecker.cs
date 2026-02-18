using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using GW2RaidStats.Infrastructure.Services.Achievements.Progress;

namespace GW2RaidStats.Infrastructure.Services.Achievements.Checkers;

/// <summary>
/// Checks milestone/progress personal achievements:
/// - Wing Master (all roles on all bosses in a wing)
/// - Completion achievements (completion, legendary_raider, wing_8_clear, wing_8_cm_clear, guardians_glade_*)
/// - Spec Diversity (versatile, jack_of_all_trades, class_completionist, master_of_one)
/// - Social achievements (dynamic_duo, trio, guild_pride)
/// - Dedication (the_regular, dedicated)
/// - Growth (keeping_up)
/// </summary>
public class PersonalMilestoneChecker : IAchievementChecker
{
    private readonly RaidStatsDb _db;
    private readonly AchievementAwardService _awardService;
    private readonly PlayerHistoryCalculator _historyCalculator;

    // Strike mission trigger IDs
    private const int GuardiansGladeTriggerId = 27124;

    public PersonalMilestoneChecker(
        RaidStatsDb db,
        AchievementAwardService awardService,
        PlayerHistoryCalculator historyCalculator)
    {
        _db = db;
        _awardService = awardService;
        _historyCalculator = historyCalculator;
    }

    public async Task<List<AchievementUnlock>> CheckAsync(AchievementCheckContext context, CancellationToken ct)
    {
        var unlocks = new List<AchievementUnlock>();

        // Only check for successful kills for most milestone achievements
        if (!context.Encounter.Success) return unlocks;

        var encounter = context.Encounter;

        // Check achievements for each guild member
        foreach (var player in context.GuildMembers)
        {
            var earned = await _awardService.GetEarnedCodesAsync(player.PlayerId, ct);

            // Wing Master achievements
            await CheckWingMasterAsync(player, encounter, earned, unlocks, ct);

            // Completion achievements
            await CheckCompletionAsync(player.PlayerId, encounter, earned, unlocks, ct);

            // Spec Diversity achievements
            await CheckSpecDiversityAsync(player, encounter, earned, unlocks, ct);

            // Social achievements
            await CheckSocialAsync(player.PlayerId, encounter, earned, unlocks, ct);

            // Guild Pride
            if (!earned.Contains("guild_pride"))
            {
                var unlock = CheckGuildPride(player, context, encounter);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Dedication achievements
            await CheckDedicationAsync(player.PlayerId, encounter, earned, unlocks, ct);

            // Growth - Keeping Up
            await CheckKeepingUpAsync(player.PlayerId, encounter, earned, unlocks, ct);
        }

        return unlocks;
    }

    #region Wing Master

    private async Task CheckWingMasterAsync(
        PlayerEncounterData player,
        EncounterEntity encounter,
        HashSet<string> earned,
        List<AchievementUnlock> unlocks,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(player.Role)) return;

        // Find which wing this boss belongs to
        var normalizedTriggerId = AchievementDefinitions.NormalizeTriggerId(encounter.TriggerId);
        var wingNum = AchievementDefinitions.WingMasterBosses
            .FirstOrDefault(kvp => kvp.Value.Contains(normalizedTriggerId)).Key;

        if (wingNum == 0) return;

        var code = $"wing_{wingNum}_master";
        if (earned.Contains(code)) return;

        // Check if all roles are complete for all bosses in this wing
        var bosses = AchievementDefinitions.WingMasterBosses[wingNum];
        var allComplete = true;

        foreach (var bossId in bosses)
        {
            var matchingTriggerIds = bossId == 16137
                ? new[] { 16137, 16115 }
                : new[] { bossId };

            foreach (var role in AchievementDefinitions.RequiredRoles)
            {
                var hasRole = await _db.PlayerEncounters
                    .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
                    .Where(x => x.pe.PlayerId == player.PlayerId)
                    .Where(x => matchingTriggerIds.Contains(x.e.TriggerId) && x.e.Success)
                    .Where(x => x.pe.Role == role)
                    .AnyAsync(ct);

                if (!hasRole)
                {
                    allComplete = false;
                    break;
                }
            }
            if (!allComplete) break;
        }

        if (allComplete)
        {
            unlocks.Add(new AchievementUnlock(
                code,
                player.PlayerId,
                new { wing = wingNum },
                encounter.EncounterTime
            ));
        }
    }

    #endregion

    #region Completion

    private async Task CheckCompletionAsync(
        Guid playerId,
        EncounterEntity encounter,
        HashSet<string> earned,
        List<AchievementUnlock> unlocks,
        CancellationToken ct)
    {
        // Completion - kill every boss in Wings 1-7
        if (!earned.Contains("completion"))
        {
            var unlock = await CheckCompletionAchievementAsync(playerId, encounter, ct);
            if (unlock != null) unlocks.Add(unlock);
        }

        // Legendary Raider - kill every CM boss
        if (!earned.Contains("legendary_raider"))
        {
            var unlock = await CheckLegendaryRaiderAsync(playerId, encounter, ct);
            if (unlock != null) unlocks.Add(unlock);
        }

        // Wing 8 Clear
        if (!earned.Contains("wing_8_clear"))
        {
            var unlock = await CheckWing8ClearAsync(playerId, encounter, ct);
            if (unlock != null) unlocks.Add(unlock);
        }

        // Wing 8 CM Clear
        if (!earned.Contains("wing_8_cm_clear"))
        {
            var unlock = await CheckWing8CMClearAsync(playerId, encounter, ct);
            if (unlock != null) unlocks.Add(unlock);
        }

        // Guardian's Glade Clear
        if (!earned.Contains("guardians_glade_clear") && encounter.TriggerId == GuardiansGladeTriggerId)
        {
            unlocks.Add(new AchievementUnlock(
                "guardians_glade_clear",
                playerId,
                new { encounter_id = encounter.Id },
                encounter.EncounterTime
            ));
        }

        // Guardian's Glade Flawless (Scalding Survivor) - no Scalding Wave hits
        if (!earned.Contains("guardians_glade_flawless") && encounter.TriggerId == GuardiansGladeTriggerId)
        {
            var unlock = await CheckGuardiansGladeFlawlessAsync(playerId, encounter, ct);
            if (unlock != null) unlocks.Add(unlock);
        }
    }

    private async Task<AchievementUnlock?> CheckCompletionAchievementAsync(
        Guid playerId,
        EncounterEntity encounter,
        CancellationToken ct)
    {
        var w1to7Bosses = AchievementDefinitions.WingMasterBosses
            .Where(kvp => kvp.Key <= 7)
            .SelectMany(kvp => kvp.Value)
            .Select(AchievementDefinitions.NormalizeTriggerId)
            .Distinct()
            .ToHashSet();

        var rawKills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => w1to7Bosses.Contains(x.e.TriggerId) || x.e.TriggerId == 16115)
            .Select(x => new { x.e.TriggerId, x.e.EncounterTime })
            .ToListAsync(ct);

        var firstKillsByBoss = rawKills
            .GroupBy(x => AchievementDefinitions.NormalizeTriggerId(x.TriggerId))
            .Select(g => new { BossId = g.Key, FirstKill = g.Min(x => x.EncounterTime) })
            .ToList();

        if (w1to7Bosses.All(b => firstKillsByBoss.Any(fk => fk.BossId == b)))
        {
            var achievedAt = firstKillsByBoss.Max(fk => fk.FirstKill);
            return new AchievementUnlock("completion", playerId, null, achievedAt);
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckLegendaryRaiderAsync(
        Guid playerId,
        EncounterEntity encounter,
        CancellationToken ct)
    {
        var w1to7CmBosses = AchievementDefinitions.Wings1To7CMBosses;

        var rawCmKills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success && x.e.IsCM)
            .Where(x => w1to7CmBosses.Contains(x.e.TriggerId))
            .Select(x => new { x.e.TriggerId, x.e.EncounterTime })
            .ToListAsync(ct);

        var firstCmKillsByBoss = rawCmKills
            .GroupBy(x => x.TriggerId)
            .Select(g => new { BossId = g.Key, FirstKill = g.Min(x => x.EncounterTime) })
            .ToList();

        if (w1to7CmBosses.All(b => firstCmKillsByBoss.Any(fk => fk.BossId == b)))
        {
            var achievedAt = firstCmKillsByBoss.Max(fk => fk.FirstKill);
            return new AchievementUnlock("legendary_raider", playerId, null, achievedAt);
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckWing8ClearAsync(
        Guid playerId,
        EncounterEntity encounter,
        CancellationToken ct)
    {
        var w8Bosses = AchievementDefinitions.WingMasterBosses[8];

        var firstW8KillsByBoss = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success && !x.e.IsCM)
            .Where(x => w8Bosses.Contains(x.e.TriggerId))
            .GroupBy(x => x.e.TriggerId)
            .Select(g => new { BossId = g.Key, FirstKill = g.Min(x => x.e.EncounterTime) })
            .ToListAsync(ct);

        if (w8Bosses.All(b => firstW8KillsByBoss.Any(fk => fk.BossId == b)))
        {
            var achievedAt = firstW8KillsByBoss.Max(fk => fk.FirstKill);
            return new AchievementUnlock("wing_8_clear", playerId, null, achievedAt);
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckWing8CMClearAsync(
        Guid playerId,
        EncounterEntity encounter,
        CancellationToken ct)
    {
        var greerCmKill = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => x.e.TriggerId == 26725 && x.e.IsCM)
            .Select(x => (DateTimeOffset?)x.e.EncounterTime)
            .MinAsync(ct);

        var decimaCmKill = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => x.e.TriggerId == 26867)
            .Select(x => (DateTimeOffset?)x.e.EncounterTime)
            .MinAsync(ct);

        var uraCmKill = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => x.e.TriggerId == 26712 && x.e.IsCM)
            .Select(x => (DateTimeOffset?)x.e.EncounterTime)
            .MinAsync(ct);

        if (greerCmKill.HasValue && decimaCmKill.HasValue && uraCmKill.HasValue)
        {
            var achievedAt = new[] { greerCmKill.Value, decimaCmKill.Value, uraCmKill.Value }.Max();
            return new AchievementUnlock("wing_8_cm_clear", playerId, null, achievedAt);
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckGuardiansGladeFlawlessAsync(
        Guid playerId,
        EncounterEntity encounter,
        CancellationToken ct)
    {
        // Check if player was hit by Scalding Wave mechanic
        var wasHitByScaldingWave = await _db.MechanicEvents
            .Where(m => m.EncounterId == encounter.Id)
            .Where(m => m.PlayerId == playerId)
            .Where(m => m.MechanicName.Contains("Scalding") ||
                       m.MechanicFullName.Contains("Scalding") ||
                       m.MechanicName.Contains("scalding"))
            .AnyAsync(ct);

        if (!wasHitByScaldingWave)
        {
            return new AchievementUnlock(
                "guardians_glade_flawless",
                playerId,
                new { encounter_id = encounter.Id },
                encounter.EncounterTime
            );
        }

        return null;
    }

    #endregion

    #region Spec Diversity

    private async Task CheckSpecDiversityAsync(
        PlayerEncounterData player,
        EncounterEntity encounter,
        HashSet<string> earned,
        List<AchievementUnlock> unlocks,
        CancellationToken ct)
    {
        var topBoss = await _historyCalculator.GetTopBossBySpecDiversityAsync(player.PlayerId, ct);
        var maxSpecsOnBoss = topBoss?.specCount ?? 0;

        // Versatile - 10 specs on one boss
        if (!earned.Contains("versatile") && maxSpecsOnBoss >= 10)
        {
            unlocks.Add(new AchievementUnlock(
                "versatile",
                player.PlayerId,
                new { boss_name = topBoss!.Value.bossName, spec_count = maxSpecsOnBoss },
                encounter.EncounterTime
            ));
        }

        // Jack of All Trades - 20 specs on one boss
        if (!earned.Contains("jack_of_all_trades") && maxSpecsOnBoss >= 20)
        {
            unlocks.Add(new AchievementUnlock(
                "jack_of_all_trades",
                player.PlayerId,
                new { boss_name = topBoss!.Value.bossName, spec_count = maxSpecsOnBoss },
                encounter.EncounterTime
            ));
        }

        // Class Completionist
        if (!earned.Contains("class_completionist"))
        {
            var unlock = await CheckClassCompletionistAsync(player.PlayerId, encounter, ct);
            if (unlock != null) unlocks.Add(unlock);
        }

        // Master of One - 100 kills on same spec
        if (!earned.Contains("master_of_one"))
        {
            var unlock = await CheckMasterOfOneAsync(player.PlayerId, encounter, ct);
            if (unlock != null) unlocks.Add(unlock);
        }
    }

    private async Task<AchievementUnlock?> CheckClassCompletionistAsync(
        Guid playerId,
        EncounterEntity encounter,
        CancellationToken ct)
    {
        var specKills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .Select(x => new { x.pe.Profession, x.e.BossName, x.e.Id, x.e.EncounterTime })
            .ToListAsync(ct);

        var killsByBoss = specKills.GroupBy(x => x.BossName).ToList();

        foreach (var (profession, eliteSpecs) in AchievementDefinitions.EliteSpecsByProfession)
        {
            foreach (var bossGroup in killsByBoss)
            {
                var specsOnThisBoss = bossGroup.Select(x => x.Profession).Distinct()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (eliteSpecs.All(spec => specsOnThisBoss.Contains(spec)))
                {
                    var specDetails = eliteSpecs.Select(spec =>
                    {
                        var kill = bossGroup.First(x => x.Profession.Equals(spec, StringComparison.OrdinalIgnoreCase));
                        return new { spec, boss_name = kill.BossName, encounter_id = kill.Id };
                    }).ToList();

                    return new AchievementUnlock(
                        "class_completionist",
                        playerId,
                        new { profession, boss = bossGroup.Key, specs = specDetails },
                        encounter.EncounterTime
                    );
                }
            }
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckMasterOfOneAsync(
        Guid playerId,
        EncounterEntity encounter,
        CancellationToken ct)
    {
        var maxKills = await _historyCalculator.GetMaxKillsOnSingleSpecAsync(playerId, ct);
        if (maxKills < 100) return null;

        var specKills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .Select(x => new { x.pe.Profession, x.e.EncounterTime })
            .ToListAsync(ct);

        var topSpecData = specKills
            .GroupBy(x => x.Profession)
            .Where(g => g.Count() >= 100)
            .Select(g => new
            {
                Spec = g.Key,
                Count = g.Count(),
                AchievedAt = g.OrderBy(x => x.EncounterTime).Skip(99).First().EncounterTime
            })
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();

        if (topSpecData != null)
        {
            return new AchievementUnlock(
                "master_of_one",
                playerId,
                new { spec = topSpecData.Spec, kills = topSpecData.Count },
                topSpecData.AchievedAt
            );
        }

        return null;
    }

    #endregion

    #region Social

    private async Task CheckSocialAsync(
        Guid playerId,
        EncounterEntity encounter,
        HashSet<string> earned,
        List<AchievementUnlock> unlocks,
        CancellationToken ct)
    {
        // Dynamic Duo - 50 bosses with same party member
        if (!earned.Contains("dynamic_duo"))
        {
            var result = await _historyCalculator.GetPartyPartnerKillsWithDateAsync(playerId, 50, ct);
            if (result.HasValue)
            {
                unlocks.Add(new AchievementUnlock(
                    "dynamic_duo",
                    playerId,
                    new { kills = 50 },
                    result.Value.achievedAt
                ));
            }
        }

        // Trio - 25 bosses with same two party members
        if (!earned.Contains("trio"))
        {
            var result = await _historyCalculator.GetPartyTrioKillsWithDateAsync(playerId, 25, ct);
            if (result.HasValue)
            {
                unlocks.Add(new AchievementUnlock(
                    "trio",
                    playerId,
                    new { kills = 25 },
                    result.Value.achievedAt
                ));
            }
        }
    }

    private AchievementUnlock? CheckGuildPride(
        PlayerEncounterData player,
        AchievementCheckContext context,
        EncounterEntity encounter)
    {
        // Guild Pride - all players are guild members (no pugs)
        if (EncounterStatsCalculator.AllGuildMembers(context.Players, context.IncludedAccounts))
        {
            return new AchievementUnlock(
                "guild_pride",
                player.PlayerId,
                new { encounter_id = encounter.Id, boss = encounter.BossName },
                encounter.EncounterTime
            );
        }
        return null;
    }

    #endregion

    #region Dedication & Growth

    private async Task CheckDedicationAsync(
        Guid playerId,
        EncounterEntity encounter,
        HashSet<string> earned,
        List<AchievementUnlock> unlocks,
        CancellationToken ct)
    {
        var sessionDates = await _historyCalculator.GetSessionDatesAsync(playerId, ct);
        var sessionCount = sessionDates.Count;

        // The Regular - 25 sessions
        if (!earned.Contains("the_regular") && sessionCount >= 25)
        {
            var achievedAt = sessionDates[24];
            unlocks.Add(new AchievementUnlock(
                "the_regular",
                playerId,
                new { sessions = 25 },
                new DateTimeOffset(achievedAt, TimeSpan.Zero)
            ));
        }

        // Dedicated - 50 sessions
        if (!earned.Contains("dedicated") && sessionCount >= 50)
        {
            var achievedAt = sessionDates[49];
            unlocks.Add(new AchievementUnlock(
                "dedicated",
                playerId,
                new { sessions = 50 },
                new DateTimeOffset(achievedAt, TimeSpan.Zero)
            ));
        }
    }

    private async Task CheckKeepingUpAsync(
        Guid playerId,
        EncounterEntity encounter,
        HashSet<string> earned,
        List<AchievementUnlock> unlocks,
        CancellationToken ct)
    {
        if (earned.Contains("keeping_up")) return;

        var result = await _historyCalculator.GetPersonalBestAchievedAtAsync(playerId, 5, ct);
        if (result.HasValue)
        {
            unlocks.Add(new AchievementUnlock(
                "keeping_up",
                playerId,
                new { personal_bests = 5 },
                result.Value
            ));
        }
    }

    #endregion
}
