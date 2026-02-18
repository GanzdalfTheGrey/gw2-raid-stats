using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Services.Achievements.Progress;

namespace GW2RaidStats.Infrastructure.Services.Achievements.Checkers;

/// <summary>
/// Checks shame/fail achievements:
/// - Serial Downer: 5+ downs without dying
/// - Backpack: Die within first minute and still clear
/// - Greedy: Die when boss under 10% HP (requires mechanics data)
/// - Pacifist: Lowest damage on a kill
/// - Oil Change: First to hit oil on Deimos wipe (requires mechanics data)
/// - The Sacrifice: Die during Matthias sacrifice mechanic
/// </summary>
public class PersonalShameChecker : IAchievementChecker
{
    private readonly RaidStatsDb _db;
    private readonly MechanicTracker _mechanicTracker;
    private readonly AchievementAwardService _awardService;

    // Boss trigger IDs
    private const int DeimosTriggerId = 17154;
    private const int GorsevalTriggerId = 15429;
    private const int SabethaTriggerId = 15375;
    private const int MatthiasTriggerId = 16137;
    private const int MatthiasAltTriggerId = 16115;

    public PersonalShameChecker(
        RaidStatsDb db,
        MechanicTracker mechanicTracker,
        AchievementAwardService awardService)
    {
        _db = db;
        _mechanicTracker = mechanicTracker;
        _awardService = awardService;
    }

