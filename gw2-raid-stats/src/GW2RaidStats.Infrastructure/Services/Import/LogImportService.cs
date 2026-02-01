using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Core;
using GW2RaidStats.Core.EliteInsights;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services.Import;

public class LogImportService
{
    private readonly RaidStatsDb _db;
    private readonly RecordNotificationService _recordNotificationService;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LogImportService(RaidStatsDb db, RecordNotificationService recordNotificationService)
    {
        _db = db;
        _recordNotificationService = recordNotificationService;
    }

    public async Task<ImportResult> ImportLogAsync(Stream jsonStream, string fileName, CancellationToken ct = default)
    {
        try
        {
            // Read the entire stream for hashing and parsing
            using var ms = new MemoryStream();
            await jsonStream.CopyToAsync(ms, ct);
            var jsonBytes = ms.ToArray();

            // Compute hash for deduplication
            var hash = ComputeHash(jsonBytes);

            // Parse JSON first (needed for both new and duplicate processing)
            var log = JsonSerializer.Deserialize<EliteInsightsLog>(jsonBytes, JsonOptions);

            // Check for duplicate
            var existingEncounter = await _db.Encounters
                .FirstOrDefaultAsync(e => e.JsonHash == hash, ct);

            if (existingEncounter != null)
            {
                // Update existing encounter with progression data if missing
                if (log != null && existingEncounter.FurthestPhase == null)
                {
                    var (furthestPhase, furthestPhaseIndex, bossHpRemaining) = ExtractProgressionData(log);
                    if (furthestPhase != null || bossHpRemaining.HasValue)
                    {
                        await _db.Encounters
                            .Where(e => e.Id == existingEncounter.Id)
                            .Set(e => e.FurthestPhase, furthestPhase)
                            .Set(e => e.FurthestPhaseIndex, furthestPhaseIndex)
                            .Set(e => e.BossHealthPercentRemaining, bossHpRemaining)
                            .UpdateAsync(ct);
                    }
                }
                return new ImportResult(true, existingEncounter.Id, fileName, existingEncounter.BossName, null, WasDuplicate: true);
            }
            if (log == null)
            {
                return new ImportResult(false, null, fileName, null, "Failed to parse JSON", WasDuplicate: false);
            }

            // Skip "late start" encounters - these are incomplete recordings
            if (log.FightName.Contains("Late start", StringComparison.OrdinalIgnoreCase))
            {
                return new ImportResult(false, null, fileName, log.FightName, "Skipped: Late start encounter", WasDuplicate: false);
            }

            // Skip ignored encounters (non-boss events like Spirit Race, Twisted Castle)
            if (WingMapping.IsIgnoredEncounter(log.FightName))
            {
                return new ImportResult(false, null, fileName, log.FightName, "Skipped: Non-boss encounter", WasDuplicate: false);
            }

            // Import the log
            var encounterId = await ImportLogDataAsync(log, hash, ct);

            // Check for broken records and queue notifications (only for successful kills)
            if (log.Success)
            {
                await _recordNotificationService.CheckAndQueueRecordNotificationsAsync(encounterId, ct);
            }

            return new ImportResult(true, encounterId, fileName, log.FightName, null, WasDuplicate: false);
        }
        catch (Exception ex)
        {
            return new ImportResult(false, null, fileName, null, ex.Message, WasDuplicate: false);
        }
    }

