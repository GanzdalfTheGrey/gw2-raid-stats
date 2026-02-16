using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Core;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Services.Achievements.Checkers;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.Infrastructure.Services.Achievements;

/// <summary>
/// Thin orchestrator for achievement checking.
/// Coordinates between checkers, award service, and query service.
/// </summary>
public class AchievementOrchestrator
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;
    private readonly AchievementAwardService _awardService;
    private readonly IEnumerable<IAchievementChecker> _checkers;
    private readonly ILogger<AchievementOrchestrator> _logger;

    public AchievementOrchestrator(
        RaidStatsDb db,
        IncludedPlayerService includedPlayerService,
        AchievementAwardService awardService,
        IEnumerable<IAchievementChecker> checkers,
        ILogger<AchievementOrchestrator> logger)
    {
        _db = db;
        _includedPlayerService = includedPlayerService;
        _awardService = awardService;
        _checkers = checkers;
        _logger = logger;
    }

    /// <summary>
    /// Check achievements after an encounter is imported (incremental check)
    /// </summary>
    public async Task CheckAfterEncounterAsync(
        Guid encounterId,
        bool notify = true,
        CancellationToken ct = default)
    {
        var encounter = await _db.Encounters
            .FirstOrDefaultAsync(e => e.Id == encounterId, ct);

        if (encounter == null) return;

        // Skip ignored encounters (Spirit Race, Statues, etc.)
        if (WingMapping.IsIgnoredEncounter(encounter.BossName))
        {
            return;
        }

        // Build the context for checkers
        var context = await BuildContextAsync(encounterId, notify, ct);
        if (context == null) return;

        // Run all checkers and collect unlocks
        var allUnlocks = new List<AchievementUnlock>();
        foreach (var checker in _checkers)
        {
            try
            {
                var unlocks = await checker.CheckAsync(context, ct);
                allUnlocks.AddRange(unlocks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running checker {CheckerType} for encounter {EncounterId}",
                    checker.GetType().Name, encounterId);
            }
        }

        // Process all unlocks
        if (allUnlocks.Count > 0)
        {
            var awarded = await _awardService.ProcessUnlocksAsync(allUnlocks, notify, ct);
            _logger.LogInformation("Awarded {Count} achievements for encounter {EncounterId}", awarded, encounterId);
        }
    }

    /// <summary>
    /// Full achievement check for a single player (for retroactive scan).
    /// Returns number of new achievements awarded.
    /// </summary>
    public async Task<int> CheckAllForPlayerAsync(
        Guid playerId,
        bool notify = false,
        CancellationToken ct = default)
    {
        var startCount = await _db.PlayerAchievements
            .Where(pa => pa.PlayerId == playerId)
            .CountAsync(ct);

        // Get all encounters this player participated in
        var encounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId)
            .Where(x => x.e.Success) // Only check successful kills for most achievements
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => x.e.Id)
            .ToListAsync(ct);

        // Check each encounter
        foreach (var encounterId in encounters)
        {
            await CheckAfterEncounterAsync(encounterId, notify, ct);
        }

        var endCount = await _db.PlayerAchievements
            .Where(pa => pa.PlayerId == playerId)
            .CountAsync(ct);

        return endCount - startCount;
    }

    /// <summary>
    /// Check flawless wing achievements for today's session.
    /// Called when posting session summary.
    /// </summary>
    public async Task<int> CheckFlawlessWingsForTodayAsync(
        bool notify = true,
        CancellationToken ct = default)
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
                var bossEncounters = encounters
                    .GroupBy(e => AchievementDefinitions.NormalizeTriggerId(e.TriggerId))
                    .Select(g => g.First())
                    .Select(e => new
                    {
                        encounter_id = e.Id,
                        boss_name = AchievementDefinitions.BossNames.GetValueOrDefault(e.TriggerId, e.BossName)
                    })
                    .ToList();

                await _awardService.AwardGuildAchievementAsync(code, new
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

    #region Context Building

    private async Task<AchievementCheckContext?> BuildContextAsync(
        Guid encounterId,
        bool notify,
        CancellationToken ct)
    {
        var encounter = await _db.Encounters
            .FirstOrDefaultAsync(e => e.Id == encounterId, ct);

        if (encounter == null) return null;

        // Get all player encounters for this encounter
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => x.pe.EncounterId == encounterId)
            .ToListAsync(ct);

        // Get included accounts (guild members)
        var includedAccounts = (await _includedPlayerService.GetIncludedAccountNamesAsync(ct)).ToHashSet();

        // Build player data list
        var players = playerEncounters
            .Select(x => new PlayerEncounterData(x.pe, x.p))
            .ToList();

        return new AchievementCheckContext
        {
            Encounter = encounter,
            Players = players,
            IncludedAccounts = includedAccounts,
            Notify = notify
        };
    }

    #endregion
}
