using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Services.Achievements.Progress;

namespace GW2RaidStats.Infrastructure.Services.Achievements;

/// <summary>
/// Service for querying achievements and progress. Read-only operations.
/// </summary>
public class AchievementQueryService
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;
    private readonly PlayerHistoryCalculator _historyCalculator;

    public AchievementQueryService(
        RaidStatsDb db,
        IncludedPlayerService includedPlayerService,
        PlayerHistoryCalculator historyCalculator)
    {
        _db = db;
        _includedPlayerService = includedPlayerService;
        _historyCalculator = historyCalculator;
    }

    #region Player Achievements

    /// <summary>
    /// Get all achievements for a player
    /// </summary>
    public async Task<List<PlayerAchievementDto>> GetPlayerAchievementsAsync(
        Guid playerId,
        CancellationToken ct = default)
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
    /// Get achievement progress for a player (achievements not yet earned)
    /// </summary>
    public async Task<List<AchievementProgressDto>> GetProgressAsync(
        Guid playerId,
        CancellationToken ct = default)
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

            var progress = await _historyCalculator.GetWingMasterProgressAsync(playerId, wingNum.Value, ct);
            result.Add(new AchievementProgressDto(
                wingDef.Code,
                wingDef.Name,
                wingDef.Description,
                wingDef.Category.ToString(),
                progress.completed,
                progress.total,
                $"{progress.completed}/{progress.total} boss/role combos"
            ));
        }

        // Spec Diversity - Versatile (10 specs on one boss)
        if (!earnedCodes.Contains("versatile"))
        {
            var topBoss = await _historyCalculator.GetTopBossBySpecDiversityAsync(playerId, ct);
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
            var topBoss = await _historyCalculator.GetTopBossBySpecDiversityAsync(playerId, ct);
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
            var topSpec = await _historyCalculator.GetTopSpecKillsAsync(playerId, ct);
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
            var classProgress = await _historyCalculator.GetClassCompletionistProgressAsync(playerId, ct);
            if (classProgress != null)
            {
                var specsCount = classProgress.Value.specs.Count;
                var progressText = $"{specsCount}/4 {classProgress.Value.profession} specs on {classProgress.Value.bossName}";
                result.Add(new AchievementProgressDto("class_completionist", "Class Completionist",
                    "Complete a boss on every elite spec for a single profession",
                    "SpecDiversity", specsCount, 4, progressText));
            }
        }

        // Support - Guardian Angel (5 times with most resurrects)
        if (!earnedCodes.Contains("guardian_angel"))
        {
            var count = await _historyCalculator.GetMostResurrectsCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("guardian_angel", "Guardian Angel", "Have the most resurrects in a successful kill (5+ times)",
                "Support", Math.Min(count, 5), 5, $"{Math.Min(count, 5)}/5 times"));
        }

        // Support - CC Champion (10 times with most CC)
        if (!earnedCodes.Contains("cc_champion"))
        {
            var count = await _historyCalculator.GetMostCCCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("cc_champion", "CC Champion", "Deal the most breakbar damage in a successful kill (10+ times)",
                "Support", Math.Min(count, 10), 10, $"{Math.Min(count, 10)}/10 times"));
        }

        // Support - The Enabler (25 times with highest boon DPS)
        if (!earnedCodes.Contains("the_enabler"))
        {
            var count = await _historyCalculator.GetHighestBoonDpsCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("the_enabler", "The Enabler", "Have the highest boon DPS in a successful kill (25+ times)",
                "Support", Math.Min(count, 25), 25, $"{Math.Min(count, 25)}/25 times"));
        }

        // Dedication - The Regular (25 sessions)
        if (!earnedCodes.Contains("the_regular"))
        {
            var sessionCount = await _historyCalculator.GetSessionCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("the_regular", "The Regular", "Participate in 25 raid sessions",
                "Dedication", Math.Min(sessionCount, 25), 25, $"{Math.Min(sessionCount, 25)}/25 sessions"));
        }

        // Dedication - Dedicated (50 sessions)
        if (!earnedCodes.Contains("dedicated"))
        {
            var sessionCount = await _historyCalculator.GetSessionCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("dedicated", "Dedicated", "Participate in 50 raid sessions",
                "Dedication", Math.Min(sessionCount, 50), 50, $"{Math.Min(sessionCount, 50)}/50 sessions"));
        }

        // Growth - Keeping Up (5 personal bests)
        if (!earnedCodes.Contains("keeping_up"))
        {
            var pbResult = await _historyCalculator.GetPersonalBestCountAsync(playerId, ct);
            result.Add(new AchievementProgressDto("keeping_up", "Keeping Up", "Beat your personal DPS best on a single boss 5+ times",
                "Growth", Math.Min(pbResult.count, 5), 5, $"{Math.Min(pbResult.count, 5)}/5 personal bests"));
        }

        // Social - Dynamic Duo (50 bosses with same party member)
        if (!earnedCodes.Contains("dynamic_duo"))
        {
            var maxPartnerKills = await _historyCalculator.GetMaxPartyPartnerKillsAsync(playerId, ct);
            result.Add(new AchievementProgressDto("dynamic_duo", "Dynamic Duo", "Complete 50 bosses with the same party member",
                "Social", Math.Min(maxPartnerKills, 50), 50, $"{Math.Min(maxPartnerKills, 50)}/50 kills"));
        }

        // Social - Trio (25 bosses with same two party members)
        if (!earnedCodes.Contains("trio"))
        {
            var maxTrioKills = await _historyCalculator.GetMaxPartyTrioKillsAsync(playerId, ct);
            result.Add(new AchievementProgressDto("trio", "Trio", "Complete 25 bosses with the same two party members",
                "Social", Math.Min(maxTrioKills, 25), 25, $"{Math.Min(maxTrioKills, 25)}/25 kills"));
        }

        // Performance - Immortal (10 consecutive kills without dying)
        if (!earnedCodes.Contains("immortal"))
        {
            var currentStreak = await _historyCalculator.GetCurrentDeathlessStreakAsync(playerId, ct);
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

    #endregion

    #region Guild Achievements

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

    #endregion

    #region Detailed Progress

    /// <summary>
    /// Get detailed Wing Master progress showing which boss/role combos are missing
    /// </summary>
    public async Task<List<WingMasterDetailedProgressDto>> GetWingMasterDetailedProgressAsync(
        Guid playerId,
        CancellationToken ct)
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
        Guid playerId,
        CancellationToken ct)
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

    #endregion

    #region Helper Methods

    private static int? ExtractWingNumber(string code)
    {
        // Extract wing number from code like "wing_1_master"
        var parts = code.Split('_');
        if (parts.Length >= 2 && int.TryParse(parts[1], out var num))
            return num;
        return null;
    }

    #endregion
}
