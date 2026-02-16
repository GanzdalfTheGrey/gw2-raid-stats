using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Core;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.Infrastructure.Services.Achievements;

/// <summary>
/// Service for checking, awarding, and querying achievements
/// </summary>
public class AchievementService
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;
    private readonly ILogger<AchievementService> _logger;

    // Threshold for considering someone a boon support (generation % to squad)
    private const decimal BoonSupportThreshold = 10m;

    public AchievementService(
        RaidStatsDb db,
        IncludedPlayerService includedPlayerService,
        ILogger<AchievementService> logger)
    {
        _db = db;
        _includedPlayerService = includedPlayerService;
        _logger = logger;
    }

    /// <summary>
    /// Check achievements after an encounter is imported (incremental check)
    /// </summary>
    public async Task CheckAfterEncounterAsync(Guid encounterId, bool notify = true, CancellationToken ct = default)
    {
        var encounter = await _db.Encounters
            .FirstOrDefaultAsync(e => e.Id == encounterId, ct);

        if (encounter == null) return;

        // Skip ignored encounters (Spirit Race, Statues, etc.)
        if (WingMapping.IsIgnoredEncounter(encounter.BossName))
        {
            return;
        }

        // Get all player encounters for this encounter
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => x.pe.EncounterId == encounterId)
            .ToListAsync(ct);

        // Get included accounts (guild members)
        var includedAccounts = (await _includedPlayerService.GetIncludedAccountNamesAsync(ct)).ToHashSet();

        // Check personal achievements for each guild member in this encounter
        foreach (var pe in playerEncounters.Where(x => includedAccounts.Contains(x.p.AccountName)))
        {
            await CheckPersonalAchievementsAsync(pe.p.Id, pe.pe, encounter, notify, ct);
        }

        // Check guild achievements (only for successful kills with enough guild members)
        if (encounter.Success)
        {
            var guildMemberCount = playerEncounters.Count(x => includedAccounts.Contains(x.p.AccountName));
            if (guildMemberCount >= 5) // Need at least half the squad as guild members
            {
                var playerTuples = playerEncounters.Select(x => (x.pe, x.p)).ToList();
                await CheckGuildAchievementsAsync(encounter, playerTuples, includedAccounts, notify, ct);
            }
        }
    }

    /// <summary>
    /// Full achievement check for a single player (for retroactive scan)
    /// Returns number of new achievements awarded
    /// </summary>
    public async Task<int> CheckAllForPlayerAsync(Guid playerId, bool notify = false, CancellationToken ct = default)
    {
        var startCount = await _db.PlayerAchievements
            .Where(pa => pa.PlayerId == playerId)
            .CountAsync(ct);

        // Get all encounters this player participated in
        var encounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId)
            .OrderBy(x => x.e.EncounterTime)
            .ToListAsync(ct);

        // Check each encounter
        foreach (var data in encounters)
        {
            await CheckPersonalAchievementsAsync(playerId, data.pe, data.e, notify, ct);
        }

        // Check achievements that need aggregate data
        await CheckAggregateAchievementsAsync(playerId, notify, ct);

        var endCount = await _db.PlayerAchievements
            .Where(pa => pa.PlayerId == playerId)
            .CountAsync(ct);

        return endCount - startCount;
    }

    /// <summary>
    /// Get all achievements for a player
    /// </summary>
    public async Task<List<PlayerAchievementDto>> GetPlayerAchievementsAsync(Guid playerId, CancellationToken ct = default)
    {
        var achievements = await _db.PlayerAchievements
            .Where(pa => pa.PlayerId == playerId)
            .OrderByDescending(pa => pa.AchievedAt)
            .ToListAsync(ct);

        // Get total included players (guild members + auto-included)
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var totalIncludedPlayers = includedAccounts.Count;

        var result = new List<PlayerAchievementDto>();
        foreach (var a in achievements)
        {
            var definition = AchievementDefinitions.Personal.FirstOrDefault(d => d.Code == a.AchievementCode);
            if (definition == null) continue;

            var playersWithAchievement = await _db.PlayerAchievements
                .Where(pa => pa.AchievementCode == a.AchievementCode)
                .CountAsync(ct);

            result.Add(new PlayerAchievementDto(
                a.AchievementCode,
                definition.Name,
                definition.Description,
                definition.Category.ToString(),
                a.AchievedAt,
                a.Context,
                playersWithAchievement,
                totalIncludedPlayers
            ));
        }

        return result;
    }

    /// <summary>
    /// Get achievement progress for a player
    /// </summary>
    public async Task<List<AchievementProgressDto>> GetProgressAsync(Guid playerId, CancellationToken ct = default)
    {
        var result = new List<AchievementProgressDto>();

        // Get already earned achievements
        var earnedCodes = (await _db.PlayerAchievements
            .Where(pa => pa.PlayerId == playerId)
            .Select(pa => pa.AchievementCode)
            .ToListAsync(ct))
            .ToHashSet();

        // Wing Master progress
        foreach (var wingDef in AchievementDefinitions.Personal.Where(a => a.Category == AchievementCategory.WingMaster))
        {
            if (earnedCodes.Contains(wingDef.Code)) continue;

            var wingNum = ExtractWingNumber(wingDef.Code);
            if (wingNum == null) continue;

            var progress = await GetWingMasterProgressAsync(playerId, wingNum.Value, ct);
            result.Add(new AchievementProgressDto(
                wingDef.Code,
                wingDef.Name,
                wingDef.Description,
                wingDef.Category.ToString(),
                progress.Current,
                progress.Required,
                progress.ProgressText
            ));
        }

        // Spec Diversity - Versatile (10 specs on one boss)
        if (!earnedCodes.Contains("versatile"))
        {
            var topBoss = await GetTopBossBySpecDiversityAsync(playerId, ct);
            var maxSpecsOnBoss = topBoss?.specCount ?? 0;
            var progressText = topBoss != null
                ? $"{Math.Min(maxSpecsOnBoss, 10)}/10 specs on {topBoss.Value.bossName}"
                : $"0/10 elite specs";
            result.Add(new AchievementProgressDto("versatile", "Versatile", "Complete a boss on 10 different elite specs",
                "SpecDiversity", Math.Min(maxSpecsOnBoss, 10), 10, progressText));
        }

        // Spec Diversity - Jack of All Trades (20 specs on one boss)
        if (!earnedCodes.Contains("jack_of_all_trades"))
        {
            var topBoss = await GetTopBossBySpecDiversityAsync(playerId, ct);
            var maxSpecsOnBoss = topBoss?.specCount ?? 0;
            var progressText = topBoss != null
                ? $"{Math.Min(maxSpecsOnBoss, 20)}/20 specs on {topBoss.Value.bossName}"
                : $"0/20 elite specs";
            result.Add(new AchievementProgressDto("jack_of_all_trades", "Jack of All Trades", "Complete a boss on 20 different elite specs",
                "SpecDiversity", Math.Min(maxSpecsOnBoss, 20), 20, progressText));
        }

        // Spec Diversity - Master of One (100 kills on same spec)
        if (!earnedCodes.Contains("master_of_one"))
        {
            var topSpec = await GetTopSpecByKillsAsync(playerId, ct);
            var maxSpecKills = topSpec?.kills ?? 0;
            var progressText = topSpec != null
                ? $"{Math.Min(maxSpecKills, 100)}/100 kills ({topSpec.Value.spec})"
                : $"0/100 kills";
            result.Add(new AchievementProgressDto("master_of_one", "Master of One", "Complete 100 kills on the same elite spec",
                "SpecDiversity", Math.Min(maxSpecKills, 100), 100, progressText));
        }

        // Spec Diversity - Class Completionist (all 4 elite specs for one profession on a single boss)
        if (!earnedCodes.Contains("class_completionist"))
        {
            var classProgress = await GetClassCompletionistProgressAsync(playerId, ct);
            if (classProgress != null)
            {
                var progressText = $"{classProgress.CompletedSpecs}/4 {classProgress.Profession} specs on {classProgress.TopBoss ?? "?"} - Missing: {string.Join(", ", classProgress.MissingSpecs)}";
                result.Add(new AchievementProgressDto("class_completionist", "Class Completionist",
                    "Complete a boss on every elite spec for a single profession",
                    "SpecDiversity", classProgress.CompletedSpecs, 4, progressText));
            }
        }

        // Support - Guardian Angel (5 times with most resurrects)
        if (!earnedCodes.Contains("guardian_angel"))
        {
            var count = await GetMostResurrectsCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("guardian_angel", "Guardian Angel", "Have the most resurrects in a successful kill (5+ times)",
                "Support", Math.Min(count, 5), 5, $"{Math.Min(count, 5)}/5 times"));
        }

        // Support - CC Champion (10 times with most CC)
        if (!earnedCodes.Contains("cc_champion"))
        {
            var count = await GetMostCCCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("cc_champion", "CC Champion", "Deal the most breakbar damage in a successful kill (10+ times)",
                "Support", Math.Min(count, 10), 10, $"{Math.Min(count, 10)}/10 times"));
        }

        // Support - The Enabler (25 times with highest boon DPS)
        if (!earnedCodes.Contains("the_enabler"))
        {
            var count = await GetHighestBoonDpsCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("the_enabler", "The Enabler", "Have the highest boon DPS in a successful kill (25+ times)",
                "Support", Math.Min(count, 25), 25, $"{Math.Min(count, 25)}/25 times"));
        }

        // Dedication - The Regular (25 sessions)
        if (!earnedCodes.Contains("the_regular"))
        {
            var sessionCount = await GetSessionCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("the_regular", "The Regular", "Participate in 25 raid sessions",
                "Dedication", Math.Min(sessionCount, 25), 25, $"{Math.Min(sessionCount, 25)}/25 sessions"));
        }

        // Dedication - Dedicated (50 sessions)
        if (!earnedCodes.Contains("dedicated"))
        {
            var sessionCount = await GetSessionCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("dedicated", "Dedicated", "Participate in 50 raid sessions",
                "Dedication", Math.Min(sessionCount, 50), 50, $"{Math.Min(sessionCount, 50)}/50 sessions"));
        }

        // Growth - Keeping Up (5 personal bests)
        if (!earnedCodes.Contains("keeping_up"))
        {
            var pbCount = await GetPersonalBestCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("keeping_up", "Keeping Up", "Beat your personal DPS best on a single boss 5+ times",
                "Growth", Math.Min(pbCount, 5), 5, $"{Math.Min(pbCount, 5)}/5 personal bests"));
        }

        // Social - Dynamic Duo (50 bosses with same party member)
        if (!earnedCodes.Contains("dynamic_duo"))
        {
            var maxPartnerKills = await GetMaxPartyPartnerKillsAsync(playerId, ct);
            result.Add(new AchievementProgressDto("dynamic_duo", "Dynamic Duo", "Complete 50 bosses with the same party member",
                "Social", Math.Min(maxPartnerKills, 50), 50, $"{Math.Min(maxPartnerKills, 50)}/50 kills"));
        }

        // Social - Trio (25 bosses with same two party members)
        if (!earnedCodes.Contains("trio"))
        {
            var maxTrioKills = await GetMaxPartyTrioKillsAsync(playerId, ct);
            result.Add(new AchievementProgressDto("trio", "Trio", "Complete 25 bosses with the same two party members",
                "Social", Math.Min(maxTrioKills, 25), 25, $"{Math.Min(maxTrioKills, 25)}/25 kills"));
        }

        // Performance - Immortal (10 consecutive kills without dying)
        if (!earnedCodes.Contains("immortal"))
        {
            var currentStreak = await GetCurrentDeathlessStreakAsync(playerId, ct);
            result.Add(new AchievementProgressDto("immortal", "Immortal", "Complete 10 consecutive kills without dying",
                "Performance", Math.Min(currentStreak, 10), 10, $"{Math.Min(currentStreak, 10)}/10 consecutive kills"));
        }

        // Completion achievements - use detailed progress data
        var completionProgress = await GetCompletionDetailedProgressAsync(playerId, ct);
        foreach (var cp in completionProgress)
        {
            result.Add(new AchievementProgressDto(
                cp.Code,
                cp.Name,
                cp.Description,
                "Completion",
                cp.Completed,
                cp.Total,
                $"{cp.Completed}/{cp.Total} bosses"
            ));
        }

        return result.OrderByDescending(p => (double)p.Current / p.Required).ToList();
    }

    /// <summary>
    /// Get all guild achievements
    /// </summary>
    public async Task<List<GuildAchievementDto>> GetGuildAchievementsAsync(CancellationToken ct = default)
    {
        var earned = await _db.GuildAchievements.ToListAsync(ct);
        var earnedDict = earned.ToDictionary(e => e.AchievementCode);

        var result = new List<GuildAchievementDto>();
        foreach (var def in AchievementDefinitions.Guild)
        {
            var isEarned = earnedDict.TryGetValue(def.Code, out var achievement);
            result.Add(new GuildAchievementDto(
                def.Code,
                def.Name,
                def.Description,
                def.Category.ToString(),
                isEarned ? achievement!.AchievedAt : null,
                isEarned,
                isEarned ? achievement!.Context : null,
                isEarned ? achievement!.CompletionCount : 0,
                isEarned ? achievement!.LastAchievedAt : null,
                isEarned ? achievement!.LastContext : null
            ));
        }

        return result;
    }

    /// <summary>
    /// Check flawless wing achievements for today's session
    /// Called when posting session summary
    /// </summary>
    public async Task<int> CheckFlawlessWingsForTodayAsync(bool notify = true, CancellationToken ct = default)
    {
        var today = DateTimeOffset.UtcNow.Date;
        var awarded = 0;

        _logger.LogInformation("Checking flawless wing achievements for {Date}", today);

        for (int wingNum = 1; wingNum <= 8; wingNum++)
        {
            var code = $"flawless_wing_{wingNum}";

            var wingBosses = AchievementDefinitions.WingMasterBosses.GetValueOrDefault(wingNum);
            if (wingBosses == null || wingBosses.Length == 0) continue;

            // Get all successful encounters for this wing today
            var encounters = await _db.Encounters
                .Where(e => e.Success && wingBosses.Contains(e.TriggerId))
                .Where(e => e.EncounterTime.Date == today)
                .OrderBy(e => e.EncounterTime)
                .ToListAsync(ct);

            if (encounters.Count == 0) continue;

            // Check if all bosses were cleared
            var bossesCleared = encounters.Select(e => e.TriggerId).Distinct().ToList();
            if (!wingBosses.All(b => bossesCleared.Contains(b))) continue;

            // Check if 0 deaths across all wing encounters today
            var totalDeaths = 0;
            foreach (var enc in encounters)
            {
                var deaths = await _db.PlayerEncounters
                    .Where(pe => pe.EncounterId == enc.Id)
                    .SumAsync(pe => pe.Deaths, ct);
                totalDeaths += deaths;
            }

            if (totalDeaths == 0)
            {
                var firstEncounter = encounters.First();
                // Build list of boss encounters for the context
                var bossEncounters = encounters
                    .GroupBy(e => AchievementDefinitions.NormalizeTriggerId(e.TriggerId))
                    .Select(g => g.First()) // Take first kill of each boss
                    .Select(e => new
                    {
                        encounter_id = e.Id,
                        boss_name = AchievementDefinitions.BossNames.GetValueOrDefault(e.TriggerId, e.BossName)
                    })
                    .ToList();

                await AwardGuildAchievementAsync(code, new
                {
                    encounter_id = firstEncounter.Id,
                    boss = $"Wing {wingNum} Flawless",
                    date = today.ToString("yyyy-MM-dd"),
                    bosses = bossEncounters
                }, notify, ct, firstEncounter.EncounterTime);

                awarded++;
                _logger.LogInformation("Awarded Flawless Wing {Wing} achievement", wingNum);
            }
        }

        return awarded;
    }

    #region Personal Achievement Checks

    private async Task CheckPersonalAchievementsAsync(
        Guid playerId,
        PlayerEncounterEntity pe,
        EncounterEntity encounter,
        bool notify,
        CancellationToken ct)
    {
        // Only check certain achievements for successful kills
        if (encounter.Success)
        {
            // Wing Master - check if role completed for this boss
            await CheckWingMasterProgressAsync(playerId, pe, encounter, notify, ct);

            // Performance - The Carry (25%+ of squad DPS)
            await CheckCarryAchievementAsync(playerId, pe, encounter, notify, ct);

            // Performance - Clutch Player (survive when 5+ died)
            await CheckClutchPlayerAsync(playerId, pe, encounter, notify, ct);

            // Performance - Speed Demon (guild record kill time)
            await CheckSpeedDemonAsync(playerId, encounter, notify, ct);

            // Performance - Immortal (10 consecutive kills without dying)
            await CheckImmortalAsync(playerId, notify, ct);

            // Records - Former Champion (held a DPS record)
            // Note: This is checked separately during backfill to find historical record holders

            // Support achievements
            await CheckSupportAchievementsAsync(playerId, pe, encounter, notify, ct);

            // Social - Guild Pride (all guild members, no pugs)
            await CheckGuildPrideAsync(playerId, encounter, notify, ct);

            // Social - Dynamic Duo and Trio (check after each encounter)
            await CheckSocialAchievementsAsync(playerId, notify, ct);

            // Completion achievements (wing clears, legendary raider, etc.)
            await CheckCompletionAchievementsAsync(playerId, notify, ct);

            // Dedication achievements (the_regular, dedicated - session counts)
            await CheckDedicationAchievementsAsync(playerId, notify, ct);

            // Growth - Keeping Up (personal bests)
            await CheckKeepingUpAsync(playerId, notify, ct);
        }

        // Spec Diversity achievements (for both kills and wipes)
        await CheckSpecDiversityAsync(playerId, pe, encounter, notify, ct);
    }

    private async Task CheckAggregateAchievementsAsync(Guid playerId, bool notify, CancellationToken ct)
    {
        // Completion achievements
        await CheckCompletionAchievementsAsync(playerId, notify, ct);

        // Performance - Immortal (10 consecutive kills without dying)
        await CheckImmortalAsync(playerId, notify, ct);

        // Dedication achievements
        await CheckDedicationAchievementsAsync(playerId, notify, ct);

        // Growth - Keeping Up
        await CheckKeepingUpAsync(playerId, notify, ct);

        // Social achievements
        await CheckSocialAchievementsAsync(playerId, notify, ct);
    }

    private async Task CheckWingMasterProgressAsync(
        Guid playerId,
        PlayerEncounterEntity pe,
        EncounterEntity encounter,
        bool notify,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pe.Role)) return;

        // Find which wing this boss belongs to (normalize trigger ID for Matthias)
        var normalizedTriggerId = AchievementDefinitions.NormalizeTriggerId(encounter.TriggerId);
        var wingNum = AchievementDefinitions.WingMasterBosses
            .FirstOrDefault(kvp => kvp.Value.Contains(normalizedTriggerId)).Key;

        if (wingNum == 0) return;

        var code = $"wing_{wingNum}_master";
        if (await HasAchievementAsync(playerId, code, ct)) return;

        // Check if all roles are complete for all bosses in this wing
        var bosses = AchievementDefinitions.WingMasterBosses[wingNum];
        var allComplete = true;

        foreach (var bossId in bosses)
        {
            // Get all trigger IDs that should count for this boss (handles Matthias having 2 IDs)
            var matchingTriggerIds = bossId == 16137
                ? new[] { 16137, 16115 } // Matthias has two trigger IDs
                : new[] { bossId };

            foreach (var role in AchievementDefinitions.RequiredRoles)
            {
                var hasRole = await _db.PlayerEncounters
                    .InnerJoin(_db.Encounters, (pe2, e) => pe2.EncounterId == e.Id, (pe2, e) => new { pe2, e })
                    .Where(x => x.pe2.PlayerId == playerId)
                    .Where(x => matchingTriggerIds.Contains(x.e.TriggerId) && x.e.Success)
                    .Where(x => x.pe2.Role == role)
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
            await AwardAchievementAsync(playerId, code, new { wing = wingNum }, notify, ct, encounter.EncounterTime);
        }
    }

    private async Task CheckCarryAchievementAsync(
        Guid playerId,
        PlayerEncounterEntity pe,
        EncounterEntity encounter,
        bool notify,
        CancellationToken ct)
    {
        if (await HasAchievementAsync(playerId, "the_carry", ct)) return;

        // Get all squad members' DPS for this encounter
        var squadDps = await _db.PlayerEncounters
            .Where(pe2 => pe2.EncounterId == encounter.Id)
            .Select(pe2 => pe2.Dps)
            .ToListAsync(ct);

        // Require at least 8 players for a valid raid squad (accounts for disconnects)
        if (squadDps.Count < 8) return;

        var totalSquadDps = squadDps.Sum();
        if (totalSquadDps == 0) return;

        var playerShare = (decimal)pe.Dps / totalSquadDps * 100;

        if (playerShare >= 25)
        {
            await AwardAchievementAsync(playerId, "the_carry", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName,
                dps = pe.Dps,
                total_squad_dps = totalSquadDps,
                squad_size = squadDps.Count,
                share = Math.Round(playerShare, 1)
            }, notify, ct, encounter.EncounterTime);
        }
    }

    private async Task CheckClutchPlayerAsync(
        Guid playerId,
        PlayerEncounterEntity pe,
        EncounterEntity encounter,
        bool notify,
        CancellationToken ct)
    {
        if (await HasAchievementAsync(playerId, "clutch_player", ct)) return;

        // Player must have survived (0 deaths)
        if (pe.Deaths > 0) return;

        // Count how many squadmates died
        var squadDeaths = await _db.PlayerEncounters
            .Where(pe2 => pe2.EncounterId == encounter.Id && pe2.Id != pe.Id)
            .SumAsync(pe2 => pe2.Deaths, ct);

        if (squadDeaths >= 5)
        {
            await AwardAchievementAsync(playerId, "clutch_player", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName,
                squad_deaths = squadDeaths
            }, notify, ct, encounter.EncounterTime);
        }
    }

    // Achievement baseline date - only count encounters from 2025 onwards for certain achievements
    private static readonly DateTimeOffset AchievementBaselineDate = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task CheckSpeedDemonAsync(
        Guid playerId,
        EncounterEntity encounter,
        bool notify,
        CancellationToken ct)
    {
        if (await HasAchievementAsync(playerId, "speed_demon", ct)) return;

        // Only apply to 2025+ encounters
        if (encounter.EncounterTime < AchievementBaselineDate) return;

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
            await AwardAchievementAsync(playerId, "speed_demon", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName,
                time_ms = encounter.DurationMs
            }, notify, ct, encounter.EncounterTime);
        }
    }

    private async Task CheckSupportAchievementsAsync(
        Guid playerId,
        PlayerEncounterEntity pe,
        EncounterEntity encounter,
        bool notify,
        CancellationToken ct)
    {
        // Guardian Angel - most resurrects
        if (!await HasAchievementAsync(playerId, "guardian_angel", ct) && pe.Resurrects > 0)
        {
            var maxResurrects = await _db.PlayerEncounters
                .Where(pe2 => pe2.EncounterId == encounter.Id)
                .MaxAsync(pe2 => pe2.Resurrects, ct);

            if (pe.Resurrects == maxResurrects && pe.Resurrects > 0)
            {
                var count = await GetMostResurrectsCountAsync(playerId, ct);
                if (count >= 5)
                {
                    await AwardAchievementAsync(playerId, "guardian_angel", new { times = count }, notify, ct, encounter.EncounterTime);
                }
            }
        }

        // CC Champion - most breakbar damage
        if (!await HasAchievementAsync(playerId, "cc_champion", ct) && pe.BreakbarDamage > 0)
        {
            var maxCC = await _db.PlayerEncounters
                .Where(pe2 => pe2.EncounterId == encounter.Id)
                .MaxAsync(pe2 => pe2.BreakbarDamage ?? 0, ct);

            if (pe.BreakbarDamage == maxCC)
            {
                var count = await GetMostCCCountAsync(playerId, ct);
                if (count >= 10)
                {
                    await AwardAchievementAsync(playerId, "cc_champion", new { times = count }, notify, ct, encounter.EncounterTime);
                }
            }
        }

        // The Enabler - highest boon DPS
        var isSupport = (pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                        (pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold;

        if (!await HasAchievementAsync(playerId, "the_enabler", ct) && isSupport)
        {
            var boonPlayers = await _db.PlayerEncounters
                .Where(pe2 => pe2.EncounterId == encounter.Id)
                .Where(pe2 => (pe2.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                              (pe2.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
                .ToListAsync(ct);

            var maxBoonDps = boonPlayers.Max(p => p.Dps);
            if (pe.Dps == maxBoonDps)
            {
                var count = await GetHighestBoonDpsCountAsync(playerId, ct);
                if (count >= 25)
                {
                    await AwardAchievementAsync(playerId, "the_enabler", new { times = count }, notify, ct, encounter.EncounterTime);
                }
            }
        }
    }

    private async Task CheckGuildPrideAsync(
        Guid playerId,
        EncounterEntity encounter,
        bool notify,
        CancellationToken ct)
    {
        if (await HasAchievementAsync(playerId, "guild_pride", ct)) return;

        // Get all players in THIS encounter
        var playerAccounts = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => x.pe.EncounterId == encounter.Id)
            .Select(x => x.p.AccountName)
            .Distinct()
            .ToListAsync(ct);

        // Check if all are included (guild members - includes both manually and auto-included)
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var allGuildMembers = playerAccounts.All(a => includedAccounts.Contains(a));

        if (allGuildMembers)
        {
            await AwardAchievementAsync(playerId, "guild_pride", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName
            }, notify, ct, encounter.EncounterTime);
        }
    }

    private async Task CheckSpecDiversityAsync(
        Guid playerId,
        PlayerEncounterEntity pe,
        EncounterEntity encounter,
        bool notify,
        CancellationToken ct)
    {
        // Only count successful kills for spec diversity
        if (!encounter.Success) return;

        // Versatile & Jack of All Trades - based on max specs on any single boss
        var topBoss = await GetTopBossBySpecDiversityAsync(playerId, ct);
        var maxSpecsOnBoss = topBoss?.specCount ?? 0;

        // Versatile - 10 different elite specs on one boss
        if (!await HasAchievementAsync(playerId, "versatile", ct) && maxSpecsOnBoss >= 10)
        {
            await AwardAchievementAsync(playerId, "versatile", new { boss_name = topBoss!.Value.bossName, spec_count = maxSpecsOnBoss }, notify, ct, encounter.EncounterTime);
        }

        // Jack of All Trades - 20 different elite specs on one boss
        if (!await HasAchievementAsync(playerId, "jack_of_all_trades", ct) && maxSpecsOnBoss >= 20)
        {
            await AwardAchievementAsync(playerId, "jack_of_all_trades", new { boss_name = topBoss!.Value.bossName, spec_count = maxSpecsOnBoss }, notify, ct, encounter.EncounterTime);
        }

        // Class Completionist - all 4 elite specs for one profession on a SINGLE boss
        if (!await HasAchievementAsync(playerId, "class_completionist", ct))
        {
            // Get all spec kills grouped by boss
            var specKills = await _db.PlayerEncounters
                .InnerJoin(_db.Encounters, (pe2, e) => pe2.EncounterId == e.Id, (pe2, e) => new { pe2, e })
                .Where(x => x.pe2.PlayerId == playerId && x.e.Success)
                .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe2.Profession))
                .Select(x => new { x.pe2.Profession, x.e.BossName, x.e.Id, x.e.EncounterTime })
                .ToListAsync(ct);

            // Group by boss, then check if any boss has all 4 specs for a profession
            var killsByBoss = specKills.GroupBy(x => x.BossName).ToList();

            foreach (var (profession, eliteSpecs) in AchievementDefinitions.EliteSpecsByProfession)
            {
                foreach (var bossGroup in killsByBoss)
                {
                    var specsOnThisBoss = bossGroup.Select(x => x.Profession).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);

                    // Check if this boss has all 4 elite specs for this profession
                    if (eliteSpecs.All(spec => specsOnThisBoss.Contains(spec)))
                    {
                        // Found a complete profession on a single boss - get the details
                        var specDetails = eliteSpecs.Select(spec =>
                        {
                            var kill = bossGroup.First(x => x.Profession.Equals(spec, StringComparison.OrdinalIgnoreCase));
                            return new { spec, boss_name = kill.BossName, encounter_id = kill.Id };
                        }).ToList();

                        await AwardAchievementAsync(playerId, "class_completionist", new
                        {
                            profession,
                            boss = bossGroup.Key,
                            specs = specDetails
                        }, notify, ct, encounter.EncounterTime);
                        break;
                    }
                }

                // If we awarded, stop checking other professions
                if (await HasAchievementAsync(playerId, "class_completionist", ct))
                    break;
            }
        }

        // Master of One - 100 kills on same elite spec
        if (!await HasAchievementAsync(playerId, "master_of_one", ct))
        {
            var maxKills = await GetMaxKillsOnSingleSpecAsync(playerId, ct);
            if (maxKills >= 100)
            {
                // Find which spec and when the 100th kill happened
                var specKills = await _db.PlayerEncounters
                    .InnerJoin(_db.Encounters, (pe2, e) => pe2.EncounterId == e.Id, (pe2, e) => new { pe2, e })
                    .Where(x => x.pe2.PlayerId == playerId && x.e.Success)
                    .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe2.Profession))
                    .Select(x => new { x.pe2.Profession, x.e.EncounterTime })
                    .ToListAsync(ct);

                // Group by spec, order by time, find the 100th kill date
                var topSpecData = specKills
                    .GroupBy(x => x.Profession)
                    .Where(g => g.Count() >= 100)
                    .Select(g => new
                    {
                        Spec = g.Key,
                        Count = g.Count(),
                        // The date of the 100th kill (0-indexed, so index 99)
                        AchievedAt = g.OrderBy(x => x.EncounterTime).Skip(99).First().EncounterTime
                    })
                    .OrderByDescending(x => x.Count)
                    .FirstOrDefault();

                if (topSpecData != null)
                {
                    await AwardAchievementAsync(playerId, "master_of_one", new
                    {
                        spec = topSpecData.Spec,
                        kills = topSpecData.Count
                    }, notify, ct, topSpecData.AchievedAt);
                }
            }
        }
    }

    private async Task CheckCompletionAchievementsAsync(Guid playerId, bool notify, CancellationToken ct)
    {
        // Completion - kill every boss in Wings 1-7
        if (!await HasAchievementAsync(playerId, "completion", ct))
        {
            // Normalize boss IDs to avoid duplicate Matthias entries
            var w1to7Bosses = AchievementDefinitions.WingMasterBosses
                .Where(kvp => kvp.Key <= 7)
                .SelectMany(kvp => kvp.Value)
                .Select(AchievementDefinitions.NormalizeTriggerId)
                .Distinct()
                .ToHashSet();

            // Get first kill of each boss with dates (CM kills count - if you beat CM, you beat the boss)
            // Materialize first, then group in memory (NormalizeTriggerId can't be translated to SQL)
            var rawKills = await _db.PlayerEncounters
                .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
                .Where(x => x.pe.PlayerId == playerId && x.e.Success)
                .Where(x => w1to7Bosses.Contains(x.e.TriggerId) || x.e.TriggerId == 16115) // Include alternate Matthias
                .Select(x => new { x.e.TriggerId, x.e.EncounterTime })
                .ToListAsync(ct);

            var firstKillsByBoss = rawKills
                .GroupBy(x => AchievementDefinitions.NormalizeTriggerId(x.TriggerId))
                .Select(g => new { BossId = g.Key, FirstKill = g.Min(x => x.EncounterTime) })
                .ToList();

            if (w1to7Bosses.All(b => firstKillsByBoss.Any(fk => fk.BossId == b)))
            {
                // Achievement date is when the last required boss was first killed
                var achievedAt = firstKillsByBoss.Max(fk => fk.FirstKill);
                await AwardAchievementAsync(playerId, "completion", null, notify, ct, achievedAt);
            }
        }

        // Legendary Raider - kill every CM boss in Wings 3-7 (Wings 1-2 have no CMs, Xera has no CM)
        if (!await HasAchievementAsync(playerId, "legendary_raider", ct))
        {
            // Use the defined set of bosses that have CMs
            var w1to7CmBosses = AchievementDefinitions.Wings1To7CMBosses;

            // Get first CM kill of each boss with dates
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
                await AwardAchievementAsync(playerId, "legendary_raider", null, notify, ct, achievedAt);
            }
        }

        // Wing 8 Clear
        if (!await HasAchievementAsync(playerId, "wing_8_clear", ct))
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
                await AwardAchievementAsync(playerId, "wing_8_clear", null, notify, ct, achievedAt);
            }
        }

        // Wing 8 CM Clear - Greer/Ura use same trigger ID as NM (check IsCM), Decima has separate CM ID (26867)
        if (!await HasAchievementAsync(playerId, "wing_8_cm_clear", ct))
        {
            // Check for CM kills of each W8 boss
            // Greer CM: trigger 26725 with IsCM=true
            // Decima CM: trigger 26867 (unique CM trigger)
            // Ura CM: trigger 26712 with IsCM=true
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
                await AwardAchievementAsync(playerId, "wing_8_cm_clear", null, notify, ct, achievedAt);
            }
        }
    }

    private async Task CheckImmortalAsync(Guid playerId, bool notify, CancellationToken ct)
    {
        if (await HasAchievementAsync(playerId, "immortal", ct)) return;

        // Get all successful kills ordered by time, tracking deaths
        var kills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.pe.Deaths, x.e.EncounterTime })
            .ToListAsync(ct);

        // Find the first time they hit a 10-kill deathless streak
        int streak = 0;
        DateTimeOffset? achievedAt = null;
        foreach (var kill in kills)
        {
            if (kill.Deaths == 0)
            {
                streak++;
                if (streak >= 10 && achievedAt == null)
                {
                    achievedAt = kill.EncounterTime;
                    break;
                }
            }
            else
            {
                streak = 0;
            }
        }

        if (achievedAt != null)
        {
            await AwardAchievementAsync(playerId, "immortal", new { streak = 10 }, notify, ct, achievedAt);
        }
    }

    private async Task CheckDedicationAchievementsAsync(Guid playerId, bool notify, CancellationToken ct)
    {
        // Get all unique session dates ordered chronologically
        var sessionDates = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => x.e.EncounterTime.Date)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);

        var sessionCount = sessionDates.Count;

        // The Regular - 25 sessions
        if (!await HasAchievementAsync(playerId, "the_regular", ct) && sessionCount >= 25)
        {
            var achievedAt = sessionDates[24]; // 25th session (0-indexed)
            await AwardAchievementAsync(playerId, "the_regular", new { sessions = 25 }, notify, ct, new DateTimeOffset(achievedAt, TimeSpan.Zero));
        }

        // Dedicated - 50 sessions
        if (!await HasAchievementAsync(playerId, "dedicated", ct) && sessionCount >= 50)
        {
            var achievedAt = sessionDates[49]; // 50th session (0-indexed)
            await AwardAchievementAsync(playerId, "dedicated", new { sessions = 50 }, notify, ct, new DateTimeOffset(achievedAt, TimeSpan.Zero));
        }
    }

    private async Task CheckKeepingUpAsync(Guid playerId, bool notify, CancellationToken ct)
    {
        if (await HasAchievementAsync(playerId, "keeping_up", ct)) return;

        // Find when the 5th personal best was achieved (only counting 2025+ encounters)
        var encounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => x.e.EncounterTime >= AchievementBaselineDate)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.e.TriggerId, x.e.IsCM, x.pe.Dps, x.e.EncounterTime })
            .ToListAsync(ct);

        var personalBests = new Dictionary<(int, bool), int>();
        var pbCount = 0;
        DateTimeOffset? achievedAt = null;

        foreach (var enc in encounters)
        {
            var key = (enc.TriggerId, enc.IsCM);
            if (personalBests.TryGetValue(key, out var currentBest))
            {
                if (enc.Dps > currentBest)
                {
                    personalBests[key] = enc.Dps;
                    pbCount++;
                    if (pbCount == 5)
                    {
                        achievedAt = enc.EncounterTime;
                        break;
                    }
                }
            }
            else
            {
                personalBests[key] = enc.Dps;
            }
        }

        if (achievedAt != null)
        {
            await AwardAchievementAsync(playerId, "keeping_up", new { personal_bests = 5 }, notify, ct, achievedAt);
        }
    }

    private async Task CheckSocialAchievementsAsync(Guid playerId, bool notify, CancellationToken ct)
    {
        // Dynamic Duo - 50 bosses with same party member
        if (!await HasAchievementAsync(playerId, "dynamic_duo", ct))
        {
            var duoResult = await GetPartyPartnerKillsWithDateAsync(playerId, 50, ct);
            if (duoResult.HasValue)
            {
                await AwardAchievementAsync(playerId, "dynamic_duo", new { kills = 50 }, notify, ct, duoResult.Value.achievedAt);
            }
        }

        // Trio - 25 bosses with same two party members
        if (!await HasAchievementAsync(playerId, "trio", ct))
        {
            var trioResult = await GetPartyTrioKillsWithDateAsync(playerId, 25, ct);
            if (trioResult.HasValue)
            {
                await AwardAchievementAsync(playerId, "trio", new { kills = 25 }, notify, ct, trioResult.Value.achievedAt);
            }
        }
    }

    #endregion

    #region Guild Achievement Checks

    private async Task CheckGuildAchievementsAsync(
        EncounterEntity encounter,
        List<(PlayerEncounterEntity pe, PlayerEntity p)> playerEncounters,
        HashSet<string> includedAccounts,
        bool notify,
        CancellationToken ct)
    {
        // Convert to proper tuple list
        var players = playerEncounters.Select(x => (x.pe, x.p)).ToList();

        // Composition achievements
        await CheckCompositionAchievementsAsync(encounter, players, includedAccounts, notify, ct);

        // Performance achievements
        await CheckGuildPerformanceAchievementsAsync(encounter, players, notify, ct);
    }

    private async Task CheckCompositionAchievementsAsync(
        EncounterEntity encounter,
        List<(PlayerEncounterEntity pe, PlayerEntity p)> players,
        HashSet<string> includedAccounts,
        bool notify,
        CancellationToken ct)
    {
        var professions = players.Select(x => x.pe.Profession).ToList();
        var baseProfessions = professions.Select(AchievementDefinitions.GetBaseProfession).ToList();

        // One Trick Guild - all 10 on same profession
        if (baseProfessions.Distinct().Count() == 1 && players.Count >= 10)
        {
            await AwardGuildAchievementAsync("one_trick_guild", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName,
                profession = baseProfessions.First()
            }, notify, ct, encounter.EncounterTime);

            // Also award the profession-specific achievement
            var profession = baseProfessions.First();
            var professionAchievementCode = profession?.ToLowerInvariant() switch
            {
                "elementalist" => "all_elementalist",
                "necromancer" => "all_necromancer",
                "mesmer" => "all_mesmer",
                "guardian" => "all_guardian",
                "warrior" => "all_warrior",
                "revenant" => "all_revenant",
                "engineer" => "all_engineer",
                "ranger" => "all_ranger",
                "thief" => "all_thief",
                _ => null
            };

            if (professionAchievementCode != null)
            {
                await AwardGuildAchievementAsync(professionAchievementCode, new
                {
                    encounter_id = encounter.Id,
                    boss = encounter.BossName,
                    profession = profession
                }, notify, ct, encounter.EncounterTime);
            }
        }

        // Heavy Metal - only heavy armor
        var heavySpecs = AchievementDefinitions.ArmorClasses["Heavy"];
        if (professions.All(p => heavySpecs.Contains(p)))
        {
            await AwardGuildAchievementAsync("heavy_metal", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName
            }, notify, ct, encounter.EncounterTime);
        }

        // Cloth Squad - only light armor
        var lightSpecs = AchievementDefinitions.ArmorClasses["Light"];
        if (professions.All(p => lightSpecs.Contains(p)))
        {
            await AwardGuildAchievementAsync("cloth_squad", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName
            }, notify, ct, encounter.EncounterTime);
        }

        // Leather Lovers - only medium armor
        var mediumSpecs = AchievementDefinitions.ArmorClasses["Medium"];
        if (professions.All(p => mediumSpecs.Contains(p)))
        {
            await AwardGuildAchievementAsync("leather_lovers", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName
            }, notify, ct, encounter.EncounterTime);
        }

        // No Duplicates - 10 different elite specs in one encounter
        var uniqueSpecs = professions.Where(p => AchievementDefinitions.AllEliteSpecs.Contains(p)).Distinct().Count();
        if (uniqueSpecs >= 10)
        {
            await AwardGuildAchievementAsync("no_duplicates", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName,
                specs = uniqueSpecs
            }, notify, ct, encounter.EncounterTime);
        }

        // Rainbow Squad - at least one of each profession (9 classes) in one encounter
        // Must have all 9 base professions represented
        var allBaseProfessions = AchievementDefinitions.EliteSpecsByProfession.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var presentBaseProfessions = baseProfessions
            .Where(p => allBaseProfessions.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (presentBaseProfessions.Count >= 9)
        {
            await AwardGuildAchievementAsync("rainbow_squad", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName
            }, notify, ct, encounter.EncounterTime);
        }

        // Bench Warmers - kill with 7 or fewer players
        if (players.Count <= 7)
        {
            await AwardGuildAchievementAsync("bench_warmers", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName,
                player_count = players.Count
            }, notify, ct, encounter.EncounterTime);
        }

        // Core Memory - everyone on core classes (no elite specs)
        if (professions.All(p => AchievementDefinitions.IsCoreProfession(p)))
        {
            await AwardGuildAchievementAsync("core_memory", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName
            }, notify, ct, encounter.EncounterTime);
        }

        // Chaos Strat - everyone in the same subgroup (raids only, 7+ players)
        var subgroups = players.Select(x => x.pe.SquadGroup).Distinct().ToList();
        if (encounter.Wing >= 1 && encounter.Wing <= 8 &&
            players.Count >= 7 &&
            subgroups.Count == 1 && subgroups[0] != null)
        {
            await AwardGuildAchievementAsync("chaos_strat", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName,
                subgroup = subgroups[0]
            }, notify, ct, encounter.EncounterTime);
        }
    }

    private async Task CheckGuildPerformanceAchievementsAsync(
        EncounterEntity encounter,
        List<(PlayerEncounterEntity pe, PlayerEntity p)> players,
        bool notify,
        CancellationToken ct)
    {
        // Untouchable - 0 downs across entire squad
        var totalDowns = players.Sum(x => x.pe.Downs);
        if (totalDowns == 0)
        {
            await AwardGuildAchievementAsync("untouchable", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName
            }, notify, ct, encounter.EncounterTime);
        }

        // Photo Finish - kill in final 10 seconds before enrage
        // Note: We'd need enrage timer data to implement this properly
        // Skipping for now as we don't have enrage timer data

        // Record Breakers - break DPS and boon DPS records in the same encounter
        await CheckRecordBreakersAsync(encounter, players, notify, ct);

        // The Comeback - kill a boss after wiping 5+ times on it in the same session
        await CheckTheComebackAsync(encounter, notify, ct);
    }

    /// <summary>
    /// Check if both a DPS record and a boon DPS record were broken in the same encounter
    /// </summary>
    private async Task CheckRecordBreakersAsync(
        EncounterEntity encounter,
        List<(PlayerEncounterEntity pe, PlayerEntity p)> players,
        bool notify,
        CancellationToken ct)
    {
        var includedAccounts = (await _includedPlayerService.GetIncludedAccountNamesAsync(ct)).ToHashSet();

        // Check if DPS record was broken
        var topDps = players
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .OrderByDescending(x => x.pe.Dps)
            .FirstOrDefault();

        if (topDps.pe == null) return;

        var previousDpsRecord = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == encounter.TriggerId && x.e.IsCM == encounter.IsCM && x.e.Success)
            .Where(x => x.e.EncounterTime < encounter.EncounterTime)
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .MaxAsync(x => (int?)x.pe.Dps, ct) ?? 0;

        var dpsRecordBroken = topDps.pe.Dps > previousDpsRecord;

        // Check if boon DPS record was broken (support players with quickness/alac >= 10%)
        var boonPlayers = players
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                       (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .OrderByDescending(x => x.pe.Dps)
            .ToList();

        if (boonPlayers.Count == 0) return;

        var topBoonDps = boonPlayers.First();

        var previousBoonDpsRecord = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == encounter.TriggerId && x.e.IsCM == encounter.IsCM && x.e.Success)
            .Where(x => x.e.EncounterTime < encounter.EncounterTime)
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                       (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .MaxAsync(x => (int?)x.pe.Dps, ct) ?? 0;

        var boonDpsRecordBroken = topBoonDps.pe.Dps > previousBoonDpsRecord;

        // Award if both records broken
        if (dpsRecordBroken && boonDpsRecordBroken)
        {
            await AwardGuildAchievementAsync("record_breakers", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName,
                dps_player = topDps.p.AccountName,
                dps = topDps.pe.Dps,
                boon_dps_player = topBoonDps.p.AccountName,
                boon_dps = topBoonDps.pe.Dps
            }, notify, ct, encounter.EncounterTime);
        }
    }

    /// <summary>
    /// Check "The Comeback" achievement - kill a boss after wiping 5+ times on it in the same session
    /// </summary>
    private async Task CheckTheComebackAsync(
        EncounterEntity encounter,
        bool notify,
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
            await AwardGuildAchievementAsync("the_comeback", new
            {
                encounter_id = encounter.Id,
                boss = encounter.BossName,
                wipes = wipesBeforeKill
            }, notify, ct, encounter.EncounterTime);
        }
    }

    #endregion

    #region Helper Methods

    private async Task<bool> HasAchievementAsync(Guid playerId, string code, CancellationToken ct)
    {
        return await _db.PlayerAchievements
            .AnyAsync(pa => pa.PlayerId == playerId && pa.AchievementCode == code, ct);
    }

    private async Task<bool> HasGuildAchievementAsync(string code, CancellationToken ct)
    {
        return await _db.GuildAchievements
            .AnyAsync(ga => ga.AchievementCode == code, ct);
    }

    private async Task AwardAchievementAsync(Guid playerId, string code, object? context, bool notify, CancellationToken ct, DateTimeOffset? achievedAt = null)
    {
        // Double-check we don't already have it
        if (await HasAchievementAsync(playerId, code, ct)) return;

        var achievement = new PlayerAchievementEntity
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            AchievementCode = code,
            AchievedAt = achievedAt ?? DateTimeOffset.UtcNow,
            Context = context != null ? JsonSerializer.Serialize(context) : null,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(achievement, token: ct);

        _logger.LogInformation("Awarded achievement {Code} to player {PlayerId}", code, playerId);

        if (notify)
        {
            await QueueAchievementNotificationAsync(playerId, code, false, ct);
        }
    }

    private async Task AwardGuildAchievementAsync(string code, object? context, bool notify, CancellationToken ct, DateTimeOffset? achievedAt = null)
    {
        var effectiveAchievedAt = achievedAt ?? DateTimeOffset.UtcNow;
        var contextJson = context != null ? JsonSerializer.Serialize(context) : null;

        // Check if achievement already exists
        var existing = await _db.GuildAchievements
            .FirstOrDefaultAsync(ga => ga.AchievementCode == code, ct);

        if (existing != null)
        {
            // Increment count and update last achieved
            await _db.GuildAchievements
                .Where(ga => ga.Id == existing.Id)
                .Set(ga => ga.CompletionCount, existing.CompletionCount + 1)
                .Set(ga => ga.LastAchievedAt, effectiveAchievedAt)
                .Set(ga => ga.LastContext, contextJson)
                .UpdateAsync(ct);

            _logger.LogInformation("Guild achievement {Code} completed again (count: {Count})", code, existing.CompletionCount + 1);
        }
        else
        {
            // First time earning this achievement
            var achievement = new GuildAchievementEntity
            {
                Id = Guid.NewGuid(),
                AchievementCode = code,
                AchievedAt = effectiveAchievedAt,
                Context = contextJson,
                CreatedAt = DateTimeOffset.UtcNow,
                CompletionCount = 1,
                LastAchievedAt = effectiveAchievedAt,
                LastContext = contextJson
            };

            await _db.InsertAsync(achievement, token: ct);

            _logger.LogInformation("Awarded guild achievement {Code}", code);

            if (notify)
            {
                await QueueAchievementNotificationAsync(Guid.Empty, code, true, ct);
            }
        }
    }

    private async Task QueueAchievementNotificationAsync(Guid playerId, string code, bool isGuild, CancellationToken ct)
    {
        string? playerName = null;
        if (!isGuild)
        {
            playerName = await _db.Players
                .Where(p => p.Id == playerId)
                .Select(p => p.AccountName)
                .FirstOrDefaultAsync(ct);
        }

        var definition = isGuild
            ? (object?)AchievementDefinitions.Guild.FirstOrDefault(a => a.Code == code)
            : AchievementDefinitions.Personal.FirstOrDefault(a => a.Code == code);

        if (definition == null) return;

        var (name, description) = definition switch
        {
            AchievementDefinition a => (a.Name, a.Description),
            GuildAchievementDefinition g => (g.Name, g.Description),
            _ => ("Unknown", "Unknown achievement")
        };

        var payload = new AchievementPayload(
            playerName,
            code,
            name,
            description,
            isGuild
        );

        var notification = new NotificationQueueEntity
        {
            Id = Guid.NewGuid(),
            NotificationType = "achievement_unlocked",
            Payload = JsonSerializer.Serialize(payload),
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(notification, token: ct);
    }

    private static int? ExtractWingNumber(string code)
    {
        // Extract wing number from code like "wing_1_master"
        var parts = code.Split('_');
        if (parts.Length >= 2 && int.TryParse(parts[1], out var num))
            return num;
        return null;
    }

    private async Task<(int Current, int Required, string ProgressText)> GetWingMasterProgressAsync(
        Guid playerId, int wingNum, CancellationToken ct)
    {
        var bosses = AchievementDefinitions.WingMasterBosses[wingNum];
        var totalRequired = bosses.Length * AchievementDefinitions.RequiredRoles.Length;
        var completed = 0;

        foreach (var bossId in bosses)
        {
            // Get all trigger IDs that should count for this boss (handles Matthias having 2 IDs)
            var matchingTriggerIds = bossId == 16137
                ? new[] { 16137, 16115 } // Matthias has two trigger IDs
                : new[] { bossId };

            foreach (var role in AchievementDefinitions.RequiredRoles)
            {
                var hasRole = await _db.PlayerEncounters
                    .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
                    .Where(x => x.pe.PlayerId == playerId)
                    .Where(x => matchingTriggerIds.Contains(x.e.TriggerId) && x.e.Success)
                    .Where(x => x.pe.Role == role)
                    .AnyAsync(ct);

                if (hasRole) completed++;
            }
        }

        return (completed, totalRequired, $"{completed}/{totalRequired} boss/role combos");
    }

    /// <summary>
    /// Get detailed Wing Master progress showing which boss/role combos are missing
    /// </summary>
    public async Task<List<WingMasterDetailedProgressDto>> GetWingMasterDetailedProgressAsync(
        Guid playerId, CancellationToken ct)
    {
        var result = new List<WingMasterDetailedProgressDto>();

        // Get already earned achievements
        var earnedCodes = (await _db.PlayerAchievements
            .Where(pa => pa.PlayerId == playerId)
            .Select(pa => pa.AchievementCode)
            .ToListAsync(ct))
            .ToHashSet();

        foreach (var wingDef in AchievementDefinitions.Personal.Where(a => a.Category == AchievementCategory.WingMaster))
        {
            var wingNum = ExtractWingNumber(wingDef.Code);
            if (wingNum == null) continue;

            // Skip earned achievements
            if (earnedCodes.Contains(wingDef.Code)) continue;

            var bosses = AchievementDefinitions.WingMasterBosses[wingNum.Value];
            var bossProgressList = new List<WingMasterBossProgressDto>();
            var totalCompleted = 0;
            var totalRequired = bosses.Length * AchievementDefinitions.RequiredRoles.Length;

            foreach (var bossId in bosses)
            {
                var bossName = AchievementDefinitions.BossNames.GetValueOrDefault(bossId, $"Boss {bossId}");
                var roleProgressList = new List<WingMasterRoleProgressDto>();

                // Get all trigger IDs that should count for this boss (handles Matthias having 2 IDs)
                var matchingTriggerIds = bossId == 16137
                    ? new[] { 16137, 16115 } // Matthias has two trigger IDs
                    : new[] { bossId };

                foreach (var role in AchievementDefinitions.RequiredRoles)
                {
                    var hasRole = await _db.PlayerEncounters
                        .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
                        .Where(x => x.pe.PlayerId == playerId)
                        .Where(x => matchingTriggerIds.Contains(x.e.TriggerId) && x.e.Success)
                        .Where(x => x.pe.Role == role)
                        .AnyAsync(ct);

                    var roleDisplayName = AchievementDefinitions.RoleDisplayNames.GetValueOrDefault(role, role);
                    roleProgressList.Add(new WingMasterRoleProgressDto(role, roleDisplayName, hasRole));

                    if (hasRole) totalCompleted++;
                }

                bossProgressList.Add(new WingMasterBossProgressDto(bossId, bossName, roleProgressList));
            }

            result.Add(new WingMasterDetailedProgressDto(
                wingDef.Code,
                wingDef.Name,
                wingDef.Description,
                wingNum.Value,
                totalCompleted,
                totalRequired,
                bossProgressList
            ));
        }

        return result;
    }

    /// <summary>
    /// Get detailed completion progress showing which bosses are missing
    /// </summary>
    public async Task<List<CompletionDetailedProgressDto>> GetCompletionDetailedProgressAsync(
        Guid playerId, CancellationToken ct)
    {
        var result = new List<CompletionDetailedProgressDto>();

        // Get already earned achievements
        var earnedCodes = (await _db.PlayerAchievements
            .Where(pa => pa.PlayerId == playerId)
            .Select(pa => pa.AchievementCode)
            .ToListAsync(ct))
            .ToHashSet();

        // Get all successful kills for this player
        var killedTriggerIds = (await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => x.e.TriggerId)
            .ToListAsync(ct))
            .ToHashSet();

        // Get all successful CM kills for this player
        // Include IsCM=true OR Decima CM trigger (26867) which has unique ID
        var killedCMTriggerIds = (await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success && (x.e.IsCM || x.e.TriggerId == 26867))
            .Select(x => x.e.TriggerId)
            .ToListAsync(ct))
            .ToHashSet();

        // Completion - all bosses in Wings 1-7
        if (!earnedCodes.Contains("completion"))
        {
            var bossList = new List<CompletionBossProgressDto>();
            var completed = 0;
            var total = 0;

            for (int wing = 1; wing <= 7; wing++)
            {
                var wingBosses = AchievementDefinitions.WingMasterBosses.GetValueOrDefault(wing);
                if (wingBosses == null) continue;

                foreach (var bossId in wingBosses.Distinct())
                {
                    // Normalize Matthias IDs
                    var normalizedId = AchievementDefinitions.NormalizeTriggerId(bossId);
                    if (bossList.Any(b => AchievementDefinitions.NormalizeTriggerId(b.TriggerId) == normalizedId))
                        continue; // Skip duplicate Matthias

                    var bossName = AchievementDefinitions.BossNames.GetValueOrDefault(bossId, $"Boss {bossId}");
                    var isKilled = killedTriggerIds.Contains(bossId) ||
                                   (bossId == 16137 && killedTriggerIds.Contains(16115)) ||
                                   (bossId == 16115 && killedTriggerIds.Contains(16137));

                    bossList.Add(new CompletionBossProgressDto(bossId, bossName, wing, isKilled));
                    total++;
                    if (isKilled) completed++;
                }
            }

            result.Add(new CompletionDetailedProgressDto(
                "completion", "Completion", "Kill every boss in Wings 1-7",
                completed, total, bossList));
        }

        // Legendary Raider - all CM bosses in Wings 1-7 (only bosses that have CMs)
        if (!earnedCodes.Contains("legendary_raider"))
        {
            var bossList = new List<CompletionBossProgressDto>();
            var completed = 0;
            var total = 0;

            for (int wing = 3; wing <= 7; wing++) // Wings 1-2 have no CMs
            {
                var wingBosses = AchievementDefinitions.WingMasterBosses.GetValueOrDefault(wing);
                if (wingBosses == null) continue;

                foreach (var bossId in wingBosses.Distinct())
                {
                    // Skip bosses that don't have CM (like Xera)
                    if (!AchievementDefinitions.Wings1To7CMBosses.Contains(bossId))
                        continue;

                    var normalizedId = AchievementDefinitions.NormalizeTriggerId(bossId);
                    if (bossList.Any(b => AchievementDefinitions.NormalizeTriggerId(b.TriggerId) == normalizedId))
                        continue;

                    var bossName = AchievementDefinitions.BossNames.GetValueOrDefault(bossId, $"Boss {bossId}") + " CM";
                    var isKilled = killedCMTriggerIds.Contains(bossId);

                    bossList.Add(new CompletionBossProgressDto(bossId, bossName, wing, isKilled));
                    total++;
                    if (isKilled) completed++;
                }
            }

            result.Add(new CompletionDetailedProgressDto(
                "legendary_raider", "Legendary Raider", "Kill every CM boss in Wings 3-7",
                completed, total, bossList));
        }

        // Wing 8 Clear
        if (!earnedCodes.Contains("wing_8_clear"))
        {
            var bossList = new List<CompletionBossProgressDto>();
            var completed = 0;
            var wingBosses = AchievementDefinitions.WingMasterBosses.GetValueOrDefault(8);

            if (wingBosses != null)
            {
                foreach (var bossId in wingBosses)
                {
                    var bossName = AchievementDefinitions.BossNames.GetValueOrDefault(bossId, $"Boss {bossId}");
                    var isKilled = killedTriggerIds.Contains(bossId);

                    bossList.Add(new CompletionBossProgressDto(bossId, bossName, 8, isKilled));
                    if (isKilled) completed++;
                }
            }

            result.Add(new CompletionDetailedProgressDto(
                "wing_8_clear", "Wing 8 Clear", "Complete all Wing 8 bosses",
                completed, wingBosses?.Length ?? 0, bossList));
        }

        // Wing 8 CM Clear
        if (!earnedCodes.Contains("wing_8_cm_clear"))
        {
            var bossList = new List<CompletionBossProgressDto>();
            var completed = 0;
            var cmBosses = AchievementDefinitions.Wing8CMBosses;

            foreach (var bossId in cmBosses)
            {
                var bossName = AchievementDefinitions.BossNames.GetValueOrDefault(bossId, $"Boss {bossId}");
                var isKilled = killedCMTriggerIds.Contains(bossId);

                bossList.Add(new CompletionBossProgressDto(bossId, bossName, 8, isKilled));
                if (isKilled) completed++;
            }

            result.Add(new CompletionDetailedProgressDto(
                "wing_8_cm_clear", "Wing 8 CM Clear", "Complete all Wing 8 CMs",
                completed, cmBosses.Length, bossList));
        }

        return result;
    }

    private async Task<int> GetUniqueEliteSpecCountAsync(Guid playerId, CancellationToken ct)
    {
        return await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .Select(x => x.pe.Profession)
            .Distinct()
            .CountAsync(ct);
    }

    private async Task<(string spec, int kills)?> GetTopSpecByKillsAsync(Guid playerId, CancellationToken ct)
    {
        var topSpec = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .GroupBy(x => x.pe.Profession)
            .Select(g => new { Spec = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .FirstOrDefaultAsync(ct);

        return topSpec != null ? (topSpec.Spec, topSpec.Count) : null;
    }

    private async Task<int> GetMaxKillsOnSingleSpecAsync(Guid playerId, CancellationToken ct)
    {
        var specKills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .GroupBy(x => x.pe.Profession)
            .Select(g => g.Count())
            .ToListAsync(ct);

        return specKills.Count > 0 ? specKills.Max() : 0;
    }

    private async Task<(string bossName, int specCount)?> GetTopBossBySpecDiversityAsync(Guid playerId, CancellationToken ct)
    {
        // Get all successful kills with elite specs, grouped by boss
        var killsByBoss = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .Select(x => new { x.e.TriggerId, x.e.BossName, x.pe.Profession })
            .ToListAsync(ct);

        if (killsByBoss.Count == 0) return null;

        // Group by boss and count unique specs
        var bossDiversity = killsByBoss
            .GroupBy(x => new { x.TriggerId, x.BossName })
            .Select(g => new { g.Key.BossName, SpecCount = g.Select(x => x.Profession).Distinct().Count() })
            .OrderByDescending(x => x.SpecCount)
            .FirstOrDefault();

        return bossDiversity != null ? (bossDiversity.BossName, bossDiversity.SpecCount) : null;
    }

    private async Task<ClassCompletionistProgress?> GetClassCompletionistProgressAsync(Guid playerId, CancellationToken ct)
    {
        // Get all specs the player has used successfully, grouped by boss
        var playerSpecsByBoss = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .Select(x => new { x.pe.Profession, x.e.BossName })
            .Distinct()
            .ToListAsync(ct);

        // Find the best boss+profession combo (the one with the most specs completed on a single boss)
        ClassCompletionistProgress? bestProgress = null;

        foreach (var (profession, eliteSpecs) in AchievementDefinitions.EliteSpecsByProfession)
        {
            // Group by boss and find the boss with the most specs of this profession
            var bosses = playerSpecsByBoss
                .Where(x => eliteSpecs.Contains(x.Profession))
                .GroupBy(x => x.BossName)
                .Select(g => new
                {
                    BossName = g.Key,
                    CompletedSpecs = g.Select(x => x.Profession).ToList(),
                    SpecCount = g.Count()
                })
                .OrderByDescending(x => x.SpecCount)
                .ToList();

            foreach (var boss in bosses)
            {
                var missingSpecs = eliteSpecs.Where(spec => !boss.CompletedSpecs.Contains(spec)).ToList();

                // Check if this boss+profession combo is better than our current best
                if (bestProgress == null || boss.SpecCount > bestProgress.CompletedSpecs)
                {
                    bestProgress = new ClassCompletionistProgress(
                        profession,
                        boss.SpecCount,
                        boss.CompletedSpecs,
                        missingSpecs,
                        boss.BossName
                    );
                }
            }
        }

        return bestProgress;
    }

    private async Task<int> GetMostResurrectsCountAsync(Guid playerId, CancellationToken ct)
    {
        // Get all encounters where this player had the most resurrects
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success && x.pe.Resurrects > 0)
            .Select(x => new { x.pe.EncounterId, x.pe.Resurrects })
            .ToListAsync(ct);

        var count = 0;
        foreach (var pe in playerEncounters)
        {
            var maxInEncounter = await _db.PlayerEncounters
                .Where(x => x.EncounterId == pe.EncounterId)
                .MaxAsync(x => x.Resurrects, ct);

            if (pe.Resurrects == maxInEncounter)
                count++;
        }

        return count;
    }

    private async Task<int> GetMostCCCountAsync(Guid playerId, CancellationToken ct)
    {
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success && x.pe.BreakbarDamage > 0)
            .Select(x => new { x.pe.EncounterId, x.pe.BreakbarDamage })
            .ToListAsync(ct);

        var count = 0;
        foreach (var pe in playerEncounters)
        {
            var maxInEncounter = await _db.PlayerEncounters
                .Where(x => x.EncounterId == pe.EncounterId)
                .MaxAsync(x => x.BreakbarDamage ?? 0, ct);

            if (pe.BreakbarDamage == maxInEncounter)
                count++;
        }

        return count;
    }

    private async Task<int> GetHighestBoonDpsCountAsync(Guid playerId, CancellationToken ct)
    {
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                        (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .Select(x => new { x.pe.EncounterId, x.pe.Dps })
            .ToListAsync(ct);

        var count = 0;
        foreach (var pe in playerEncounters)
        {
            var boonPlayersInEncounter = await _db.PlayerEncounters
                .Where(x => x.EncounterId == pe.EncounterId)
                .Where(x => (x.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                            (x.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
                .ToListAsync(ct);

            var maxBoonDps = boonPlayersInEncounter.Count > 0 ? boonPlayersInEncounter.Max(x => x.Dps) : 0;
            if (pe.Dps == maxBoonDps)
                count++;
        }

        return count;
    }

    private async Task<int> GetSessionCountAsync(Guid playerId, CancellationToken ct)
    {
        // A session is a unique date on which the player participated
        return await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId)
            .Select(x => x.e.EncounterTime.Date)
            .Distinct()
            .CountAsync(ct);
    }

    private async Task<int> GetPersonalBestCountAsync(Guid playerId, CancellationToken ct)
    {
        // Count how many times the player has beaten their own DPS record on any boss (only 2025+)
        var encounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => x.e.EncounterTime >= AchievementBaselineDate)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.e.TriggerId, x.e.IsCM, x.pe.Dps, x.e.EncounterTime })
            .ToListAsync(ct);

        var personalBests = new Dictionary<(int, bool), int>(); // (triggerId, isCM) -> best DPS
        var pbCount = 0;

        foreach (var enc in encounters)
        {
            var key = (enc.TriggerId, enc.IsCM);
            if (personalBests.TryGetValue(key, out var currentBest))
            {
                if (enc.Dps > currentBest)
                {
                    personalBests[key] = enc.Dps;
                    pbCount++;
                }
            }
            else
            {
                personalBests[key] = enc.Dps;
                // First kill doesn't count as "beating" personal best
            }
        }

        return pbCount;
    }

    private async Task<int> GetCurrentDeathlessStreakAsync(Guid playerId, CancellationToken ct)
    {
        // Get all successful kills ordered by time
        var encounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderByDescending(x => x.e.EncounterTime)
            .Select(x => x.pe.Deaths)
            .ToListAsync(ct);

        var streak = 0;
        foreach (var deaths in encounters)
        {
            if (deaths == 0)
                streak++;
            else
                break;
        }

        return streak;
    }

    private async Task<int> GetMaxPartnerKillsAsync(Guid playerId, CancellationToken ct)
    {
        // Get all encounters for this player
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => x.e.Id)
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return 0;

        // Count kills with each partner
        var partnerCounts = await _db.PlayerEncounters
            .Where(pe => myEncounters.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .GroupBy(pe => pe.PlayerId)
            .Select(g => g.Count())
            .ToListAsync(ct);

        return partnerCounts.Count > 0 ? partnerCounts.Max() : 0;
    }

    private async Task<int> GetMaxTrioKillsAsync(Guid playerId, CancellationToken ct)
    {
        // This is more complex - need to find pairs of partners
        // For simplicity, we'll use a different approach
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => x.e.Id)
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return 0;

        // Get all co-occurrences
        var coPlayers = await _db.PlayerEncounters
            .Where(pe => myEncounters.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId })
            .ToListAsync(ct);

        // Group by encounter, then find all pairs that appear together
        var encounterPlayers = coPlayers.GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToHashSet());

        // Count pair occurrences
        var pairCounts = new Dictionary<(Guid, Guid), int>();
        foreach (var (encounterId, players) in encounterPlayers)
        {
            var playerList = players.ToList();
            for (var i = 0; i < playerList.Count; i++)
            {
                for (var j = i + 1; j < playerList.Count; j++)
                {
                    var pair = playerList[i].CompareTo(playerList[j]) < 0
                        ? (playerList[i], playerList[j])
                        : (playerList[j], playerList[i]);

                    pairCounts.TryGetValue(pair, out var count);
                    pairCounts[pair] = count + 1;
                }
            }
        }

        return pairCounts.Count > 0 ? pairCounts.Values.Max() : 0;
    }

    private async Task<(DateTimeOffset achievedAt, Guid partnerId)?> GetPartnerKillsWithDateAsync(Guid playerId, int threshold, CancellationToken ct)
    {
        // Get all encounters for this player with dates
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.e.Id, x.e.EncounterTime })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return null;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();

        // Get all partners in these encounters
        var partnersByEncounter = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId })
            .ToListAsync(ct);

        var partnerLookup = partnersByEncounter
            .GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToHashSet());

        // For each partner, track kill count and find when threshold is reached
        var partnerCounts = new Dictionary<Guid, int>();
        foreach (var enc in myEncounters)
        {
            if (!partnerLookup.TryGetValue(enc.Id, out var partners)) continue;
            foreach (var partnerId in partners)
            {
                partnerCounts.TryGetValue(partnerId, out var count);
                partnerCounts[partnerId] = count + 1;

                if (partnerCounts[partnerId] == threshold)
                {
                    return (enc.EncounterTime, partnerId);
                }
            }
        }

        return null;
    }

    private async Task<(DateTimeOffset achievedAt, Guid partner1, Guid partner2)?> GetTrioKillsWithDateAsync(Guid playerId, int threshold, CancellationToken ct)
    {
        // Get all encounters for this player with dates
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.e.Id, x.e.EncounterTime })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return null;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();

        // Get all partners in these encounters
        var partnersByEncounter = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId })
            .ToListAsync(ct);

        var partnerLookup = partnersByEncounter
            .GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToList());

        // Track pair counts
        var pairCounts = new Dictionary<(Guid, Guid), int>();
        foreach (var enc in myEncounters)
        {
            if (!partnerLookup.TryGetValue(enc.Id, out var players)) continue;

            for (var i = 0; i < players.Count; i++)
            {
                for (var j = i + 1; j < players.Count; j++)
                {
                    var pair = players[i].CompareTo(players[j]) < 0
                        ? (players[i], players[j])
                        : (players[j], players[i]);

                    pairCounts.TryGetValue(pair, out var count);
                    pairCounts[pair] = count + 1;

                    if (pairCounts[pair] == threshold)
                    {
                        return (enc.EncounterTime, pair.Item1, pair.Item2);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Get max kills with a party member (same squad group)
    /// </summary>
    private async Task<int> GetMaxPartyPartnerKillsAsync(Guid playerId, CancellationToken ct)
    {
        // Get all encounters for this player with their squad group
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => new { x.e.Id, x.pe.SquadGroup })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return 0;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();
        var myGroupByEncounter = myEncounters.ToDictionary(e => e.Id, e => e.SquadGroup);

        // Get all party members (same squad group) in these encounters
        var partyMembers = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId, pe.SquadGroup })
            .ToListAsync(ct);

        // Filter to only same party (same squad group)
        var partnerCounts = new Dictionary<Guid, int>();
        foreach (var member in partyMembers)
        {
            if (!myGroupByEncounter.TryGetValue(member.EncounterId, out var myGroup)) continue;
            // Only count if in same party (same squad group)
            if (myGroup != null && member.SquadGroup == myGroup)
            {
                partnerCounts.TryGetValue(member.PlayerId, out var count);
                partnerCounts[member.PlayerId] = count + 1;
            }
        }

        return partnerCounts.Count > 0 ? partnerCounts.Values.Max() : 0;
    }

    /// <summary>
    /// Get max kills with two party members (same squad group)
    /// </summary>
    private async Task<int> GetMaxPartyTrioKillsAsync(Guid playerId, CancellationToken ct)
    {
        // Get all encounters for this player with their squad group
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => new { x.e.Id, x.pe.SquadGroup })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return 0;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();
        var myGroupByEncounter = myEncounters.ToDictionary(e => e.Id, e => e.SquadGroup);

        // Get all party members (same squad group) in these encounters
        var partyMembers = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId, pe.SquadGroup })
            .ToListAsync(ct);

        // Group by encounter and filter to same party
        var partnerLookup = partyMembers
            .Where(m => myGroupByEncounter.TryGetValue(m.EncounterId, out var myGroup) && myGroup != null && m.SquadGroup == myGroup)
            .GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToList());

        // Count pair occurrences
        var pairCounts = new Dictionary<(Guid, Guid), int>();
        foreach (var encounterId in encounterIds)
        {
            if (!partnerLookup.TryGetValue(encounterId, out var players)) continue;

            for (var i = 0; i < players.Count; i++)
            {
                for (var j = i + 1; j < players.Count; j++)
                {
                    var pair = players[i].CompareTo(players[j]) < 0
                        ? (players[i], players[j])
                        : (players[j], players[i]);

                    pairCounts.TryGetValue(pair, out var count);
                    pairCounts[pair] = count + 1;
                }
            }
        }

        return pairCounts.Count > 0 ? pairCounts.Values.Max() : 0;
    }

    /// <summary>
    /// Get party partner kills with the date when threshold was reached
    /// </summary>
    private async Task<(DateTimeOffset achievedAt, Guid partnerId)?> GetPartyPartnerKillsWithDateAsync(Guid playerId, int threshold, CancellationToken ct)
    {
        // Get all encounters for this player with dates and squad group
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.e.Id, x.e.EncounterTime, x.pe.SquadGroup })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return null;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();
        var myGroupByEncounter = myEncounters.ToDictionary(e => e.Id, e => e.SquadGroup);

        // Get all party members in these encounters
        var partyMembers = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId, pe.SquadGroup })
            .ToListAsync(ct);

        var partnerLookup = partyMembers
            .Where(m => myGroupByEncounter.TryGetValue(m.EncounterId, out var myGroup) && myGroup != null && m.SquadGroup == myGroup)
            .GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToHashSet());

        // For each partner, track kill count and find when threshold is reached
        var partnerCounts = new Dictionary<Guid, int>();
        foreach (var enc in myEncounters)
        {
            if (!partnerLookup.TryGetValue(enc.Id, out var partners)) continue;
            foreach (var partnerId in partners)
            {
                partnerCounts.TryGetValue(partnerId, out var count);
                partnerCounts[partnerId] = count + 1;

                if (partnerCounts[partnerId] == threshold)
                {
                    return (enc.EncounterTime, partnerId);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Get party trio kills with the date when threshold was reached
    /// </summary>
    private async Task<(DateTimeOffset achievedAt, Guid partner1, Guid partner2)?> GetPartyTrioKillsWithDateAsync(Guid playerId, int threshold, CancellationToken ct)
    {
        // Get all encounters for this player with dates and squad group
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.e.Id, x.e.EncounterTime, x.pe.SquadGroup })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return null;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();
        var myGroupByEncounter = myEncounters.ToDictionary(e => e.Id, e => e.SquadGroup);

        // Get all party members in these encounters
        var partyMembers = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId, pe.SquadGroup })
            .ToListAsync(ct);

        var partnerLookup = partyMembers
            .Where(m => myGroupByEncounter.TryGetValue(m.EncounterId, out var myGroup) && myGroup != null && m.SquadGroup == myGroup)
            .GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToList());

        // Track pair counts
        var pairCounts = new Dictionary<(Guid, Guid), int>();
        foreach (var enc in myEncounters)
        {
            if (!partnerLookup.TryGetValue(enc.Id, out var players)) continue;

            for (var i = 0; i < players.Count; i++)
            {
                for (var j = i + 1; j < players.Count; j++)
                {
                    var pair = players[i].CompareTo(players[j]) < 0
                        ? (players[i], players[j])
                        : (players[j], players[i]);

                    pairCounts.TryGetValue(pair, out var count);
                    pairCounts[pair] = count + 1;

                    if (pairCounts[pair] == threshold)
                    {
                        return (enc.EncounterTime, pair.Item1, pair.Item2);
                    }
                }
            }
        }

        return null;
    }

    #endregion

    private record AchievementPayload(
        string? PlayerName,
        string AchievementCode,
        string AchievementName,
        string Description,
        bool IsGuildAchievement
    );
}
