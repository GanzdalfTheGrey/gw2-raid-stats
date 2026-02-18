using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Core;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.Infrastructure.Services.Achievements;

/// <summary>
/// Service for retroactively scanning and awarding achievements
/// </summary>
public class AchievementBackfillService
{
    private readonly RaidStatsDb _db;
    private readonly AchievementOrchestrator _orchestrator;
    private readonly IncludedPlayerService _includedPlayerService;
    private readonly ILogger<AchievementBackfillService> _logger;

    public AchievementBackfillService(
        RaidStatsDb db,
        AchievementOrchestrator orchestrator,
        IncludedPlayerService includedPlayerService,
        ILogger<AchievementBackfillService> logger)
    {
        _db = db;
        _orchestrator = orchestrator;
        _includedPlayerService = includedPlayerService;
        _logger = logger;
    }

    /// <summary>
    /// Backfill achievements for all players
    /// </summary>
    public async Task<BackfillResult> BackfillAllAchievementsAsync(
        IProgress<BackfillProgress>? progress = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting achievement backfill");

        // Get all included players (guild members only)
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedSet = includedAccounts.ToHashSet();

        // Get all unique encounter IDs that have at least one guild member
        var encounterIdsWithGuildMembers = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => includedSet.Contains(x.p.AccountName))
            .Select(x => x.pe.EncounterId)
            .Distinct()
            .ToListAsync(ct);

        // Now get the encounter details - this guarantees unique encounters
        var encounters = await _db.Encounters
            .Where(e => encounterIdsWithGuildMembers.Contains(e.Id))
            .OrderBy(e => e.EncounterTime)
            .Select(e => new { e.Id, e.BossName, e.EncounterTime })
            .ToListAsync(ct);

        // Filter out ignored encounters in memory (can't translate to SQL)
        encounters = encounters
            .Where(x => !WingMapping.IsIgnoredEncounter(x.BossName))
            .ToList();

        _logger.LogInformation("Found {Count} encounters with guild members to process", encounters.Count);

        var totalAwarded = 0;
        var processed = 0;
        var errors = new List<string>();

        // Count achievements before
        var beforeCount = await _db.PlayerAchievements.CountAsync(ct);
        var beforeGuildCount = await _db.GuildAchievements.CountAsync(ct);

        foreach (var encounter in encounters)
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            try
            {
                // This checks ALL guild members in this encounter through ALL checkers
                // Each encounter is processed exactly once
                await _orchestrator.CheckAfterEncounterAsync(encounter.Id, notify: false, ct);
            }
            catch (Exception ex)
            {
                errors.Add($"Encounter {encounter.Id}: {ex.Message}");
                _logger.LogWarning(ex, "Error processing achievements for encounter {EncounterId}", encounter.Id);
            }

