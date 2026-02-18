using System.Text.Json;
using GW2RaidStats.Core.EliteInsights;
using GW2RaidStats.Infrastructure.Configuration;
using GW2RaidStats.Infrastructure.Database;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.Infrastructure.Services.Import;

public class RescanService
{
    private readonly RaidStatsDb _db;
    private readonly StorageOptions _storageOptions;
    private readonly ILogger<RescanService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RescanService(
        RaidStatsDb db,
        StorageOptions storageOptions,
        ILogger<RescanService> logger)
    {
        _db = db;
        _storageOptions = storageOptions;
        _logger = logger;
    }

    public async Task<RescanResult> RescanAllAsync(
        IProgress<RescanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var encounters = await _db.Encounters
            .Where(e => e.FilesPath != null)
            .Select(e => new { e.Id, e.FilesPath, e.BossName })
            .ToListAsync(ct);

        _logger.LogInformation("Found {Count} encounters with stored files to rescan", encounters.Count);
        _logger.LogInformation("Encounters path: {Path}", _storageOptions.EncountersPath);

        // Log first encounter path for debugging
        if (encounters.Count > 0)
        {
            var samplePath = Path.Combine(_storageOptions.EncountersPath, encounters[0].FilesPath!, "report.json");
            _logger.LogInformation("Sample path: {Path} (exists: {Exists})", samplePath, File.Exists(samplePath));
        }

        var total = encounters.Count;
        var processed = 0;
        var updated = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var encounter in encounters)
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            try
            {
                var jsonPath = Path.Combine(
                    _storageOptions.EncountersPath,
                    encounter.FilesPath!,
                    "report.json");

                if (!File.Exists(jsonPath))
                {
                    skipped++;
                    _logger.LogDebug("JSON not found for encounter {Id}: {Path}", encounter.Id, jsonPath);
                    progress?.Report(new RescanProgress(processed, total, updated, skipped, errors.Count));
                    continue;
                }

                var wasUpdated = await RescanEncounterAsync(encounter.Id, jsonPath, ct);
                if (wasUpdated)
                    updated++;
                else
                    skipped++;
            }
            catch (Exception ex)
            {
                errors.Add($"{encounter.BossName} ({encounter.Id}): {ex.Message}");
                _logger.LogWarning(ex, "Error rescanning encounter {Id}", encounter.Id);
            }

