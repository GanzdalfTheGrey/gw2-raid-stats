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
    private readonly AchievementService _achievementService;
    private readonly IncludedPlayerService _includedPlayerService;
    private readonly ILogger<AchievementBackfillService> _logger;

    public AchievementBackfillService(
        RaidStatsDb db,
        AchievementService achievementService,
        IncludedPlayerService includedPlayerService,
        ILogger<AchievementBackfillService> logger)
    {
        _db = db;
        _achievementService = achievementService;
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

        var players = await _db.Players
            .Where(p => includedSet.Contains(p.AccountName))
            .OrderBy(p => p.AccountName)
            .ToListAsync(ct);

        _logger.LogInformation("Found {Count} guild members to process", players.Count);

        var totalAwarded = 0;
        var processed = 0;
        var errors = new List<string>();

        foreach (var player in players)
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            try
            {
                var awarded = await _achievementService.CheckAllForPlayerAsync(player.Id, notify: false, ct);
                totalAwarded += awarded;

                if (awarded > 0)
                {
                    _logger.LogInformation("Awarded {Count} achievements to {Player}", awarded, player.AccountName);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{player.AccountName}: {ex.Message}");
                _logger.LogWarning(ex, "Error processing achievements for {Player}", player.AccountName);
            }

            progress?.Report(new BackfillProgress(processed, players.Count, totalAwarded, errors.Count));
        }

        // Check Former Champion achievement (requires historical DPS record analysis)
        var formerChampionAwarded = await CheckFormerChampionAchievementAsync(ct);
        totalAwarded += formerChampionAwarded;

        // Check guild achievements retroactively
        var guildAwarded = await CheckAllGuildAchievementsAsync(ct);
        totalAwarded += guildAwarded;

        _logger.LogInformation(
            "Backfill complete: {Processed} players, {Awarded} achievements awarded, {Errors} errors",
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
    /// Check all guild achievements retroactively
    /// </summary>
    private async Task<int> CheckAllGuildAchievementsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Checking guild achievements retroactively");

        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedSet = includedAccounts.ToHashSet();

        var awarded = 0;

        // Get all successful encounters
        var encounters = await _db.Encounters
            .Where(e => e.Success)
            .OrderBy(e => e.EncounterTime)
            .ToListAsync(ct);

        foreach (var encounter in encounters)
        {
            ct.ThrowIfCancellationRequested();

            // Skip ignored encounters (Spirit Race, Statues, etc.)
            if (WingMapping.IsIgnoredEncounter(encounter.BossName))
                continue;

            // Get players for this encounter
            var players = await _db.PlayerEncounters
                .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
                .Where(x => x.pe.EncounterId == encounter.Id)
                .ToListAsync(ct);

            // Check composition achievements
            var professions = players.Select(x => x.pe.Profession).ToList();
            var baseProfessions = professions.Select(AchievementDefinitions.GetBaseProfession).ToList();
            var guildMemberCount = players.Count(x => includedSet.Contains(x.p.AccountName));

            // Need enough guild members
            if (guildMemberCount < 5) continue;

            // One Trick Guild
            if (baseProfessions.Distinct().Count() == 1 && players.Count >= 10)
            {
                if (await AwardGuildAchievementAsync("one_trick_guild", encounter.Id, encounter.BossName, encounter.EncounterTime, ct))
                    awarded++;
            }

            // Heavy Metal
            var heavySpecs = AchievementDefinitions.ArmorClasses["Heavy"];
            if (professions.All(p => heavySpecs.Contains(p)))
            {
                if (await AwardGuildAchievementAsync("heavy_metal", encounter.Id, encounter.BossName, encounter.EncounterTime, ct))
                    awarded++;
            }

            // Cloth Squad
            var lightSpecs = AchievementDefinitions.ArmorClasses["Light"];
            if (professions.All(p => lightSpecs.Contains(p)))
            {
                if (await AwardGuildAchievementAsync("cloth_squad", encounter.Id, encounter.BossName, encounter.EncounterTime, ct))
                    awarded++;
            }

            // Leather Lovers
            var mediumSpecs = AchievementDefinitions.ArmorClasses["Medium"];
            if (professions.All(p => mediumSpecs.Contains(p)))
            {
                if (await AwardGuildAchievementAsync("leather_lovers", encounter.Id, encounter.BossName, encounter.EncounterTime, ct))
                    awarded++;
            }

            // No Duplicates (10 different elite specs in one encounter)
            var uniqueSpecs = professions.Where(p => AchievementDefinitions.AllEliteSpecs.Contains(p)).Distinct().Count();
            if (uniqueSpecs >= 10)
            {
                if (await AwardGuildAchievementAsync("no_duplicates", encounter.Id, encounter.BossName, encounter.EncounterTime, ct))
                    awarded++;
            }

            // Rainbow Squad (all 9 professions in one encounter)
            // Must have all 9 base professions represented
            var allBaseProfessions = AchievementDefinitions.EliteSpecsByProfession.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var presentBaseProfessions = baseProfessions
                .Where(p => allBaseProfessions.Contains(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (presentBaseProfessions >= 9)
            {
                if (await AwardGuildAchievementAsync("rainbow_squad", encounter.Id, encounter.BossName, encounter.EncounterTime, ct))
                    awarded++;
            }

            // Bench Warmers (7 or fewer players)
            if (players.Count <= 7)
            {
                if (await AwardGuildAchievementAsync("bench_warmers", encounter.Id, encounter.BossName, encounter.EncounterTime, ct))
                    awarded++;
            }

            // Untouchable (0 downs)
            var totalDowns = players.Sum(x => x.pe.Downs);
            if (totalDowns == 0)
            {
                if (await AwardGuildAchievementAsync("untouchable", encounter.Id, encounter.BossName, encounter.EncounterTime, ct))
                    awarded++;
            }

            // The Comeback - check if we killed this boss after wiping 5+ times on it this session
            await CheckTheComebackAsync(encounter, includedSet, ct);

            // Record Breakers - DPS and boon DPS records in the same encounter
            await CheckRecordBreakersAsync(encounter, includedSet, ct);
        }

        // Check flawless wing achievements (0 deaths in entire wing in one session)
        awarded += await CheckFlawlessWingAchievementsAsync(includedSet, ct);

        _logger.LogInformation("Guild achievements check complete: {Awarded} awarded", awarded);
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

    /// <summary>
    /// Check "The Comeback" achievement - kill a boss after wiping 5+ times on it in the same session
    /// </summary>
    private async Task CheckTheComebackAsync(EncounterEntity successfulEncounter, HashSet<string> includedSet, CancellationToken ct)
    {
        if (!successfulEncounter.Success) return;

        // Get all encounters for this boss on this day
        var sessionDate = successfulEncounter.EncounterTime.Date;
        var sessionStart = new DateTimeOffset(sessionDate, successfulEncounter.EncounterTime.Offset);
        var sessionEnd = sessionStart.AddDays(1);

        var bossEncountersToday = await _db.Encounters
            .Where(e => e.TriggerId == successfulEncounter.TriggerId)
            .Where(e => e.IsCM == successfulEncounter.IsCM)
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime < sessionEnd)
            .Where(e => e.EncounterTime <= successfulEncounter.EncounterTime)
            .OrderBy(e => e.EncounterTime)
            .ToListAsync(ct);

        // Count wipes before this kill
        var wipesBeforeKill = bossEncountersToday
            .TakeWhile(e => e.Id != successfulEncounter.Id)
            .Count(e => !e.Success);

        if (wipesBeforeKill >= 5)
        {
            await AwardGuildAchievementAsync("the_comeback", successfulEncounter.Id, successfulEncounter.BossName, successfulEncounter.EncounterTime, ct);
        }
    }

    // Threshold for boon support
    private const decimal BoonSupportThreshold = 10m;

    /// <summary>
    /// Check "Record Breakers" achievement - break DPS and boon DPS records in the same encounter
    /// </summary>
    private async Task CheckRecordBreakersAsync(
        EncounterEntity encounter,
        HashSet<string> includedSet,
        CancellationToken ct)
    {
        // Get all guild member performances in this encounter
        var encounterPlayers = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => x.pe.EncounterId == encounter.Id)
            .Where(x => includedSet.Contains(x.p.AccountName))
            .ToListAsync(ct);

        if (encounterPlayers.Count == 0) return;

        // Get the top DPS from guild members in this encounter
        var topDps = encounterPlayers
            .OrderByDescending(x => x.pe.Dps)
            .First();

        // Get the previous DPS record (before this encounter)
        var previousDpsRecord = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == encounter.TriggerId && x.e.IsCM == encounter.IsCM && x.e.Success)
            .Where(x => x.e.EncounterTime < encounter.EncounterTime)
            .Where(x => includedSet.Contains(x.p.AccountName))
            .MaxAsync(x => (int?)x.pe.Dps, ct) ?? 0;

        var dpsRecordBroken = topDps.pe.Dps > previousDpsRecord;

        // Get boon support players (quickness or alac >= 10%)
        var boonPlayers = encounterPlayers
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                       (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .OrderByDescending(x => x.pe.Dps)
            .ToList();

        if (boonPlayers.Count == 0) return;

        var topBoonDps = boonPlayers.First();

        // Get the previous boon DPS record
        var previousBoonDpsRecord = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == encounter.TriggerId && x.e.IsCM == encounter.IsCM && x.e.Success)
            .Where(x => x.e.EncounterTime < encounter.EncounterTime)
            .Where(x => includedSet.Contains(x.p.AccountName))
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                       (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .MaxAsync(x => (int?)x.pe.Dps, ct) ?? 0;

        var boonDpsRecordBroken = topBoonDps.pe.Dps > previousBoonDpsRecord;

        // Award if both records broken
        if (dpsRecordBroken && boonDpsRecordBroken)
        {
            await AwardGuildAchievementAsync("record_breakers", encounter.Id, encounter.BossName, encounter.EncounterTime, ct);
        }
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
    int PlayersProcessed,
    int AchievementsAwarded,
    List<string> Errors);