            // Report progress every 100 encounters to avoid UI spam
            if (processed % 100 == 0 || processed == encounters.Count)
            {
                progress?.Report(new BackfillProgress(processed, encounters.Count, totalAwarded, errors.Count));
            }
        }

        // Count achievements after per-encounter checks
        var afterEncounterCount = await _db.PlayerAchievements.CountAsync(ct);
        var afterEncounterGuildCount = await _db.GuildAchievements.CountAsync(ct);
        totalAwarded = (afterEncounterCount - beforeCount) + (afterEncounterGuildCount - beforeGuildCount);

        _logger.LogInformation("Per-encounter check complete: {Awarded} achievements awarded", totalAwarded);

        // Check Former Champion achievement (requires historical DPS record analysis)
        var formerChampionAwarded = await CheckFormerChampionAchievementAsync(ct);
        totalAwarded += formerChampionAwarded;

        // Check multi-encounter guild achievements (flawless wings, wing compositions, etc.)
        var multiEncounterAwarded = await CheckMultiEncounterGuildAchievementsAsync(ct);
        totalAwarded += multiEncounterAwarded;

        _logger.LogInformation(
            "Backfill complete: {Processed} encounters, {Awarded} achievements awarded, {Errors} errors",
            processed, totalAwarded, errors.Count);

        return new BackfillResult(processed, totalAwarded, errors);
    }

    /// <summary>
    /// Check the "Former Champion" achievement for all players
    /// This requires historical analysis to find who ever held a DPS record.
    /// Awards to:
    /// 1. Anyone who held a record as of Jan 1, 2025 (even if set before that date)
    /// 2. Anyone who held a record at any point after Jan 1, 2025
    /// </summary>
    private async Task<int> CheckFormerChampionAchievementAsync(CancellationToken ct)
    {
        _logger.LogInformation("Checking Former Champion achievement (historical DPS record analysis)");

        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedSet = includedAccounts.ToHashSet();

        var cutoffDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Get ALL encounters ordered by time (we need full history to know who held records as of cutoff)
        var encounters = await _db.Encounters
            .Where(e => e.Success)
            .OrderBy(e => e.EncounterTime)
            .Select(e => new { e.Id, e.TriggerId, e.IsCM, e.EncounterTime, e.BossName })
            .ToListAsync(ct);

        // Track current record holders per boss/CM combo
        var recordHolders = new Dictionary<(int TriggerId, bool IsCM), (Guid PlayerId, int Dps)>();

        // Track all players who qualify for the achievement, with the date to award
        var qualifyingPlayers = new Dictionary<Guid, DateTimeOffset>();

        // Flag to track if we've passed the cutoff and awarded the "as of cutoff" holders
        var passedCutoff = false;

        foreach (var encounter in encounters)
        {
            ct.ThrowIfCancellationRequested();

            // When we cross the cutoff date, award all current record holders
            if (!passedCutoff && encounter.EncounterTime >= cutoffDate)
            {
                passedCutoff = true;
                // Everyone who holds a record as of Jan 1, 2025 gets the achievement
                foreach (var (_, holder) in recordHolders)
                {
                    if (!qualifyingPlayers.ContainsKey(holder.PlayerId))
                    {
                        qualifyingPlayers[holder.PlayerId] = cutoffDate;
                    }
                }
            }

            // Skip ignored encounters (Spirit Race, Statues, etc.)
            if (WingMapping.IsIgnoredEncounter(encounter.BossName))
                continue;

            var key = (encounter.TriggerId, encounter.IsCM);

            // Get player performances for this encounter (guild members only)
            var playerPerfs = await _db.PlayerEncounters
                .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
                .Where(x => x.pe.EncounterId == encounter.Id)
                .Where(x => includedSet.Contains(x.p.AccountName))
                .OrderByDescending(x => x.pe.Dps)
                .Select(x => new { x.pe.PlayerId, x.pe.Dps })
                .ToListAsync(ct);

            if (playerPerfs.Count == 0) continue;

            var topPerf = playerPerfs.First();

            // Check if this beats or sets the record
            if (!recordHolders.TryGetValue(key, out var currentRecord) || topPerf.Dps > currentRecord.Dps)
            {
                recordHolders[key] = (topPerf.PlayerId, topPerf.Dps);

                // If we're past the cutoff, this player held a record after Jan 1, 2025
                if (passedCutoff && !qualifyingPlayers.ContainsKey(topPerf.PlayerId))
                {
                    qualifyingPlayers[topPerf.PlayerId] = encounter.EncounterTime;
                }
            }
        }

        // Handle case where all encounters are before cutoff (no encounters after Jan 1, 2025 yet)
        // In this case, we still award to current record holders as of the cutoff
        if (!passedCutoff)
        {
            foreach (var (key, holder) in recordHolders)
            {
                if (!qualifyingPlayers.ContainsKey(holder.PlayerId))
                {
                    qualifyingPlayers[holder.PlayerId] = cutoffDate;
                }
            }
        }

        // Award "Former Champion" to everyone who qualifies
        var awarded = 0;
        foreach (var (playerId, achievedAt) in qualifyingPlayers)
        {
            var hasAchievement = await _db.PlayerAchievements
                .AnyAsync(pa => pa.PlayerId == playerId && pa.AchievementCode == "former_champion", ct);

            if (!hasAchievement)
            {
                var achievement = new PlayerAchievementEntity
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    AchievementCode = "former_champion",
                    AchievedAt = achievedAt,
                    Context = null,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                await _db.InsertAsync(achievement, token: ct);
                awarded++;

                var player = await _db.Players.FirstOrDefaultAsync(p => p.Id == playerId, ct);
                _logger.LogInformation("Awarded Former Champion to {Player}", player?.AccountName);
            }
        }

        _logger.LogInformation("Former Champion check complete: {Awarded} awarded, {Total} players qualified",
            awarded, qualifyingPlayers.Count);

        return awarded;
    }

    /// <summary>
    /// Check multi-encounter guild achievements that require session-level analysis.
    /// Per-encounter guild achievements are now handled by GuildChallengeChecker and GuildMilestoneChecker.
    /// </summary>
    private async Task<int> CheckMultiEncounterGuildAchievementsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Checking multi-encounter guild achievements");

        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedSet = includedAccounts.ToHashSet();

        var awarded = 0;

        // Check flawless wing achievements (0 deaths in entire wing in one session)
        awarded += await CheckFlawlessWingAchievementsAsync(includedSet, ct);

        // Check wing-based composition achievements (core_2_duo, chaos_dunk)
        awarded += await CheckWingCompositionAchievementsAsync(includedSet, ct);

        // Check expansion-themed achievements (thorn_in_my_side, ring_of_fire)
        awarded += await CheckExpansionAchievementsAsync(includedSet, ct);

        _logger.LogInformation("Multi-encounter guild achievements check complete: {Awarded} awarded", awarded);
        return awarded;
    }

    private async Task<int> CheckFlawlessWingAchievementsAsync(HashSet<string> includedSet, CancellationToken ct)
    {
        var awarded = 0;

        for (int wingNum = 1; wingNum <= 8; wingNum++)
        {
            var code = $"flawless_wing_{wingNum}";
            if (await HasGuildAchievementAsync(code, ct)) continue;

            var wingBosses = AchievementDefinitions.WingMasterBosses.GetValueOrDefault(wingNum);
            if (wingBosses == null || wingBosses.Length == 0) continue;

            // Get all sessions (dates) where the wing was cleared
            var encounters = await _db.Encounters
                .Where(e => e.Success && wingBosses.Contains(e.TriggerId))
                .OrderBy(e => e.EncounterTime)
                .ToListAsync(ct);

            // Group by date
            var byDate = encounters.GroupBy(e => e.EncounterTime.Date).ToList();

            foreach (var dateGroup in byDate)
            {
                var bossesCleared = dateGroup.Select(e => e.TriggerId).Distinct().ToList();
                if (!wingBosses.All(b => bossesCleared.Contains(b))) continue;

                // Check if 0 deaths across all wing encounters this session
                var totalDeaths = 0;
                foreach (var enc in dateGroup)
                {
                    var deaths = await _db.PlayerEncounters
                        .Where(pe => pe.EncounterId == enc.Id)
                        .SumAsync(pe => pe.Deaths, ct);
                    totalDeaths += deaths;
                }

                if (totalDeaths == 0)
                {
                    var firstEncounter = dateGroup.First();
                    // Build list of boss encounters for the context
                    var bossEncounters = dateGroup
                        .GroupBy(e => AchievementDefinitions.NormalizeTriggerId(e.TriggerId))
                        .Select(g => g.First()) // Take first kill of each boss
                        .Select(e => new
                        {
                            encounter_id = e.Id,
                            boss_name = AchievementDefinitions.BossNames.GetValueOrDefault(e.TriggerId, e.BossName)
                        })
                        .ToList();

                    var contextJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        encounter_id = firstEncounter.Id,
                        boss = $"Wing {wingNum} Flawless",
                        date = dateGroup.Key.ToString("yyyy-MM-dd"),
                        bosses = bossEncounters
                    });

                    if (await AwardGuildAchievementWithContextAsync(code, contextJson, firstEncounter.EncounterTime, ct))
                        awarded++;
                }
            }
        }

        return awarded;
    }

    private async Task<bool> HasGuildAchievementAsync(string code, CancellationToken ct)
    {
        return await _db.GuildAchievements.AnyAsync(ga => ga.AchievementCode == code, ct);
    }

    /// <summary>
    /// Award or increment a guild achievement
    /// </summary>
    /// <returns>True if this was the first time earning it (new achievement)</returns>
    private async Task<bool> AwardGuildAchievementAsync(string code, Guid encounterId, string bossName, DateTimeOffset achievedAt, CancellationToken ct)
    {
        var contextJson = System.Text.Json.JsonSerializer.Serialize(new { encounter_id = encounterId, boss = bossName });

        // Check if achievement already exists
        var existing = await _db.GuildAchievements
            .FirstOrDefaultAsync(ga => ga.AchievementCode == code, ct);

        if (existing != null)
        {
            // Increment count and update last achieved
            await _db.GuildAchievements
                .Where(ga => ga.Id == existing.Id)
                .Set(ga => ga.CompletionCount, existing.CompletionCount + 1)
                .Set(ga => ga.LastAchievedAt, achievedAt)
                .Set(ga => ga.LastContext, contextJson)
                .UpdateAsync(ct);

            _logger.LogDebug("Guild achievement {Code} completed again (count: {Count})", code, existing.CompletionCount + 1);
            return false;
        }
        else
        {
            // First time earning this achievement
            var achievement = new GuildAchievementEntity
            {
                Id = Guid.NewGuid(),
                AchievementCode = code,
                AchievedAt = achievedAt,
                Context = contextJson,
                CreatedAt = DateTimeOffset.UtcNow,
                CompletionCount = 1,
                LastAchievedAt = achievedAt,
                LastContext = contextJson
            };

            await _db.InsertAsync(achievement, token: ct);
            _logger.LogInformation("Awarded guild achievement {Code}", code);
            return true;
        }
    }

    /// <summary>
    /// Award or increment a guild achievement with pre-serialized context
    /// </summary>
    /// <returns>True if this was the first time earning it (new achievement)</returns>
    private async Task<bool> AwardGuildAchievementWithContextAsync(string code, string contextJson, DateTimeOffset achievedAt, CancellationToken ct)
    {
        // Check if achievement already exists
        var existing = await _db.GuildAchievements
            .FirstOrDefaultAsync(ga => ga.AchievementCode == code, ct);

        if (existing != null)
        {
            // Increment count and update last achieved
            await _db.GuildAchievements
                .Where(ga => ga.Id == existing.Id)
                .Set(ga => ga.CompletionCount, existing.CompletionCount + 1)
                .Set(ga => ga.LastAchievedAt, achievedAt)
                .Set(ga => ga.LastContext, contextJson)
                .UpdateAsync(ct);

            _logger.LogDebug("Guild achievement {Code} completed again (count: {Count})", code, existing.CompletionCount + 1);
            return false;
        }
        else
        {
            // First time earning this achievement
            var achievement = new GuildAchievementEntity
            {
                Id = Guid.NewGuid(),
                AchievementCode = code,
                AchievedAt = achievedAt,
                Context = contextJson,
                CreatedAt = DateTimeOffset.UtcNow,
                CompletionCount = 1,
                LastAchievedAt = achievedAt,
                LastContext = contextJson
            };

            await _db.InsertAsync(achievement, token: ct);
            _logger.LogInformation("Awarded guild achievement {Code}", code);
            return true;
        }
    }

    /// <summary>
    /// Check wing-based composition achievements (core_2_duo, chaos_dunk)
    /// </summary>
    private async Task<int> CheckWingCompositionAchievementsAsync(HashSet<string> includedSet, CancellationToken ct)
    {
        var awarded = 0;

        for (int wingNum = 1; wingNum <= 8; wingNum++)
        {
            var wingBosses = AchievementDefinitions.WingMasterBosses.GetValueOrDefault(wingNum);
            if (wingBosses == null || wingBosses.Length == 0) continue;

            // Get all successful encounters for this wing, grouped by date
            var encounters = await _db.Encounters
                .Where(e => e.Success && wingBosses.Contains(e.TriggerId))
                .OrderBy(e => e.EncounterTime)
                .ToListAsync(ct);

            var byDate = encounters.GroupBy(e => e.EncounterTime.Date).ToList();

            foreach (var dateGroup in byDate)
            {
                var bossesCleared = dateGroup.Select(e => e.TriggerId).Distinct().ToList();
                if (!wingBosses.All(b => bossesCleared.Contains(b))) continue;

                // Get one encounter per boss (first kill)
                var wingEncounters = dateGroup
                    .GroupBy(e => AchievementDefinitions.NormalizeTriggerId(e.TriggerId))
                    .Select(g => g.First())
                    .ToList();

                // Check Core 2 Duo - all players on core classes for entire wing
                var allCoreForWing = true;
                foreach (var enc in wingEncounters)
                {
                    var players = await _db.PlayerEncounters
                        .Where(pe => pe.EncounterId == enc.Id)
                        .Select(pe => pe.Profession)
                        .ToListAsync(ct);

                    if (!players.All(p => AchievementDefinitions.IsCoreProfession(p)))
                    {
                        allCoreForWing = false;
                        break;
                    }
                }

                if (allCoreForWing)
                {
                    var firstEnc = wingEncounters.First();
                    var bossEncounters = wingEncounters.Select(e => new
                    {
                        encounter_id = e.Id,
                        boss_name = AchievementDefinitions.BossNames.GetValueOrDefault(e.TriggerId, e.BossName)
                    }).ToList();

                    var contextJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        encounter_id = firstEnc.Id,
                        boss = $"Wing {wingNum} Core Only",
                        date = dateGroup.Key.ToString("yyyy-MM-dd"),
                        bosses = bossEncounters
                    });

                    if (await AwardGuildAchievementWithContextAsync("core_2_duo", contextJson, firstEnc.EncounterTime, ct))
                        awarded++;
                }

                // Check Chaos Dunk - all players in same subgroup for entire wing (7+ players each encounter)
                var allSameSubgroupForWing = true;
                int? consistentSubgroup = null;

                foreach (var enc in wingEncounters)
                {
                    var encounterPlayers = await _db.PlayerEncounters
                        .Where(pe => pe.EncounterId == enc.Id)
                        .Select(pe => new { pe.SquadGroup })
                        .ToListAsync(ct);

                    // Require at least 7 players
                    if (encounterPlayers.Count < 7)
                    {
                        allSameSubgroupForWing = false;
                        break;
                    }

                    var subgroups = encounterPlayers
                        .Select(p => p.SquadGroup)
                        .Where(g => g != null)
                        .Distinct()
                        .ToList();

                    if (subgroups.Count != 1)
                    {
                        allSameSubgroupForWing = false;
                        break;
                    }

                    if (consistentSubgroup == null)
                        consistentSubgroup = subgroups[0];
                    else if (consistentSubgroup != subgroups[0])
                    {
                        allSameSubgroupForWing = false;
                        break;
                    }
                }

                if (allSameSubgroupForWing && consistentSubgroup != null)
                {
                    var firstEnc = wingEncounters.First();
                    var bossEncounters = wingEncounters.Select(e => new
                    {
                        encounter_id = e.Id,
                        boss_name = AchievementDefinitions.BossNames.GetValueOrDefault(e.TriggerId, e.BossName)
                    }).ToList();

                    var contextJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        encounter_id = firstEnc.Id,
                        boss = $"Wing {wingNum} Chaos",
                        date = dateGroup.Key.ToString("yyyy-MM-dd"),
                        subgroup = consistentSubgroup,
                        bosses = bossEncounters
                    });

                    if (await AwardGuildAchievementWithContextAsync("chaos_dunk", contextJson, firstEnc.EncounterTime, ct))
                        awarded++;
                }
            }
        }

        return awarded;
    }

    /// <summary>
    /// Check expansion-themed achievements (thorn_in_my_side, ring_of_fire)
    /// </summary>
    private async Task<int> CheckExpansionAchievementsAsync(HashSet<string> includedSet, CancellationToken ct)
    {
        var awarded = 0;

        // Thorn in My Side - Complete Wings 1-4 on only HoT specs (single session)
        var hotWingBosses = new List<int>();
        for (int w = 1; w <= 4; w++)
        {
            var bosses = AchievementDefinitions.WingMasterBosses.GetValueOrDefault(w);
            if (bosses != null) hotWingBosses.AddRange(bosses);
        }

        var hotEncounters = await _db.Encounters
            .Where(e => e.Success && hotWingBosses.Contains(e.TriggerId))
            .OrderBy(e => e.EncounterTime)
            .ToListAsync(ct);

        var hotByDate = hotEncounters.GroupBy(e => e.EncounterTime.Date).ToList();

        foreach (var dateGroup in hotByDate)
        {
            var bossesCleared = dateGroup.Select(e => e.TriggerId).Distinct().ToList();
            if (!hotWingBosses.All(b => bossesCleared.Contains(b))) continue;

            // Get one encounter per boss
            var wingEncounters = dateGroup
                .GroupBy(e => AchievementDefinitions.NormalizeTriggerId(e.TriggerId))
                .Select(g => g.First())
                .ToList();

            // Check if all players used HoT specs
            var allHotForSession = true;
            foreach (var enc in wingEncounters)
            {
                var players = await _db.PlayerEncounters
                    .Where(pe => pe.EncounterId == enc.Id)
                    .Select(pe => pe.Profession)
                    .ToListAsync(ct);

                if (!players.All(p => AchievementDefinitions.IsHotSpec(p)))
                {
                    allHotForSession = false;
                    break;
                }
            }

            if (allHotForSession)
            {
                var firstEnc = wingEncounters.First();
                var contextJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    encounter_id = firstEnc.Id,
                    boss = "Wings 1-4 HoT Only",
                    date = dateGroup.Key.ToString("yyyy-MM-dd")
                });

                if (await AwardGuildAchievementWithContextAsync("thorn_in_my_side", contextJson, firstEnc.EncounterTime, ct))
                    awarded++;
            }
        }

        // Ring of Fire - Complete Wings 5-7 on only PoF specs (single session)
        var pofWingBosses = new List<int>();
        for (int w = 5; w <= 7; w++)
        {
            var bosses = AchievementDefinitions.WingMasterBosses.GetValueOrDefault(w);
            if (bosses != null) pofWingBosses.AddRange(bosses);
        }

        var pofEncounters = await _db.Encounters
            .Where(e => e.Success && pofWingBosses.Contains(e.TriggerId))
            .OrderBy(e => e.EncounterTime)
            .ToListAsync(ct);

        var pofByDate = pofEncounters.GroupBy(e => e.EncounterTime.Date).ToList();

        foreach (var dateGroup in pofByDate)
        {
            var bossesCleared = dateGroup.Select(e => e.TriggerId).Distinct().ToList();
            if (!pofWingBosses.All(b => bossesCleared.Contains(b))) continue;

            // Get one encounter per boss
            var wingEncounters = dateGroup
                .GroupBy(e => AchievementDefinitions.NormalizeTriggerId(e.TriggerId))
                .Select(g => g.First())
                .ToList();

            // Check if all players used PoF specs
            var allPofForSession = true;
            foreach (var enc in wingEncounters)
            {
                var players = await _db.PlayerEncounters
                    .Where(pe => pe.EncounterId == enc.Id)
                    .Select(pe => pe.Profession)
                    .ToListAsync(ct);

                if (!players.All(p => AchievementDefinitions.IsPofSpec(p)))
                {
                    allPofForSession = false;
                    break;
                }
            }

            if (allPofForSession)
            {
                var firstEnc = wingEncounters.First();
                var contextJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    encounter_id = firstEnc.Id,
                    boss = "Wings 5-7 PoF Only",
                    date = dateGroup.Key.ToString("yyyy-MM-dd")
                });

                if (await AwardGuildAchievementWithContextAsync("ring_of_fire", contextJson, firstEnc.EncounterTime, ct))
                    awarded++;
            }
        }

        return awarded;
    }
}

public record BackfillProgress(
    int Processed,
    int Total,
    int Awarded,
    int Errors)
{
    public double PercentComplete => Total > 0 ? (double)Processed / Total * 100 : 0;
}

public record BackfillResult(
    int EncountersProcessed,
    int AchievementsAwarded,
    List<string> Errors);
