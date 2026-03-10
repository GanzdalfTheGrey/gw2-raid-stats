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
/// - Heavy Metal / Cloth Squad / Leather Lovers (wing with armor class)
/// </summary>
public class GuildMilestoneChecker : IAchievementChecker
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;

    // Threshold for boon support
    private const decimal BoonSupportThreshold = 10m;

    // Armor class definitions
    private static readonly Dictionary<string, string[]> ArmorClasses = new()
    {
        { "heavy", new[] { "Guardian", "Warrior", "Revenant" } },
        { "medium", new[] { "Engineer", "Ranger", "Thief" } },
        { "light", new[] { "Elementalist", "Mesmer", "Necromancer" } }
    };

    // Achievement codes by armor class
    private static readonly Dictionary<string, string> ArmorAchievementCodes = new()
    {
        { "heavy", "heavy_metal" },
        { "medium", "leather_lovers" },
        { "light", "cloth_squad" }
    };

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

        // Armor class wing achievements (Heavy Metal, Cloth Squad, Leather Lovers)
        var armorClassUnlock = await CheckArmorClassWingAchievementsAsync(encounter, ct);
        if (armorClassUnlock != null)
        {
            unlocks.Add(armorClassUnlock);

            // Check for meta-achievements (master achievements and triple threat)
            var metaUnlocks = await CheckArmorClassMetaAchievementsAsync(encounter, armorClassUnlock, ct);
            unlocks.AddRange(metaUnlocks);
        }

        // Expansion-themed achievements (Thorn in My Side, Ring of Fire)
        var expansionUnlocks = await CheckExpansionThemedAchievementsAsync(encounter, ct);
        unlocks.AddRange(expansionUnlocks);

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
            // Only award if the current encounter is one of the kills being used
            var normalizedCurrentTrigger = AchievementDefinitions.NormalizeTriggerId(encounter.TriggerId);
            if (!allWingBosses.Contains(normalizedCurrentTrigger)) return null;

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

        // Build set of valid trigger IDs for this wing (both NM and CM versions)
        // Most CMs use same trigger ID as NM, but some (like Decima CM=26867) have different IDs
        var wingBossSet = wingBosses.Select(AchievementDefinitions.NormalizeTriggerId).ToHashSet();

        // Add Wing 8 CM trigger IDs if checking Wing 8
        if (wing == 8)
        {
            foreach (var cmTriggerId in AchievementDefinitions.Wing8CMBosses)
            {
                wingBossSet.Add(cmTriggerId);
            }
        }

        // Get all successful kills in the session window, filtering by trigger ID directly
        // This ensures we get both NM and CM kills regardless of Wing field
        var sessionEncounters = await _db.Encounters
            .Where(e => e.Success)
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime <= sessionEnd)
            .Where(e => wingBossSet.Contains(e.TriggerId))
            .Select(e => new { e.Id, e.TriggerId, e.BossName, e.EncounterTime })
            .ToListAsync(ct);

        // Normalize trigger IDs and take most recent kill per boss
        // Map CM trigger IDs back to their NM equivalents for grouping
        var bossKills = sessionEncounters
            .Select(e => new {
                e.Id,
                TriggerId = NormalizeTriggerIdForWing(e.TriggerId, wing),
                e.BossName,
                e.EncounterTime
            })
            .GroupBy(e => e.TriggerId)
            .Select(g => g.OrderByDescending(e => e.EncounterTime).First())
            .ToList();

        // Need all bosses in the wing killed
        if (bossKills.Count != wingBosses.Length) return unlocks;

        // Only award if the current encounter is one of the boss kills being used
        // This prevents re-awarding when a later encounter looks back and finds old kills
        if (!bossKills.Any(k => k.Id == encounter.Id)) return unlocks;

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

        // For each role group, check that:
        // 1. Each boss has at least one player in this role group
        // 2. No player repeats across bosses
        var isMusicalChairs = true;
        foreach (var (groupName, roles) in roleGroups)
        {
            var playersInRoleGroup = playerEncounters
                .Where(pe => pe.Role != null && roles.Contains(pe.Role))
                .GroupBy(pe => pe.EncounterId)
                .ToDictionary(g => g.Key, g => g.Select(pe => pe.AccountName).ToList());

            // Each boss must have at least one player in this role group
            if (playersInRoleGroup.Count != bossKills.Count)
            {
                // Some bosses don't have this role (missing role data)
                isMusicalChairs = false;
                break;
            }

            // No player should appear more than once across all bosses in this role group
            var allPlayersInGroup = playersInRoleGroup.Values.SelectMany(p => p).ToList();
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

    /// <summary>
    /// Armor class wing achievements - Complete an entire wing with only one armor class,
    /// featuring at least one of each base profession in that class.
    /// - Heavy Metal: Guardian, Warrior, Revenant
    /// - Cloth Squad: Elementalist, Mesmer, Necromancer
    /// - Leather Lovers: Engineer, Ranger, Thief
    /// </summary>
    private async Task<AchievementUnlock?> CheckArmorClassWingAchievementsAsync(
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        // Only check for raid encounters (wings 1-8)
        if (!encounter.Wing.HasValue || encounter.Wing < 1 || encounter.Wing > 8) return null;

        var wing = encounter.Wing.Value;
        var wingBosses = AchievementDefinitions.WingMasterBosses.GetValueOrDefault(wing);
        if (wingBosses == null) return null;

        // Look for kills within 8 hours (same session window as full clear)
        var sessionStart = encounter.EncounterTime.AddHours(-8);
        var sessionEnd = encounter.EncounterTime;

        // Build set of valid trigger IDs for this wing (both NM and CM versions)
        var wingBossSet = wingBosses.Select(AchievementDefinitions.NormalizeTriggerId).ToHashSet();

        // Add Wing 8 CM trigger IDs if checking Wing 8
        if (wing == 8)
        {
            foreach (var cmTriggerId in AchievementDefinitions.Wing8CMBosses)
            {
                wingBossSet.Add(cmTriggerId);
            }
        }

        // Get all successful kills in the session window, filtering by trigger ID
        var sessionEncounters = await _db.Encounters
            .Where(e => e.Success)
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime <= sessionEnd)
            .Where(e => wingBossSet.Contains(e.TriggerId))
            .Select(e => new { e.Id, e.TriggerId, e.BossName, e.EncounterTime })
            .ToListAsync(ct);

        // Normalize trigger IDs and take most recent kill per boss
        var bossKills = sessionEncounters
            .Select(e => new {
                e.Id,
                TriggerId = NormalizeTriggerIdForWing(e.TriggerId, wing),
                e.BossName,
                e.EncounterTime
            })
            .GroupBy(e => e.TriggerId)
            .Select(g => g.OrderByDescending(e => e.EncounterTime).First())
            .ToList();

        // Need all bosses in the wing killed
        if (bossKills.Count != wingBosses.Length) return null;

        // Only award if the current encounter is one of the boss kills being used
        if (!bossKills.Any(k => k.Id == encounter.Id)) return null;

        // Get all players and their base professions for each encounter
        var encounterIds = bossKills.Select(k => k.Id).ToList();
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => encounterIds.Contains(x.pe.EncounterId))
            .Select(x => new
            {
                x.pe.EncounterId,
                x.p.AccountName,
                x.pe.Profession // This is the elite spec, we need to map to base profession
            })
            .ToListAsync(ct);

        // Map all professions to their base professions
        var playerBaseProfessions = playerEncounters
            .Select(pe => new
            {
                pe.EncounterId,
                pe.AccountName,
                BaseProfession = AchievementDefinitions.GetBaseProfession(pe.Profession)
            })
            .ToList();

        // Get all unique base professions across all encounters
        var allBaseProfessions = playerBaseProfessions
            .Select(p => p.BaseProfession)
            .Distinct()
            .ToList();

        // Check each armor class
        foreach (var (armorClass, professions) in ArmorClasses)
        {
            var professionSet = professions.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // All players must be in this armor class
            var allPlayersInClass = allBaseProfessions.All(bp =>
                bp != null && professionSet.Contains(bp));

            if (!allPlayersInClass) continue;

            // Must have at least one of each profession in the armor class (across all encounters)
            var professionsPresent = allBaseProfessions
                .Where(bp => bp != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var hasAllProfessions = professions.All(p => professionsPresent.Contains(p));

            if (hasAllProfessions)
            {
                var achievementCode = ArmorAchievementCodes[armorClass];
                var lastBossTime = bossKills.Max(k => k.EncounterTime);

                return new AchievementUnlock(
                    achievementCode,
                    null, // Guild achievement
                    new
                    {
                        wing,
                        armor_class = armorClass,
                        professions_used = professionsPresent.ToList(),
                        bosses = bossKills.Select(b => b.BossName).ToList(),
                        session_date = lastBossTime.Date
                    },
                    lastBossTime
                );
            }
        }

        return null;
    }

    /// <summary>
    /// Check for armor class meta-achievements:
    /// - Master achievements (complete an armor class on all 8 wings)
    /// - Triple Threat (complete all 3 armor classes on the same wing)
    /// </summary>
    private async Task<List<AchievementUnlock>> CheckArmorClassMetaAchievementsAsync(
        Database.Entities.EncounterEntity encounter,
        AchievementUnlock armorClassUnlock,
        CancellationToken ct)
    {
        var unlocks = new List<AchievementUnlock>();

        if (!encounter.Wing.HasValue) return unlocks;
        var wing = encounter.Wing.Value;

        // Map achievement codes to their master versions
        var masterAchievements = new Dictionary<string, string>
        {
            { "heavy_metal", "heavy_metal_master" },
            { "cloth_squad", "cloth_squad_master" },
            { "leather_lovers", "leather_lovers_master" }
        };

        var armorAchievementCodes = new[] { "heavy_metal", "cloth_squad", "leather_lovers" };

        // Get all existing armor class achievements from database
        var existingAchievements = await _db.GuildAchievements
            .Where(ga => armorAchievementCodes.Contains(ga.AchievementCode))
            .Select(ga => new { ga.AchievementCode, ga.Context })
            .ToListAsync(ct);

        // Parse wing numbers from metadata for each achievement type
        var wingsByAchievement = new Dictionary<string, HashSet<int>>();
        foreach (var code in armorAchievementCodes)
        {
            wingsByAchievement[code] = new HashSet<int>();
        }

        foreach (var achievement in existingAchievements)
        {
            if (string.IsNullOrEmpty(achievement.Context)) continue;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(achievement.Context);
                if (doc.RootElement.TryGetProperty("wing", out var wingProp) && wingProp.TryGetInt32(out var achievementWing))
                {
                    wingsByAchievement[achievement.AchievementCode].Add(achievementWing);
                }
            }
            catch
            {
                // Ignore parse errors
            }
        }

        // Add the current unlock's wing
        wingsByAchievement[armorClassUnlock.Code].Add(wing);

        // Check for master achievement (all 8 wings completed for this armor class)
        if (masterAchievements.TryGetValue(armorClassUnlock.Code, out var masterCode))
        {
            var wingsCompleted = wingsByAchievement[armorClassUnlock.Code];
            if (wingsCompleted.Count == 8)
            {
                // Check if master achievement already exists
                var masterExists = await _db.GuildAchievements
                    .AnyAsync(ga => ga.AchievementCode == masterCode, ct);

                if (!masterExists)
                {
                    unlocks.Add(new AchievementUnlock(
                        masterCode,
                        null,
                        new
                        {
                            armor_class = armorClassUnlock.Code,
                            wings_completed = wingsCompleted.OrderBy(w => w).ToList()
                        },
                        encounter.EncounterTime
                    ));
                }
            }
        }

        // Check for Triple Threat (all 3 armor classes on the same wing)
        var allThreeOnWing = armorAchievementCodes.All(code => wingsByAchievement[code].Contains(wing));
        if (allThreeOnWing)
        {
            // Check if Triple Threat for this wing already exists
            var tripleExists = await _db.GuildAchievements
                .Where(ga => ga.AchievementCode == "triple_threat")
                .Where(ga => ga.Context != null && ga.Context.Contains($"\"wing\":{wing}"))
                .AnyAsync(ct);

            if (!tripleExists)
            {
                unlocks.Add(new AchievementUnlock(
                    "triple_threat",
                    null,
                    new
                    {
                        wing,
                        achievements = armorAchievementCodes.ToList()
                    },
                    encounter.EncounterTime
                ));
            }
        }

        return unlocks;
    }

    /// <summary>
    /// Normalize trigger ID for wing grouping - maps CM trigger IDs to their NM equivalents
    /// so that KC CM and KC NM are grouped as the same boss.
    /// </summary>
    private static int NormalizeTriggerIdForWing(int triggerId, int wing)
    {
        // First apply standard normalization (e.g., Matthias)
        triggerId = AchievementDefinitions.NormalizeTriggerId(triggerId);

        // Wing 8: Map Decima CM (26867) to Decima NM (26774)
        if (wing == 8 && triggerId == 26867)
        {
            return 26774;
        }

        // Other CMs use the same trigger ID as NM (with IsCM flag), so no mapping needed
        return triggerId;
    }

    /// <summary>
    /// Expansion-themed achievements:
    /// - Thorn in My Side: Complete Wings 1-4 on only Heart of Thorns specs
    /// - Ring of Fire: Complete Wings 5-7 on only Path of Fire specs
    /// </summary>
    private async Task<List<AchievementUnlock>> CheckExpansionThemedAchievementsAsync(
        Database.Entities.EncounterEntity encounter,
        CancellationToken ct)
    {
        var unlocks = new List<AchievementUnlock>();

        // Only check for raid encounters
        if (!encounter.Wing.HasValue || encounter.Wing < 1 || encounter.Wing > 7) return unlocks;

        // Check Thorn in My Side (Wings 1-4, HoT specs only)
        if (encounter.Wing >= 1 && encounter.Wing <= 4)
        {
            var thornUnlock = await CheckExpansionAchievementAsync(
                encounter, 1, 4, "thorn_in_my_side", "Wings 1-4 HoT Only",
                AchievementDefinitions.IsHotSpec, ct);
            if (thornUnlock != null) unlocks.Add(thornUnlock);
        }

        // Check Ring of Fire (Wings 5-7, PoF specs only)
        if (encounter.Wing >= 5 && encounter.Wing <= 7)
        {
            var ringUnlock = await CheckExpansionAchievementAsync(
                encounter, 5, 7, "ring_of_fire", "Wings 5-7 PoF Only",
                AchievementDefinitions.IsPofSpec, ct);
            if (ringUnlock != null) unlocks.Add(ringUnlock);
        }

        return unlocks;
    }

    private async Task<AchievementUnlock?> CheckExpansionAchievementAsync(
        Database.Entities.EncounterEntity encounter,
        int startWing,
        int endWing,
        string achievementCode,
        string contextBoss,
        Func<string, bool> specCheck,
        CancellationToken ct)
    {
        // Build list of required boss trigger IDs for these wings
        var requiredBosses = new List<int>();
        for (int w = startWing; w <= endWing; w++)
        {
            var bosses = AchievementDefinitions.WingMasterBosses.GetValueOrDefault(w);
            if (bosses != null) requiredBosses.AddRange(bosses);
        }

        // Also include alternate trigger IDs for querying (e.g., Matthias alternate)
        var queryTriggerIds = requiredBosses.ToHashSet();
        queryTriggerIds.Add(16115); // Matthias alternate trigger ID

        // Look for kills within 8 hours (same session window as other achievements)
        var sessionStart = encounter.EncounterTime.AddHours(-8);
        var sessionEnd = encounter.EncounterTime;

        // Get all successful kills in the session window for these wings
        var sessionEncounters = await _db.Encounters
            .Where(e => e.Success)
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime <= sessionEnd)
            .Where(e => queryTriggerIds.Contains(e.TriggerId))
            .Select(e => new { e.Id, e.TriggerId, e.BossName, e.EncounterTime })
            .ToListAsync(ct);

        // Normalize trigger IDs and take most recent kill per boss
        var bossKills = sessionEncounters
            .Select(e => new {
                e.Id,
                TriggerId = AchievementDefinitions.NormalizeTriggerId(e.TriggerId),
                e.BossName,
                e.EncounterTime
            })
            .GroupBy(e => e.TriggerId)
            .Select(g => g.OrderByDescending(e => e.EncounterTime).First())
            .ToList();

        // Check if all required bosses were killed
        var bossesCleared = bossKills.Select(k => k.TriggerId).ToHashSet();
        if (!requiredBosses.All(b => bossesCleared.Contains(b))) return null;

        // Only award if the current encounter is one of the boss kills being used
        if (!bossKills.Any(k => k.Id == encounter.Id)) return null;

        // Get all players and their professions for each encounter
        var encounterIds = bossKills.Select(k => k.Id).ToList();
        var playerEncounters = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId))
            .Select(pe => new { pe.EncounterId, pe.Profession })
            .ToListAsync(ct);

        // Check if ALL players across ALL encounters used the correct expansion specs
        var allSpecsValid = playerEncounters.All(pe => specCheck(pe.Profession));
        if (!allSpecsValid) return null;

        var lastBossTime = bossKills.Max(k => k.EncounterTime);

        return new AchievementUnlock(
            achievementCode,
            null, // Guild achievement
            new
            {
                boss = contextBoss,
                bosses = bossKills.Select(b => b.BossName).ToList(),
                session_date = lastBossTime.Date
            },
            lastBossTime
        );
    }
}