    public async Task<List<AchievementUnlock>> CheckAsync(AchievementCheckContext context, CancellationToken ct)
    {
        var unlocks = new List<AchievementUnlock>();
        var encounter = context.Encounter;

        // Get earned codes for all guild members
        var earnedCodes = new Dictionary<Guid, HashSet<string>>();
        foreach (var player in context.GuildMembers)
        {
            earnedCodes[player.PlayerId] = await _awardService.GetEarnedCodesAsync(player.PlayerId, ct);
        }

        foreach (var player in context.GuildMembers)
        {
            var earned = earnedCodes[player.PlayerId];

            // Serial Downer - 5+ downs without dying (successful kill)
            if (!earned.Contains("serial_downer") && encounter.Success)
            {
                var unlock = CheckSerialDowner(player, encounter);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Backpack - Die early but still clear (successful kill only)
            if (!earned.Contains("backpack") && encounter.Success)
            {
                var unlock = await CheckBackpackAsync(player, encounter, ct);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Greedy - Die when boss under 10% HP (successful kill only)
            // Requires mechanics data to know exact death timing - using approximation
            if (!earned.Contains("greedy") && encounter.Success)
            {
                var unlock = await CheckGreedyAsync(player, encounter, ct);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Pacifist - Lowest DPS in a successful kill
            if (!earned.Contains("pacifist") && encounter.Success)
            {
                var unlock = CheckPacifist(player, context.Players, encounter);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Oil Change - First to hit oil on Deimos wipe
            if (!earned.Contains("oil_change") && !encounter.Success && encounter.TriggerId == DeimosTriggerId)
            {
                var unlock = await CheckOilChangeAsync(player, encounter, ct);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Breakfast Special - Get egged by Gorseval AND die to Sabetha flame wall in same session
            // Only check when we're on Sabetha (so we can look back at Gorseval in same session)
            if (!earned.Contains("breakfast_special") && encounter.TriggerId == SabethaTriggerId)
            {
                var unlock = await CheckBreakfastSpecialAsync(player, encounter, ct);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Glass Cannon (Without the Cannon) - 3+ downs while doing less DPS than a boon DPS
            if (!earned.Contains("glass_cannon"))
            {
                var unlock = CheckGlassCannon(player, context.Players, encounter);
                if (unlock != null) unlocks.Add(unlock);
            }

            // Just GG Already - Last alive on a wipe for 5+ seconds
            if (!earned.Contains("just_gg") && !encounter.Success)
            {
                var unlock = await CheckJustGGAsync(player, context.Players, encounter, ct);
                if (unlock != null) unlocks.Add(unlock);
            }

            // The Sacrifice - Die during Matthias sacrifice mechanic
            if (!earned.Contains("the_sacrifice") &&
                (encounter.TriggerId == MatthiasTriggerId || encounter.TriggerId == MatthiasAltTriggerId))
            {
                var unlock = await CheckTheSacrificeAsync(player, encounter, ct);
                if (unlock != null) unlocks.Add(unlock);
            }
        }

        return unlocks;
    }

    /// <summary>
    /// Serial Downer - Go downstate 5+ times without dying
    /// </summary>
    private AchievementUnlock? CheckSerialDowner(PlayerEncounterData player, Database.Entities.EncounterEntity encounter)
    {
        if (player.Downs >= 5 && player.Deaths == 0)
        {
            return new AchievementUnlock(
                "serial_downer",
                player.PlayerId,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    downs = player.Downs
                },
                encounter.EncounterTime
            );
        }
        return null;
    }

    /// <summary>
    /// Backpack - Die within first minute and still clear
    /// Uses mechanics data to find actual death time
    /// </summary>
    private async Task<AchievementUnlock?> CheckBackpackAsync(
        PlayerEncounterData player,
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        if (player.Deaths == 0) return null;

        // Fight must be longer than 1 minute to be meaningful
        if (encounter.DurationMs <= 60000) return null;

        // Find the first death event for this player
        var firstDeath = await _db.MechanicEvents
            .Where(m => m.EncounterId == encounter.Id)
            .Where(m => m.PlayerId == player.PlayerId)
            .Where(m => m.MechanicName.Contains("Dead") || m.MechanicFullName.Contains("Dead"))
            .OrderBy(m => m.EventTimeMs)
            .FirstOrDefaultAsync(ct);

        // Check if they died within the first minute (60000ms)
        if (firstDeath != null && firstDeath.EventTimeMs <= 60000)
        {
            return new AchievementUnlock(
                "backpack",
                player.PlayerId,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    death_time_ms = firstDeath.EventTimeMs,
                    fight_duration_ms = encounter.DurationMs
                },
                encounter.EncounterTime
            );
        }
        return null;
    }

    /// <summary>
    /// Greedy - Die when boss is under 10% HP
    /// This checks for late deaths using mechanics data if available
    /// </summary>
    private async Task<AchievementUnlock?> CheckGreedyAsync(
        PlayerEncounterData player,
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        if (player.Deaths == 0) return null;

        // Look for "Dead" mechanic events for this player
        // and check if they happened when boss was low (late in the fight)
        var deathEvents = await _db.MechanicEvents
            .Where(m => m.EncounterId == encounter.Id)
            .Where(m => m.PlayerId == player.PlayerId)
            .Where(m => m.MechanicName.Contains("Dead") || m.MechanicFullName.Contains("Dead"))
            .OrderByDescending(m => m.EventTimeMs)
            .FirstOrDefaultAsync(ct);

        if (deathEvents != null)
        {
            // Check if death happened in last 10% of fight time (approximation for boss under 10%)
            var fightDurationMs = encounter.DurationMs;
            var lastTenPercentStart = fightDurationMs * 0.9;

            if (deathEvents.EventTimeMs >= lastTenPercentStart)
            {
                return new AchievementUnlock(
                    "greedy",
                    player.PlayerId,
                    new
                    {
                        encounter_id = encounter.Id,
                        boss = encounter.BossName,
                        death_time_ms = deathEvents.EventTimeMs,
                        fight_duration_ms = fightDurationMs
                    },
                    encounter.EncounterTime
                );
            }
        }

        return null;
    }

    /// <summary>
    /// Pacifist - Do the least damage on a boss kill
    /// Requires at least 8 players to be meaningful
    /// </summary>
    private AchievementUnlock? CheckPacifist(
        PlayerEncounterData player,
        List<PlayerEncounterData> allPlayers,
        Database.Entities.EncounterEntity encounter)
    {
        // Need at least 8 players for this to be meaningful
        if (allPlayers.Count < 8) return null;

        var minDps = allPlayers.Min(p => p.Dps);

        // Only award if they have the absolute minimum and it's not a tie at 0
        if (player.Dps == minDps && player.Dps > 0)
        {
            // Check they're the only one with this DPS (not a tie)
            var playersWithMinDps = allPlayers.Count(p => p.Dps == minDps);
            if (playersWithMinDps == 1)
            {
                return new AchievementUnlock(
                    "pacifist",
                    player.PlayerId,
                    new
                    {
                        encounter_id = encounter.Id,
                        boss = encounter.BossName,
                        dps = player.Dps,
                        squad_size = allPlayers.Count
                    },
                    encounter.EncounterTime
                );
            }
        }

        return null;
    }

    /// <summary>
    /// Oil Change - Be the first to step in oil on Deimos on a failed run
    /// </summary>
    private async Task<AchievementUnlock?> CheckOilChangeAsync(
        PlayerEncounterData player,
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        // Look for first oil mechanic event on Deimos
        // Oil mechanics in Elite Insights are typically named "Weak Minded" or contain "Oil"
        var firstOilEvent = await _db.MechanicEvents
            .Where(m => m.EncounterId == encounter.Id)
            .Where(m => m.MechanicName.Contains("Oil") ||
                       m.MechanicFullName.Contains("Oil") ||
                       m.MechanicName == "Weak Minded")
            .OrderBy(m => m.EventTimeMs)
            .FirstOrDefaultAsync(ct);

        if (firstOilEvent != null && firstOilEvent.PlayerId == player.PlayerId)
        {
            return new AchievementUnlock(
                "oil_change",
                player.PlayerId,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    time_ms = firstOilEvent.EventTimeMs
                },
                encounter.EncounterTime
            );
        }

        return null;
    }

    /// <summary>
    /// Glass Cannon (Without the Cannon) - Go down 3+ times while doing less DPS than a boon DPS (must be pure DPS role)
    /// </summary>
    private AchievementUnlock? CheckGlassCannon(
        PlayerEncounterData player,
        List<PlayerEncounterData> allPlayers,
        Database.Entities.EncounterEntity encounter)
    {
        // Must be a pure DPS (not a support/healer)
        if (player.Role != "pure_dps") return null;

        // Need at least 3 downs
        if (player.Downs < 3) return null;

        // Find boon DPS players (dps_alac or dps_quick role)
        var boonDpsPlayers = allPlayers
            .Where(p => (p.Role == "dps_alac" || p.Role == "dps_quick") && p.PlayerId != player.PlayerId)
            .ToList();

        if (boonDpsPlayers.Count == 0) return null;

        // Check if player's DPS is lower than the lowest boon DPS
        var lowestBoonDps = boonDpsPlayers.Min(p => p.Dps);

        if (player.Dps < lowestBoonDps)
        {
            return new AchievementUnlock(
                "glass_cannon",
                player.PlayerId,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    downs = player.Downs,
                    player_dps = player.Dps,
                    lowest_boon_dps = lowestBoonDps
                },
                encounter.EncounterTime
            );
        }

        return null;
    }

    /// <summary>
    /// Just GG Already - Be the last one alive on a wipe for 5+ seconds
    /// Checks ALL players in encounter (including pugs) to ensure guild member was truly last alive
    /// </summary>
    private async Task<AchievementUnlock?> CheckJustGGAsync(
        PlayerEncounterData player,
        List<PlayerEncounterData> allPlayers,
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        // Get ALL players in this encounter (including pugs)
        var allEncounterPlayerIds = await _db.PlayerEncounters
            .Where(pe => pe.EncounterId == encounter.Id)
            .Select(pe => pe.PlayerId)
            .ToListAsync(ct);

        // Get all death events for this encounter (all players)
        var deathEvents = await _db.MechanicEvents
            .Where(m => m.EncounterId == encounter.Id)
            .Where(m => m.MechanicName.Contains("Dead") || m.MechanicFullName.Contains("Dead"))
            .OrderBy(m => m.EventTimeMs)
            .ToListAsync(ct);

        if (deathEvents.Count < 2) return null;

        // Group by player to get their first death time
        var playerDeaths = deathEvents
            .GroupBy(d => d.PlayerId)
            .Select(g => new { PlayerId = g.Key, FirstDeathMs = g.Min(d => d.EventTimeMs) })
            .OrderBy(d => d.FirstDeathMs)
            .ToList();

        // Find players who didn't die at all
        var playersThatDied = playerDeaths.Select(d => d.PlayerId).ToHashSet();
        var playersThatSurvived = allEncounterPlayerIds.Where(id => !playersThatDied.Contains(id)).ToList();

        // If anyone else survived (didn't die), this player can't be the only one alive
        if (playersThatSurvived.Count > 1) return null;
        if (playersThatSurvived.Count == 1 && playersThatSurvived[0] != player.PlayerId) return null;

        // Need at least 2 players who died for comparison
        if (playerDeaths.Count < 2) return null;

        var playerDeath = playerDeaths.FirstOrDefault(d => d.PlayerId == player.PlayerId);
        var secondToLastDeath = playerDeaths[^2]; // Second to last

        // If player didn't die (and no one else survived per checks above), they were last alive
        if (playerDeath == null)
        {
            var timeSurvived = encounter.DurationMs - secondToLastDeath.FirstDeathMs;
            if (timeSurvived >= 5000)
            {
                return new AchievementUnlock(
                    "just_gg",
                    player.PlayerId,
                    new
                    {
                        encounter_id = encounter.Id,
                        boss = encounter.BossName,
                        survived_alone_ms = timeSurvived
                    },
                    encounter.EncounterTime
                );
            }
        }
        else
        {
            // Player died - check if they were the last to die
            var lastDeath = playerDeaths[^1];
            if (lastDeath.PlayerId == player.PlayerId)
            {
                var timeSurvivedAlone = playerDeath.FirstDeathMs - secondToLastDeath.FirstDeathMs;
                if (timeSurvivedAlone >= 5000)
                {
                    return new AchievementUnlock(
                        "just_gg",
                        player.PlayerId,
                        new
                        {
                            encounter_id = encounter.Id,
                            boss = encounter.BossName,
                            survived_alone_ms = timeSurvivedAlone
                        },
                        encounter.EncounterTime
                    );
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Breakfast Special - Get egged by Gorseval AND die to flame wall on Sabetha in same session
    /// Flame wall kills instantly without downing, so we look for deaths without a preceding down event.
    /// Excludes deaths in last 10 seconds to filter out /gg.
    /// </summary>
    private async Task<AchievementUnlock?> CheckBreakfastSpecialAsync(
        PlayerEncounterData player,
        Database.Entities.EncounterEntity sabethaEncounter,
        CancellationToken ct)
    {
        if (player.Deaths == 0) return null;

        // Get death events for this player, excluding last 10 seconds (filter out /gg)
        var cutoffTime = sabethaEncounter.DurationMs - 10000;
        var deathEvents = await _db.MechanicEvents
            .Where(m => m.EncounterId == sabethaEncounter.Id)
            .Where(m => m.PlayerId == player.PlayerId)
            .Where(m => m.MechanicName.Contains("Dead") || m.MechanicFullName.Contains("Dead"))
            .Where(m => m.EventTimeMs < cutoffTime)
            .Select(m => m.EventTimeMs)
            .ToListAsync(ct);

        if (deathEvents.Count == 0) return null;

        // Get down events for this player
        var downEvents = await _db.MechanicEvents
            .Where(m => m.EncounterId == sabethaEncounter.Id)
            .Where(m => m.PlayerId == player.PlayerId)
            .Where(m => m.MechanicName.Contains("Downed") || m.MechanicFullName.Contains("Downed"))
            .Select(m => m.EventTimeMs)
            .ToListAsync(ct);

        // Check if any death was instant (no down event within 5 seconds before death)
        var hasInstantDeath = deathEvents.Any(deathTime =>
            !downEvents.Any(downTime => downTime >= deathTime - 5000 && downTime < deathTime));

        if (!hasInstantDeath) return null;

        // Find Gorseval encounters in the same session (within 4 hours before Sabetha)
        var sessionStart = sabethaEncounter.EncounterTime.AddHours(-4);
        var gorsevalEncounters = await _db.Encounters
            .Where(e => e.TriggerId == GorsevalTriggerId)
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime <= sabethaEncounter.EncounterTime)
            .Select(e => e.Id)
            .ToListAsync(ct);

        if (gorsevalEncounters.Count == 0) return null;

        // Check if player got egged on any Gorseval encounter in this session
        // Gorseval egg mechanic is "Ghastly Prison" or contains "Egg"
        var gotEgged = await _db.MechanicEvents
            .Where(m => gorsevalEncounters.Contains(m.EncounterId))
            .Where(m => m.PlayerId == player.PlayerId)
            .Where(m => m.MechanicName.Contains("Egg") ||
                       m.MechanicFullName.Contains("Egg") ||
                       m.MechanicName == "Ghastly Prison" ||
                       m.MechanicFullName.Contains("Ghastly Prison"))
            .AnyAsync(ct);

        if (gotEgged)
        {
            return new AchievementUnlock(
                "breakfast_special",
                player.PlayerId,
                new
                {
                    sabetha_encounter_id = sabethaEncounter.Id,
                    session_date = sabethaEncounter.EncounterTime.Date
                },
                sabethaEncounter.EncounterTime
            );
        }

        return null;
    }

    /// <summary>
    /// The Sacrifice - Die during Matthias sacrifice mechanic
    /// The sacrifice mechanic in Elite Insights is typically called "Sacrifice" or "Unbalanced"
    /// </summary>
    private async Task<AchievementUnlock?> CheckTheSacrificeAsync(
        PlayerEncounterData player,
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        if (player.Deaths == 0) return null;

        // Check if player was involved in sacrifice mechanic
        // In Elite Insights, the sacrifice mechanic is tracked as "Sacrifice" or related
        var wasSacrificed = await _db.MechanicEvents
            .Where(m => m.EncounterId == encounter.Id)
            .Where(m => m.PlayerId == player.PlayerId)
            .Where(m => m.MechanicName.Contains("Sacrifice") ||
                       m.MechanicFullName.Contains("Sacrifice") ||
                       m.MechanicName.Contains("Unbalanced") ||
                       m.MechanicFullName.Contains("Unbalanced"))
            .AnyAsync(ct);

        if (wasSacrificed)
        {
            return new AchievementUnlock(
                "the_sacrifice",
                player.PlayerId,
                new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName
                },
                encounter.EncounterTime
            );
        }

        return null;
    }
}
