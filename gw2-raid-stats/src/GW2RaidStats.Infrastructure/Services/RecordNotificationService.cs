using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services;

/// <summary>
/// Service for detecting broken records and queueing Discord notifications
/// </summary>
public class RecordNotificationService
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;

    // Threshold for considering someone a boon support (generation % to squad)
    private const decimal BoonSupportThreshold = 10m;

    public RecordNotificationService(RaidStatsDb db, IncludedPlayerService includedPlayerService)
    {
        _db = db;
        _includedPlayerService = includedPlayerService;
    }

    // Milestone thresholds for total kills
    private static readonly int[] KillMilestones = { 100, 250, 500, 1000, 2500, 5000, 10000 };

    // HTCM trigger ID
    private const int HtcmTriggerId = 43488;

    /// <summary>
    /// Check for broken records after an encounter is imported and queue notifications
    /// </summary>
    public async Task CheckAndQueueRecordNotificationsAsync(Guid encounterId, CancellationToken ct = default)
    {
        // Get the encounter details
        var encounter = await _db.Encounters
            .FirstOrDefaultAsync(e => e.Id == encounterId, ct);

        if (encounter == null) return;

        // Get included players (guild members)
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = includedAccounts.ToList();

        // For successful kills only
        if (encounter.Success)
        {
            // Check for kill time record
            await CheckKillTimeRecordAsync(encounter, ct);

            // Check for DPS records
            await CheckDpsRecordsAsync(encounter, includedList, ct);

            // Check for Boon DPS records
            await CheckBoonDpsRecordsAsync(encounter, includedList, ct);

            // Check for first kill milestone
            await CheckFirstKillMilestoneAsync(encounter, ct);

            // Check for total kills milestone
            await CheckTotalKillsMilestoneAsync(ct);
        }

        // Check for HTCM progress (even on wipes)
        if (encounter.TriggerId == HtcmTriggerId && encounter.IsCM)
        {
            await CheckHtcmProgressAsync(encounter, ct);
        }
    }

    private async Task CheckKillTimeRecordAsync(EncounterEntity encounter, CancellationToken ct)
    {
        // Get the previous best kill time for this boss
        var previousBest = await _db.Encounters
            .Where(e => e.TriggerId == encounter.TriggerId
                     && e.IsCM == encounter.IsCM
                     && e.Success
                     && e.Id != encounter.Id
                     && e.EncounterTime < encounter.EncounterTime)
            .OrderBy(e => e.DurationMs)
            .FirstOrDefaultAsync(ct);

        if (previousBest == null || encounter.DurationMs < previousBest.DurationMs)
        {
            var payload = new RecordPayload(
                "Kill Time",
                encounter.BossName,
                encounter.IsCM,
                null,
                null,
                encounter.DurationMs / 1000.0,
                previousBest?.DurationMs / 1000.0,
                encounter.LogUrl
            );

            await QueueNotificationAsync("record_broken", payload, ct);
        }
    }

    private async Task CheckDpsRecordsAsync(EncounterEntity encounter, List<string> includedAccounts, CancellationToken ct)
    {
        // Get top 5 DPS for this boss (before this encounter) to check for placements
        var previousTop5 = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == encounter.TriggerId
                     && x.e.IsCM == encounter.IsCM
                     && x.e.Success
                     && x.e.Id != encounter.Id
                     && x.e.EncounterTime < encounter.EncounterTime)
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .OrderByDescending(x => x.pe.Dps)
            .Take(5)
            .ToListAsync(ct);

        var previousBestDps = previousTop5.FirstOrDefault()?.pe.Dps ?? 0;
        var previousTop5Threshold = previousTop5.Count >= 5 ? previousTop5.Last().pe.Dps : 0;

        // Get player performances for this encounter, sorted by DPS descending
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => x.pe.EncounterId == encounter.Id)
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .OrderByDescending(x => x.pe.Dps)
            .ToListAsync(ct);

        // Track who broke the record (so we don't double-notify for top 5)
        var recordBreakers = new HashSet<string>();

        // Notify for ALL players who beat the previous record (highest DPS first)
        foreach (var current in playerEncounters)
        {
            if (previousTop5.Count == 0 || current.pe.Dps > previousBestDps)
            {
                recordBreakers.Add(current.p.AccountName);

                var payload = new RecordPayload(
                    "DPS",
                    encounter.BossName,
                    encounter.IsCM,
                    current.p.AccountName,
                    current.pe.Profession,
                    current.pe.Dps,
                    previousTop5.FirstOrDefault()?.pe.Dps,
                    encounter.LogUrl
                );

                await QueueNotificationAsync("record_broken", payload, ct);
            }
        }

        // Check for top 5 placements (only if they didn't break the record)
        foreach (var current in playerEncounters)
        {
            // Skip if they already broke the record
            if (recordBreakers.Contains(current.p.AccountName))
                continue;

            // Check if they would place in top 5
            if (current.pe.Dps > previousTop5Threshold)
            {
                // Calculate their rank (how many in previous top 5 they beat + 1)
                var rank = previousTop5.Count(x => current.pe.Dps > x.pe.Dps) + 1;

                // Only notify for positions 2-5 (1 is a record breaker)
                if (rank >= 2 && rank <= 5)
                {
                    var payload = new Top5Payload(
                        "DPS",
                        encounter.BossName,
                        encounter.IsCM,
                        current.p.AccountName,
                        current.pe.Profession,
                        current.pe.Dps,
                        rank,
                        encounter.LogUrl
                    );

                    await QueueNotificationAsync("top_5", payload, ct);
                }
            }
        }
    }

    private async Task CheckBoonDpsRecordsAsync(EncounterEntity encounter, List<string> includedAccounts, CancellationToken ct)
    {
        // Get previous best boon DPS for this boss
        var previousBest = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == encounter.TriggerId
                     && x.e.IsCM == encounter.IsCM
                     && x.e.Success
                     && x.e.Id != encounter.Id
                     && x.e.EncounterTime < encounter.EncounterTime)
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                        (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .OrderByDescending(x => x.pe.Dps)
            .FirstOrDefaultAsync(ct);

        var previousBestDps = previousBest?.pe.Dps ?? 0;

        // Get boon DPS performances for this encounter, sorted by DPS descending
        var boonPlayers = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => x.pe.EncounterId == encounter.Id)
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                        (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .OrderByDescending(x => x.pe.Dps)
            .ToListAsync(ct);

        // Notify for ALL boon players who beat the previous record (highest DPS first)
        foreach (var current in boonPlayers)
        {
            if (previousBest == null || current.pe.Dps > previousBestDps)
            {
                var payload = new RecordPayload(
                    "Boon DPS",
                    encounter.BossName,
                    encounter.IsCM,
                    current.p.AccountName,
                    current.pe.Profession,
                    current.pe.Dps,
                    previousBest?.pe.Dps,
                    encounter.LogUrl
                );

                await QueueNotificationAsync("record_broken", payload, ct);
            }
        }
    }

    private async Task CheckFirstKillMilestoneAsync(EncounterEntity encounter, CancellationToken ct)
    {
        // Check if this is the first kill of this boss (NM or CM separately)
        var previousKill = await _db.Encounters
            .Where(e => e.TriggerId == encounter.TriggerId
                     && e.IsCM == encounter.IsCM
                     && e.Success
                     && e.Id != encounter.Id
                     && e.EncounterTime < encounter.EncounterTime)
            .FirstOrDefaultAsync(ct);

        if (previousKill == null)
        {
            var mode = encounter.IsCM ? "CM" : "NM";
            var payload = new MilestonePayload(
                "first_kill",
                1,
                $"First {encounter.BossName} ({mode}) kill!"
            );

            await QueueNotificationAsync("milestone", payload, ct);
        }
    }

    private async Task CheckTotalKillsMilestoneAsync(CancellationToken ct)
    {
        // Get total successful kills
        var totalKills = await _db.Encounters
            .Where(e => e.Success)
            .CountAsync(ct);

        // Check if we just hit a milestone
        foreach (var milestone in KillMilestones)
        {
            if (totalKills == milestone)
            {
                var payload = new MilestonePayload(
                    "total_kills",
                    milestone,
                    $"Reached {milestone:N0} total raid kills!"
                );

                await QueueNotificationAsync("milestone", payload, ct);
                break;
            }
        }
    }

    private async Task CheckHtcmProgressAsync(EncounterEntity encounter, CancellationToken ct)
    {
        if (encounter.FurthestPhaseIndex == null && encounter.BossHealthPercentRemaining == null)
            return;

        // Get previous best phase and HP
        var previousBest = await _db.Encounters
            .Where(e => e.TriggerId == HtcmTriggerId
                     && e.IsCM
                     && e.Id != encounter.Id
                     && e.EncounterTime < encounter.EncounterTime)
            .OrderByDescending(e => e.FurthestPhaseIndex)
            .ThenBy(e => e.BossHealthPercentRemaining)
            .FirstOrDefaultAsync(ct);

        var isNewBestPhase = previousBest == null ||
            (encounter.FurthestPhaseIndex ?? 0) > (previousBest.FurthestPhaseIndex ?? 0);

        var isNewBestHp = previousBest == null ||
            (encounter.BossHealthPercentRemaining ?? 100) < (previousBest.BossHealthPercentRemaining ?? 100);

        // Only notify if this is actual progress
        if (isNewBestPhase || isNewBestHp)
        {
            // Get pull number for this session
            var today = encounter.EncounterTime.Date;
            var pullNumber = await _db.Encounters
                .Where(e => e.TriggerId == HtcmTriggerId
                         && e.IsCM
                         && e.EncounterTime.Date == today
                         && e.EncounterTime <= encounter.EncounterTime)
                .CountAsync(ct);

            var payload = new HtcmProgressPayload(
                pullNumber,
                encounter.FurthestPhase ?? "Unknown",
                encounter.BossHealthPercentRemaining ?? 100,
                isNewBestPhase,
                isNewBestHp
            );

            await QueueNotificationAsync("htcm_progress", payload, ct);
        }
    }

    private async Task QueueNotificationAsync<T>(string notificationType, T payload, CancellationToken ct)
    {
        var notification = new NotificationQueueEntity
        {
            Id = Guid.NewGuid(),
            NotificationType = notificationType,
            Payload = JsonSerializer.Serialize(payload),
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(notification, token: ct);
    }

    private record RecordPayload(
        string RecordType,
        string BossName,
        bool IsCM,
        string? PlayerName,
        string? Profession,
        double NewValue,
        double? PreviousValue,
        string? LogUrl
    );

    private record MilestonePayload(
        string Type,
        int Value,
        string Description
    );

    private record HtcmProgressPayload(
        int PullNumber,
        string Phase,
        decimal BossHpRemaining,
        bool IsNewBestPhase,
        bool IsNewBestHp
    );

    private record Top5Payload(
        string RecordType,
        string BossName,
        bool IsCM,
        string PlayerName,
        string Profession,
        int Dps,
        int Rank,
        string? LogUrl
    );
}
