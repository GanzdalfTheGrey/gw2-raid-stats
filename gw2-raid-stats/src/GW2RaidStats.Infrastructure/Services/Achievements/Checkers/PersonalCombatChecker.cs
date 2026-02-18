using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Services.Achievements.Progress;

namespace GW2RaidStats.Infrastructure.Services.Achievements.Checkers;

/// <summary>
/// Checks combat-related personal achievements:
/// - The Carry (25%+ of squad DPS)
/// - Immortal (10 consecutive kills without dying)
/// - Clutch Player (survive when 5+ died)
/// - Speed Demon (guild record kill time)
/// - Witness Me (only one alive when boss dies)
/// - Guardian Angel (most resurrects 5+ times)
/// - CC Champion (most CC 10+ times)
/// - The Enabler (highest boon DPS 25+ times)
/// - Ambulance (5+ resurrects in single encounter)
/// </summary>
public class PersonalCombatChecker : IAchievementChecker
{
    private readonly RaidStatsDb _db;
    private readonly AchievementAwardService _awardService;
    private readonly PlayerHistoryCalculator _historyCalculator;

    // Achievement baseline date - only count encounters from 2025 onwards for certain achievements
    private static readonly DateTimeOffset AchievementBaselineDate = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public PersonalCombatChecker(
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

        // Only check for successful kills
        if (!context.Encounter.Success) return unlocks;

        var encounter = context.Encounter;
        var earnedCodes = new Dictionary<Guid, HashSet<string>>();

        // Get earned codes for all guild members in this encounter
        foreach (var player in context.GuildMembers)
        {
            earnedCodes[player.PlayerId] = await _awardService.GetEarnedCodesAsync(player.PlayerId, ct);
        }

        // Check achievements for each guild member
        foreach (var player in context.GuildMembers)
        {
            var earned = earnedCodes[player.PlayerId];

            // The Carry - 25%+ of squad DPS
            if (!earned.Contains("the_carry"))
            {
                var unlock = await CheckCarryAsync(player, context.Players, encounter, ct);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Clutch Player - survive when 5+ died
            if (!earned.Contains("clutch_player"))
            {
                var unlock = CheckClutchPlayer(player, context.Players, encounter);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Speed Demon - guild record kill time
            if (!earned.Contains("speed_demon"))
            {
                var unlock = await CheckSpeedDemonAsync(player, encounter, ct);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Immortal - 10 consecutive kills without dying
            if (!earned.Contains("immortal"))
            {
                var unlock = await CheckImmortalAsync(player.PlayerId, encounter.EncounterTime, ct);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Witness Me - only one alive when boss dies
            if (!earned.Contains("witness_me"))
            {
                var unlock = CheckWitnessMe(player, context.Players, encounter);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Support achievements
            if (!earned.Contains("guardian_angel"))
            {
                var unlock = await CheckGuardianAngelAsync(player, context.Players, encounter, ct);
                if (unlock != null) unlocks.Add(unlock);
            }

            if (!earned.Contains("cc_champion"))
            {
                var unlock = await CheckCCChampionAsync(player, context.Players, encounter, ct);
                if (unlock != null) unlocks.Add(unlock);
            }

            if (!earned.Contains("the_enabler"))
            {
                var unlock = await CheckEnablerAsync(player, context.Players, encounter, ct);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Ambulance - 5+ resurrects in single encounter
            if (!earned.Contains("ambulance"))
            {
                var unlock = CheckAmbulance(player, encounter);
                if (unlock != null) unlocks.Add(unlock);
            }
        }

        return unlocks;
    }

    private Task<AchievementUnlock?> CheckCarryAsync(
        PlayerEncounterData player,
        List<PlayerEncounterData> allPlayers,
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        // Require at least 8 players for a valid raid squad
        if (allPlayers.Count < 8) return Task.FromResult<AchievementUnlock?>(null);

        var totalSquadDps = EncounterStatsCalculator.GetTotalSquadDps(allPlayers);
        if (totalSquadDps == 0) return Task.FromResult<AchievementUnlock?>(null);

        var playerShare = EncounterStatsCalculator.GetPlayerDpsShare(player.Dps, totalSquadDps);

        if (playerShare >= 25)
        {
            return Task.FromResult<AchievementUnlock?>(new AchievementUnlock(
                "the_carry",
                player.PlayerId,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    dps = player.Dps,
                    total_squad_dps = totalSquadDps,
                    squad_size = allPlayers.Count,
                    share = Math.Round(playerShare, 1)
                },
                encounter.EncounterTime
            ));
        }

        return Task.FromResult<AchievementUnlock?>(null);
    }

    private AchievementUnlock? CheckClutchPlayer(
        PlayerEncounterData player,
        List<PlayerEncounterData> allPlayers,
        Database.Entities.EncounterEntity encounter)
    {
        if (EncounterStatsCalculator.IsClutchPlayer(player, allPlayers))
        {
            var squadDeaths = EncounterStatsCalculator.GetSquadDeathsExcluding(allPlayers, player.PlayerId);
            return new AchievementUnlock(
                "clutch_player",
                player.PlayerId,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    squad_deaths = squadDeaths
                },
                encounter.EncounterTime
            );
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckSpeedDemonAsync(
        PlayerEncounterData player,
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        // Only apply to 2025+ encounters
        if (encounter.EncounterTime < AchievementBaselineDate) return null;

        // Check if this is the fastest kill for this boss/CM combo (only considering 2025+ kills)
        var fasterKills = await _db.Encounters
            .Where(e => e.TriggerId == encounter.TriggerId
                     && e.IsCM == encounter.IsCM
                     && e.Success
                     && e.EncounterTime >= AchievementBaselineDate
                     && e.DurationMs < encounter.DurationMs)
            .AnyAsync(ct);

        if (!fasterKills)
        {
            return new AchievementUnlock(
                "speed_demon",
                player.PlayerId,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    time_ms = encounter.DurationMs
                },
                encounter.EncounterTime
            );
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckImmortalAsync(
        Guid playerId,
        DateTimeOffset encounterTime,
        CancellationToken ct)
    {
        var deathlessStreak = await _historyCalculator.GetDeathlessStreakAtTimeAsync(playerId, encounterTime, ct);

        if (deathlessStreak >= 10)
        {
            return new AchievementUnlock(
                "immortal",
                playerId,
                new { streak = 10 },
                encounterTime
            );
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckGuardianAngelAsync(
        PlayerEncounterData player,
        List<PlayerEncounterData> allPlayers,
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        // Check if player has most resurrects in this encounter
        if (!EncounterStatsCalculator.HasMostResurrects(player, allPlayers))
            return null;

        // Count total times with most resurrects
        var count = await _historyCalculator.GetMostResurrectsCountAsync(player.PlayerId, ct);
        if (count >= 5)
        {
            return new AchievementUnlock(
                "guardian_angel",
                player.PlayerId,
                new { times = count },
                encounter.EncounterTime
            );
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckCCChampionAsync(
        PlayerEncounterData player,
        List<PlayerEncounterData> allPlayers,
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        // Check if player has most CC in this encounter
        if (!EncounterStatsCalculator.HasMostCC(player, allPlayers))
            return null;

        // Count total times with most CC
        var count = await _historyCalculator.GetMostCCCountAsync(player.PlayerId, ct);
        if (count >= 10)
        {
            return new AchievementUnlock(
                "cc_champion",
                player.PlayerId,
                new { times = count },
                encounter.EncounterTime
            );
        }

        return null;
    }

    private async Task<AchievementUnlock?> CheckEnablerAsync(
        PlayerEncounterData player,
        List<PlayerEncounterData> allPlayers,
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        // Check if player is a boon support and has highest boon DPS
        if (!EncounterStatsCalculator.HasHighestBoonDps(player, allPlayers))
            return null;

        // Count total times with highest boon DPS
        var count = await _historyCalculator.GetHighestBoonDpsCountAsync(player.PlayerId, ct);
        if (count >= 25)
        {
            return new AchievementUnlock(
                "the_enabler",
                player.PlayerId,
                new { times = count },
                encounter.EncounterTime
            );
        }

        return null;
    }

    /// <summary>
    /// Witness Me - Be the only one alive when boss dies (no deaths, everyone else died)
    /// </summary>
    private AchievementUnlock? CheckWitnessMe(
        PlayerEncounterData player,
        List<PlayerEncounterData> allPlayers,
        Database.Entities.EncounterEntity encounter)
    {
        // Player must have survived (0 deaths)
        if (player.Deaths > 0) return null;

        // Need at least 5 players for this to be meaningful
        if (allPlayers.Count < 5) return null;

        // Count how many others died
        var othersWhoDied = allPlayers.Count(p => p.PlayerId != player.PlayerId && p.Deaths > 0);

        // Everyone else (or nearly everyone) must have died
        var otherPlayers = allPlayers.Count - 1;
        if (othersWhoDied >= otherPlayers - 1 && othersWhoDied >= 4) // Allow 1 other survivor max, need at least 4 deaths
        {
            return new AchievementUnlock(
                "witness_me",
                player.PlayerId,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    squad_deaths = othersWhoDied,
                    squad_size = allPlayers.Count
                },
                encounter.EncounterTime
            );
        }

        return null;
    }

    /// <summary>
    /// Ambulance - Resurrect 5+ teammates in a single encounter
    /// </summary>
    private AchievementUnlock? CheckAmbulance(
        PlayerEncounterData player,
        Database.Entities.EncounterEntity encounter)
    {
        if (player.Resurrects >= 5)
        {
            return new AchievementUnlock(
                "ambulance",
                player.PlayerId,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    resurrects = player.Resurrects
                },
                encounter.EncounterTime
            );
        }

        return null;
    }
}