    public async Task<ImportResult> ImportLogFromFileAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        return await ImportLogAsync(stream, Path.GetFileName(filePath), ct);
    }

    private async Task<Guid> ImportLogDataAsync(EliteInsightsLog log, string hash, CancellationToken ct)
    {
        // Parse encounter time from multiple possible sources
        var encounterTime = ParseEncounterTime(log);

        // Determine wing from trigger ID
        var wing = WingMapping.GetWing(log.TriggerId);

        // Get log URL if available
        var logUrl = log.UploadLinks?.FirstOrDefault();

        // Extract progression data (phases and boss HP)
        var (furthestPhase, furthestPhaseIndex, bossHpRemaining) = ExtractProgressionData(log);

        // Create encounter
        var encounter = new EncounterEntity
        {
            Id = Guid.NewGuid(),
            TriggerId = log.TriggerId,
            BossName = log.FightName,
            Wing = wing,
            IsCM = log.IsCM,
            IsLegendaryCM = log.IsLegendaryCM ?? false,
            Success = log.Success,
            DurationMs = log.DurationMs,
            EncounterTime = encounterTime,
            RecordedBy = log.RecordedAccountBy ?? log.RecordedBy,
            LogUrl = logUrl,
            JsonHash = hash,
            IconUrl = log.FightIcon,
            FurthestPhase = furthestPhase,
            FurthestPhaseIndex = furthestPhaseIndex,
            BossHealthPercentRemaining = bossHpRemaining,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(encounter, token: ct);

        // Process players
        foreach (var eiPlayer in log.Players)
        {
            // Get or create player
            var player = await GetOrCreatePlayerAsync(eiPlayer.Account, encounterTime, ct);

            // For multi-target fights (like Twin Largos), use dpsAll (combined DPS on all targets)
            // For single-target fights, use dpsTargets[0] (boss-only DPS, excludes adds)
            var isMultiTarget = WingMapping.IsMultiTargetEncounter(log.TriggerId);
            var dps = isMultiTarget
                ? eiPlayer.DpsAll?.FirstOrDefault()
                : (eiPlayer.DpsTargets?.FirstOrDefault()?.FirstOrDefault() ?? eiPlayer.DpsAll?.FirstOrDefault());
            var defense = eiPlayer.Defenses?.FirstOrDefault();
            var support = eiPlayer.Support?.FirstOrDefault();

            // Get boon generation stats
            var (quicknessGen, alacrityGen) = GetBoonGeneration(eiPlayer);

            // Get healing stats (parse from dynamic JSON structure)
            var (healingTotal, healingPower, hps) = GetHealingStats(eiPlayer);

            var playerEncounter = new PlayerEncounterEntity
            {
                Id = Guid.NewGuid(),
                PlayerId = player.Id,
                EncounterId = encounter.Id,
                CharacterName = eiPlayer.Name,
                Profession = eiPlayer.Profession,
                SquadGroup = eiPlayer.Group,

                // DPS stats
                Dps = dps?.Dps ?? 0,
                Damage = dps?.Damage ?? 0,
                PowerDps = dps?.PowerDps,
                CondiDps = dps?.CondiDps,
                BreakbarDamage = dps?.BreakbarDamage,

                // Defense stats
                Deaths = defense?.DeadCount ?? 0,
                DeathDurationMs = (int)((defense?.DeadDuration ?? 0) * 1000),
                Downs = defense?.DownCount ?? 0,
                DownDurationMs = (int)((defense?.DownDuration ?? 0) * 1000),
                DamageTaken = defense?.DamageTaken ?? 0,

                // Support stats
                Resurrects = support?.Resurrects ?? 0,
                CondiCleanse = support?.CondiCleanse ?? 0,
                BoonStrips = support?.BoonStrips ?? 0,

                // Boon generation
                QuicknessGeneration = quicknessGen,
                AlacracityGeneration = alacrityGen,

                // Healing stats (from extension)
                Healing = healingTotal,
                HealingPowerHealing = healingPower,
                Hps = hps,

                // Character attribute - Healing Power stat (always available)
                HealingPowerStat = eiPlayer.HealingPower,

                CreatedAt = DateTimeOffset.UtcNow
            };

            await _db.InsertAsync(playerEncounter, token: ct);
        }

        // Process mechanics
        if (log.Mechanics != null)
        {
            foreach (var mechanic in log.Mechanics)
            {
                if (mechanic.MechanicsData == null) continue;

                foreach (var data in mechanic.MechanicsData)
                {
                    // Try to find the player by character name
                    Guid? playerId = null;
                    if (!string.IsNullOrEmpty(data.Actor))
                    {
                        var player = log.Players.FirstOrDefault(p => p.Name == data.Actor);
                        if (player != null)
                        {
                            var dbPlayer = await _db.Players
                                .FirstOrDefaultAsync(p => p.AccountName == player.Account, ct);
                            playerId = dbPlayer?.Id;
                        }
                    }

                    var mechanicEvent = new MechanicEventEntity
                    {
                        Id = Guid.NewGuid(),
                        EncounterId = encounter.Id,
                        PlayerId = playerId,
                        MechanicName = mechanic.Name,
                        MechanicFullName = mechanic.FullName,
                        Description = mechanic.Description,
                        EventTimeMs = data.Time,
                        CreatedAt = DateTimeOffset.UtcNow
                    };

                    await _db.InsertAsync(mechanicEvent, token: ct);
                }
            }
        }

        // Process phase stats (squad DPS per phase)
        if (log.Phases != null && log.Phases.Count > 0)
        {
            for (int phaseIndex = 0; phaseIndex < log.Phases.Count; phaseIndex++)
            {
                var phase = log.Phases[phaseIndex];

                // Skip phases with no duration
                if (phase.End <= phase.Start) continue;

                // Calculate squad DPS for this phase by summing all players' DPS
                var squadDps = 0;
                foreach (var player in log.Players)
                {
                    if (player.DpsAll != null && phaseIndex < player.DpsAll.Count)
                    {
                        squadDps += player.DpsAll[phaseIndex].Dps;
                    }
                }

                var phaseStat = new EncounterPhaseStatEntity
                {
                    Id = Guid.NewGuid(),
                    EncounterId = encounter.Id,
                    PhaseIndex = phaseIndex,
                    PhaseName = phase.Name,
                    SquadDps = squadDps,
                    DurationMs = phase.End - phase.Start,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                await _db.InsertAsync(phaseStat, token: ct);
            }
        }

        return encounter.Id;
    }

    private async Task<PlayerEntity> GetOrCreatePlayerAsync(string accountName, DateTimeOffset encounterTime, CancellationToken ct)
    {
        var player = await _db.Players
            .FirstOrDefaultAsync(p => p.AccountName == accountName, ct);

        if (player != null)
        {
            // Update first_seen if this encounter is earlier
            if (encounterTime < player.FirstSeen)
            {
                await _db.Players
                    .Where(p => p.Id == player.Id)
                    .Set(p => p.FirstSeen, encounterTime)
                    .UpdateAsync(ct);
                player.FirstSeen = encounterTime;
            }
            return player;
        }

        // Create new player
        player = new PlayerEntity
        {
            Id = Guid.NewGuid(),
            AccountName = accountName,
            FirstSeen = encounterTime,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(player, token: ct);
        return player;
    }

    private static string ComputeHash(byte[] data)
    {
        var hashBytes = SHA256.HashData(data);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static (decimal? quickness, decimal? alacrity) GetBoonGeneration(EIPlayer player)
    {
        decimal? quicknessGen = null;
        decimal? alacrityGen = null;

        // Check squadBuffs first (full squad generation), then groupBuffs (subgroup only)
        var buffs = player.SquadBuffs ?? player.GroupBuffs;
        if (buffs == null) return (null, null);

        foreach (var buff in buffs)
        {
            // Get the first phase data (total/all phases)
            var generation = buff.BuffData?.FirstOrDefault()?.Generation ?? 0;

            if (buff.Id == GW2BuffIds.Quickness && generation > 0)
            {
                quicknessGen = generation;
            }
            else if (buff.Id == GW2BuffIds.Alacrity && generation > 0)
            {
                alacrityGen = generation;
            }
        }

        return (quicknessGen, alacrityGen);
    }

    private static (int healing, int healingPower, int hps) GetHealingStats(EIPlayer player)
    {
        try
        {
            if (player.ExtHealingStats == null || player.ExtHealingStats.Value.ValueKind != System.Text.Json.JsonValueKind.Array)
                return (0, 0, 0);

            var statsArray = player.ExtHealingStats.Value;
            if (statsArray.GetArrayLength() == 0)
                return (0, 0, 0);

            // Get first phase stats
            var firstPhase = statsArray[0];

            // Try to get outgoingHealing array
            if (!firstPhase.TryGetProperty("outgoingHealing", out var outgoingHealing))
                return (0, 0, 0);

            if (outgoingHealing.ValueKind != System.Text.Json.JsonValueKind.Array || outgoingHealing.GetArrayLength() == 0)
                return (0, 0, 0);

            // Get first target's healing (usually "all" or total)
            var firstTarget = outgoingHealing[0];

            int healing = 0;
            int healingPower = 0;
            int hps = 0;

            if (firstTarget.TryGetProperty("healing", out var healingProp))
                healing = healingProp.GetInt32();
            if (firstTarget.TryGetProperty("healingPowerHealing", out var hpHealingProp))
                healingPower = hpHealingProp.GetInt32();
            if (firstTarget.TryGetProperty("hps", out var hpsProp))
                hps = hpsProp.GetInt32();

            return (healing, healingPower, hps);
        }
        catch
        {
            // If parsing fails for any reason, return zeros
            return (0, 0, 0);
        }
    }

    private static (string? furthestPhase, int? furthestPhaseIndex, decimal? bossHpRemaining) ExtractProgressionData(EliteInsightsLog log)
    {
        string? furthestPhase = null;
        int? furthestPhaseIndex = null;
        decimal? bossHpRemaining = null;

        // Extract furthest phase from phases array
        if (log.Phases != null && log.Phases.Count > 0)
        {
            // Find the last phase that was actually reached (has duration > 0 or end > start)
            for (int i = log.Phases.Count - 1; i >= 0; i--)
            {
                var phase = log.Phases[i];
                // A phase was reached if it has some duration (end > start)
                if (phase.End > phase.Start)
                {
                    furthestPhase = phase.Name;
                    furthestPhaseIndex = i;
                    break;
                }
            }

            // If no phase had duration (shouldn't happen), use the first phase
            if (furthestPhase == null && log.Phases.Count > 0)
            {
                furthestPhase = log.Phases[0].Name;
                furthestPhaseIndex = 0;
            }
        }

        // Calculate overall clear percentage by summing HP burned across all targets
        if (log.Targets != null && log.Targets.Count > 0)
        {
            // Sum up healthPercentBurned for all targets
            var totalHpBurned = log.Targets.Sum(t => t.HealthPercentBurned);
            var totalPossible = log.Targets.Count * 100m;

            // Clear percentage = total burned / total possible
            var clearPercentage = totalPossible > 0 ? (totalHpBurned / totalPossible) * 100 : 0;

            // Remaining = 100% - clear%
            bossHpRemaining = 100 - clearPercentage;
            if (bossHpRemaining < 0) bossHpRemaining = 0;
        }

        return (furthestPhase, furthestPhaseIndex, bossHpRemaining);
    }

    private static DateTimeOffset ParseEncounterTime(EliteInsightsLog log)
    {
        // Try timeStartStd first (formatted string like "2025-01-06 21:23:06 -06:00")
        if (!string.IsNullOrWhiteSpace(log.TimeStartStd) &&
            DateTimeOffset.TryParse(log.TimeStartStd, out var timeStartStd))
        {
            return timeStartStd;
        }

        // Try encounterStart (formatted string)
        if (!string.IsNullOrWhiteSpace(log.EncounterStart) &&
            DateTimeOffset.TryParse(log.EncounterStart, out var encounterStart))
        {
            return encounterStart;
        }

        // Try timeStart (could be Unix timestamp as string or date string)
        if (!string.IsNullOrWhiteSpace(log.TimeStart))
        {
            // Try parsing as Unix timestamp in milliseconds
            if (long.TryParse(log.TimeStart, out var unixMs) && unixMs > 0)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            }

            // Try parsing as date string
            if (DateTimeOffset.TryParse(log.TimeStart, out var timeStart))
            {
                return timeStart;
            }
        }

        // Fallback to current time if nothing else works
        return DateTimeOffset.UtcNow;
    }
}
