using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Core;
using GW2RaidStats.Core.EliteInsights;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using GW2RaidStats.Infrastructure.Services.Achievements;

namespace GW2RaidStats.Infrastructure.Services.Import;

public class LogImportService
{
    private readonly RaidStatsDb _db;
    private readonly RecordNotificationService _recordNotificationService;
    private readonly AchievementOrchestrator? _achievementOrchestrator;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LogImportService(
        RaidStatsDb db,
        RecordNotificationService recordNotificationService,
        AchievementOrchestrator? achievementOrchestrator = null)
    {
        _db = db;
        _recordNotificationService = recordNotificationService;
        _achievementOrchestrator = achievementOrchestrator;
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

            // Check for achievements (for all encounters - some track progress on wipes)
            if (_achievementOrchestrator != null)
            {
                await _achievementOrchestrator.CheckAfterEncounterAsync(encounterId, notify: true, ct);
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

            // Get boon self-uptime (player had this on themselves, active-time basis)
            var boons = GetBoonSelfUptimeFromPlayer(eiPlayer);

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
                ResurrectTime = support?.ResurrectTime ?? 0,
                CondiCleanse = support?.CondiCleanse ?? 0,
                BoonStrips = support?.BoonStrips ?? 0,

                // Boon generation
                QuicknessGeneration = quicknessGen,
                AlacracityGeneration = alacrityGen,

                // Boon self-uptime (active-time basis)
                QuicknessSelfUptime = boons.Quickness,
                AlacritySelfUptime = boons.Alacrity,
                MightAvgStacks = boons.MightAvgStacks,
                FuryUptime = boons.Fury,
                RegenerationUptime = boons.Regeneration,
                ProtectionUptime = boons.Protection,
                SwiftnessUptime = boons.Swiftness,

                // Average distance to squad centroid
                StackDistance = eiPlayer.StatsAll?.FirstOrDefault()?.StackDist,

                // Healing stats (from extension)
                Healing = healingTotal,
                HealingPowerHealing = healingPower,
                Hps = hps,

                // Character attribute - Healing Power stat (always available)
                HealingPowerStat = eiPlayer.HealingPower,

                // Role classification for achievement tracking
                Role = CalculateRole(eiPlayer.HealingPower, quicknessGen, alacrityGen),

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

        // HTCM only: per-player per-phase stats (burst, deaths, debilitated uptime).
        // Bounded storage — ~12 phases × 10 players per pull. Other encounters keep
        // their single-row PlayerEncounter stats; per-phase granularity adds up too
        // fast across hundreds of fights to be worthwhile elsewhere.
        if (log.TriggerId == HtcmTriggerId && log.Phases != null && log.Phases.Count > 0)
        {
            await ImportHtcmPhaseStatsAsync(log, encounter.Id, ct);
        }

        return encounter.Id;
    }

    // HTCM trigger ID. Matches WingMapping + HtcmProgService for grep-ability.
    private const int HtcmTriggerId = 43488;

    // Buff ID for the Debilitated stacking debuff in HTCM. Sourced via the player's
    // buffUptimesActive entry; only the uptime field is consumed (% of phase time
    // the buff was up). Count + max-stacks tracking is a follow-up if uptime % proves
    // too coarse — would need parsing of buffsActiveStats with the presence field.
    private const long DebilitatedBuffId = 67972;

    private async Task ImportHtcmPhaseStatsAsync(EliteInsightsLog log, Guid encounterId, CancellationToken ct)
    {
        var playerIdsByAccount = await BulkLoadPlayerIdsAsync(log, ct);
        if (playerIdsByAccount.Count == 0) return;

        foreach (var row in BuildHtcmPhaseStatRows(encounterId, log, playerIdsByAccount))
        {
            await _db.InsertAsync(row, token: ct);
        }
    }

    private async Task<Dictionary<string, Guid>> BulkLoadPlayerIdsAsync(EliteInsightsLog log, CancellationToken ct)
    {
        var accountNames = log.Players
            .Select(p => p.Account)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct()
            .ToList();
        if (accountNames.Count == 0) return new Dictionary<string, Guid>();

        return (await _db.Players
            .Where(p => accountNames.Contains(p.AccountName))
            .ToListAsync(ct))
            .ToDictionary(p => p.AccountName, p => p.Id);
    }

    /// <summary>
    /// Builds per-player per-phase rows from an HTCM log. Shared between the importer
    /// (first-time write) and RescanService (backfill for historical encounters).
    /// Caller is responsible for ensuring the encounter is HTCM and persisting the rows.
    /// </summary>
    public static IEnumerable<PlayerEncounterPhaseStatEntity> BuildHtcmPhaseStatRows(
        Guid encounterId, EliteInsightsLog log, Dictionary<string, Guid> playerIdsByAccount)
    {
        if (log.Phases == null || log.Phases.Count == 0) yield break;

        foreach (var eiPlayer in log.Players)
        {
            if (string.IsNullOrEmpty(eiPlayer.Account)) continue;
            if (!playerIdsByAccount.TryGetValue(eiPlayer.Account, out var playerId)) continue;

            var debilBuff = eiPlayer.BuffUptimesActive?
                .FirstOrDefault(b => b.Id == DebilitatedBuffId);

            for (int phaseIndex = 0; phaseIndex < log.Phases.Count; phaseIndex++)
            {
                var phase = log.Phases[phaseIndex];
                if (phase.End <= phase.Start) continue;

                var dpsStats = eiPlayer.DpsAll != null && phaseIndex < eiPlayer.DpsAll.Count
                    ? eiPlayer.DpsAll[phaseIndex] : null;
                var defense = eiPlayer.Defenses != null && phaseIndex < eiPlayer.Defenses.Count
                    ? eiPlayer.Defenses[phaseIndex] : null;
                // Debilitated is a stacking buff. EI emits two separate values per
                // phase: Uptime (avg stack count 0-5) and Presence (actual % uptime
                // 0-100 at any stack count). The HTML report's "Uptime" column for
                // this buff is Presence, and "Avg Active" is Uptime. Capture both.
                decimal? debilUptimePct = null;
                decimal? debilAvgStacks = null;
                if (debilBuff?.BuffData != null && phaseIndex < debilBuff.BuffData.Count)
                {
                    var bd = debilBuff.BuffData[phaseIndex];
                    if (bd.Presence > 0m) debilUptimePct = bd.Presence;
                    if (bd.Uptime > 0m) debilAvgStacks = bd.Uptime;
                }

                yield return new PlayerEncounterPhaseStatEntity
                {
                    Id = Guid.NewGuid(),
                    EncounterId = encounterId,
                    PlayerId = playerId,
                    PhaseIndex = phaseIndex,
                    PhaseName = phase.Name,
                    Dps = dpsStats?.Dps ?? 0,
                    Damage = dpsStats?.Damage ?? 0,
                    DeadCount = defense?.DeadCount ?? 0,
                    DownCount = defense?.DownCount ?? 0,
                    DeadDurationMs = (int)((defense?.DeadDuration ?? 0m) * 1000m),
                    DownDurationMs = (int)((defense?.DownDuration ?? 0m) * 1000m),
                    DeadAtPhaseStart = WasDeadAtMs(eiPlayer.DeadCombatTimes, phase.Start),
                    DebilitatedUptimePct = debilUptimePct,
                    DebilitatedAvgStacks = debilAvgStacks,
                    CreatedAt = DateTimeOffset.UtcNow
                };
            }
        }
    }

    // EI's deadCombatTimes is a list of [startMs, endMs] pairs (endMs = -1 means
    // the player stayed dead through to fight end). True if any pair brackets `timeMs`.
    public static bool WasDeadAtMs(List<List<int>>? deadCombatTimes, int timeMs)
    {
        if (deadCombatTimes == null) return false;
        foreach (var pair in deadCombatTimes)
        {
            if (pair.Count < 2) continue;
            var start = pair[0];
            var end = pair[1];
            if (start <= timeMs && (end == -1 || end > timeMs)) return true;
        }
        return false;
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

        // Create new player - handle race condition with retry
        player = new PlayerEntity
        {
            Id = Guid.NewGuid(),
            AccountName = accountName,
            FirstSeen = encounterTime,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await _db.InsertAsync(player, token: ct);
            return player;
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505") // unique_violation
        {
            // Another worker inserted this player first - fetch it
            return await _db.Players
                .FirstAsync(p => p.AccountName == accountName, ct);
        }
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

    // Per-player boon self-uptime extracted from one log. Duration boons are % uptime
    // 0-100; MightAvgStacks is average stacks 0-25.
    internal class BoonSelfUptimes
    {
        public decimal? Quickness { get; set; }
        public decimal? Alacrity { get; set; }
        public decimal? MightAvgStacks { get; set; }
        public decimal? Fury { get; set; }
        public decimal? Regeneration { get; set; }
        public decimal? Protection { get; set; }
        public decimal? Swiftness { get; set; }
    }

    // From buffUptimesActive (excludes dead/down time — matches EI HTML's
    // "Phase active duration" view). buffData[0] is the full-encounter phase.
    // For duration boons the value is % uptime 0-100; for Might (intensity) it's
    // average stacks 0-25. Internal so RescanService can backfill historical encounters.
    internal static BoonSelfUptimes GetBoonSelfUptimeFromPlayer(EIPlayer player)
    {
        if (player.BuffUptimesActive == null) return new BoonSelfUptimes();

        var result = new BoonSelfUptimes();
        foreach (var buff in player.BuffUptimesActive)
        {
            var uptime = buff.BuffData?.FirstOrDefault()?.Uptime;
            if (uptime == null) continue;
            switch (buff.Id)
            {
                case GW2BuffIds.Quickness: result.Quickness = uptime; break;
                case GW2BuffIds.Alacrity: result.Alacrity = uptime; break;
                case GW2BuffIds.Might: result.MightAvgStacks = uptime; break;
                case GW2BuffIds.Fury: result.Fury = uptime; break;
                case GW2BuffIds.Regeneration: result.Regeneration = uptime; break;
                case GW2BuffIds.Protection: result.Protection = uptime; break;
                case GW2BuffIds.Swiftness: result.Swiftness = uptime; break;
            }
        }
        return result;
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
                    bossHpRemaining = 100 - clearPercentage;
                    if (bossHpRemaining < 0) bossHpRemaining = 0;
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
                    bossHpRemaining = 100 - clearPercentage;
                    if (bossHpRemaining < 0) bossHpRemaining = 0;
                }
            }
        }

        return (furthestPhase, furthestPhaseIndex, bossHpRemaining);
    }

    /// <summary>
    /// Calculate the player's role based on healing power and boon generation.
    /// Roles: heal_alac, heal_quick, dps_alac, dps_quick, pure_dps
    /// </summary>
    public static string CalculateRole(int healingPowerStat, decimal? quicknessGen, decimal? alacrityGen)
    {
        const decimal BoonThreshold = 10m;
        const int HealerStatThreshold = 1;

        var isHealer = healingPowerStat >= HealerStatThreshold &&
                       ((quicknessGen ?? 0) >= BoonThreshold || (alacrityGen ?? 0) >= BoonThreshold);
        var hasAlacrity = (alacrityGen ?? 0) >= BoonThreshold;
        var hasQuickness = (quicknessGen ?? 0) >= BoonThreshold;

        if (isHealer && hasAlacrity)
            return "heal_alac";
        if (isHealer && hasQuickness)
            return "heal_quick";
        if (hasAlacrity)
            return "dps_alac";
        if (hasQuickness)
            return "dps_quick";

        return "pure_dps";
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