            progress?.Report(new RescanProgress(processed, total, updated, skipped, errors.Count));
        }

        _logger.LogInformation(
            "Rescan complete: {Processed} processed, {Updated} updated, {Skipped} skipped, {Errors} errors",
            processed, updated, skipped, errors.Count);

        return new RescanResult(processed, updated, skipped, errors, _storageOptions.EncountersPath);
    }

    private async Task<bool> RescanEncounterAsync(Guid encounterId, string jsonPath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(jsonPath);
        var log = await JsonSerializer.DeserializeAsync<EliteInsightsLog>(stream, JsonOptions, ct);

        if (log == null)
            return false;

        var anyUpdated = false;

        // Update encounter-level fields
        anyUpdated |= await UpdateEncounterFieldsAsync(encounterId, log, ct);

        // Update player encounter fields
        anyUpdated |= await UpdatePlayerEncounterFieldsAsync(encounterId, log, ct);

        return anyUpdated;
    }

    private async Task<bool> UpdateEncounterFieldsAsync(Guid encounterId, EliteInsightsLog log, CancellationToken ct)
    {
        // Extract progression data
        var (furthestPhase, furthestPhaseIndex, bossHpRemaining) = ExtractProgressionData(log);

        // Check if we need to update
        var encounter = await _db.Encounters
            .Where(e => e.Id == encounterId)
            .Select(e => new { e.FurthestPhase, e.BossHealthPercentRemaining })
            .FirstOrDefaultAsync(ct);

        if (encounter == null)
            return false;

        // Always update if we have calculated values (allows fixing incorrect values)
        var hasProgressionData = furthestPhase != null || bossHpRemaining.HasValue;

        if (!hasProgressionData)
            return false;

        await _db.Encounters
            .Where(e => e.Id == encounterId)
            .Set(e => e.FurthestPhase, furthestPhase ?? encounter.FurthestPhase)
            .Set(e => e.FurthestPhaseIndex, furthestPhaseIndex)
            .Set(e => e.BossHealthPercentRemaining, bossHpRemaining ?? encounter.BossHealthPercentRemaining)
            .UpdateAsync(ct);

        return true;
    }

    private async Task<bool> UpdatePlayerEncounterFieldsAsync(Guid encounterId, EliteInsightsLog log, CancellationToken ct)
    {
        var anyUpdated = false;

        foreach (var eiPlayer in log.Players)
        {
            // Find matching player_encounter by character name
            var playerEncounter = await _db.PlayerEncounters
                .Where(pe => pe.EncounterId == encounterId && pe.CharacterName == eiPlayer.Name)
                .Select(pe => new { pe.Id, pe.HealingPowerStat, pe.QuicknessGeneration, pe.AlacracityGeneration, pe.Role, pe.ResurrectTime })
                .FirstOrDefaultAsync(ct);

            if (playerEncounter == null)
                continue;

            // Check if healing power stat needs updating (0 means not set)
            if (playerEncounter.HealingPowerStat == 0 && eiPlayer.HealingPower > 0)
            {
                await _db.PlayerEncounters
                    .Where(pe => pe.Id == playerEncounter.Id)
                    .Set(pe => pe.HealingPowerStat, eiPlayer.HealingPower)
                    .UpdateAsync(ct);

                anyUpdated = true;
            }

            // Check if role needs updating (null means not set)
            if (playerEncounter.Role == null)
            {
                var healingPower = playerEncounter.HealingPowerStat > 0 ? playerEncounter.HealingPowerStat : eiPlayer.HealingPower;
                var role = LogImportService.CalculateRole(
                    healingPower,
                    playerEncounter.QuicknessGeneration,
                    playerEncounter.AlacracityGeneration);

                await _db.PlayerEncounters
                    .Where(pe => pe.Id == playerEncounter.Id)
                    .Set(pe => pe.Role, role)
                    .UpdateAsync(ct);

                anyUpdated = true;
            }

            // Check if resurrect time needs updating (0 means not set)
            var support = eiPlayer.Support?.FirstOrDefault();
            if (playerEncounter.ResurrectTime == 0 && support?.ResurrectTime > 0)
            {
                await _db.PlayerEncounters
                    .Where(pe => pe.Id == playerEncounter.Id)
                    .Set(pe => pe.ResurrectTime, support.ResurrectTime)
                    .UpdateAsync(ct);

                anyUpdated = true;
            }
        }

        return anyUpdated;
    }

    private static (string? FurthestPhase, int? PhaseIndex, decimal? BossHpRemaining) ExtractProgressionData(EliteInsightsLog log)
    {
        string? furthestPhase = null;
        int? furthestPhaseIndex = null;
        decimal? bossHpRemaining = null;

        // Extract furthest phase from phases array
        if (log.Phases is { Count: > 0 })
        {
            // The last phase in the array is the furthest reached
            // Skip phase 0 which is usually "Full Fight"
            var combatPhases = log.Phases.Skip(1).ToList();
            if (combatPhases.Count > 0)
            {
                var lastPhase = combatPhases.Last();
                furthestPhase = lastPhase.Name;
                furthestPhaseIndex = combatPhases.Count; // 1-indexed
            }
        }

        // Extract boss HP remaining from targets
        if (log.Targets is { Count: > 0 })
        {
            // Check if this is HTCM (has dragon "Void" targets)
            var dragonTargets = log.Targets.Where(t => t.Name != null && t.Name.Contains("Void")).ToList();
            var isHtcm = dragonTargets.Count >= 2; // HTCM has multiple Void dragons

            if (isHtcm)
            {
                // For HTCM: Calculate progress relative to ALL 6 dragons
                // Get dragon HP from the first reached dragon (they all have the same HP)
                var reachedDragon = dragonTargets.FirstOrDefault(t => t.TotalHealth > 0);
                if (reachedDragon != null)
                {
                    var dragonHp = (decimal)reachedDragon.TotalHealth;
                    var totalFightHp = dragonHp * 6; // 6 dragons total

                    // Sum HP burned from all reached dragons
                    var totalHpRemoved = dragonTargets
                        .Where(t => t.TotalHealth > 0)
                        .Sum(t => (decimal)t.TotalHealth * (t.HealthPercentBurned / 100m));

                    var clearPercentage = totalFightHp > 0 ? (totalHpRemoved / totalFightHp) * 100 : 0;
                    bossHpRemaining = Math.Max(0, 100 - clearPercentage);
                }
            }
            else
            {
                // For regular bosses: use weighted calculation on valid targets
                var validTargets = log.Targets.Where(t => t.TotalHealth > 0).ToList();

                if (validTargets.Count > 0)
                {
                    var totalActualHealth = validTargets.Sum(t => (decimal)t.TotalHealth);
                    var totalHpRemoved = validTargets.Sum(t => (decimal)t.TotalHealth * (t.HealthPercentBurned / 100m));

                    var clearPercentage = totalActualHealth > 0 ? (totalHpRemoved / totalActualHealth) * 100 : 0;
                    bossHpRemaining = Math.Max(0, 100 - clearPercentage);
                }
            }
        }

        return (furthestPhase, furthestPhaseIndex, bossHpRemaining);
    }
}

public record RescanProgress(
    int Processed,
    int Total,
    int Updated,
    int Skipped,
    int Errors)
{
    public double PercentComplete => Total > 0 ? (double)Processed / Total * 100 : 0;
}

public record RescanResult(
    int Processed,
    int Updated,
    int Skipped,
    List<string> Errors,
    string? EncountersPath = null);
