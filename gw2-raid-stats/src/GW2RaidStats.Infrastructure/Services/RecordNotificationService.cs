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
    /// Re-run the record checks (kill time, DPS, Boon DPS, including the patch-record path)
    /// for every successful kill in the most-recent session. Each per-encounter check is the
    /// same one that runs at import time and is time-filtered (e.EncounterTime &lt; this), so
    /// re-running produces the exact set of record_broken notifications that *would have* fired
    /// if the current code had been deployed when the session was imported.
    ///
    /// Used by the admin "Re-check Last Session" action — useful when records were missed
    /// (bot offline at import, or new record-detection logic deployed after the session).
    /// Returns the number of record_broken notifications enqueued.
    /// </summary>
    public async Task<int> RecheckLastSessionRecordsAsync(CancellationToken ct = default)
    {
        var latestEncounter = await _db.Encounters
            .OrderByDescending(e => e.EncounterTime)
            .FirstOrDefaultAsync(ct);
        if (latestEncounter == null) return 0;

        // Same session-window math as StatsService.GetSessionHighlightsAsync:
        // calendar day in the encounter's local timezone.
        var offset = latestEncounter.EncounterTime.Offset;
        var localDate = (latestEncounter.EncounterTime.UtcDateTime + offset).Date;
        var sessionStart = new DateTimeOffset(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), offset);
        var sessionEnd = sessionStart.AddDays(1);

        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = includedAccounts.ToList();

        var sessionKills = await _db.Encounters
            .Where(e => e.EncounterTime >= sessionStart
                     && e.EncounterTime < sessionEnd
                     && e.Success)
            .OrderBy(e => e.EncounterTime)
            .ToListAsync(ct);

        var beforeCount = await _db.NotificationQueue.CountAsync(ct);
        foreach (var enc in sessionKills)
        {
            await CheckKillTimeRecordAsync(enc, ct);
            await CheckDpsRecordsAsync(enc, includedList, ct);
            await CheckBoonDpsRecordsAsync(enc, includedList, ct);
        }
        var afterCount = await _db.NotificationQueue.CountAsync(ct);
        return afterCount - beforeCount;
    }

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

        // Patch-scoped previous best DPS (current patch only)
        var patchStart = await GetCurrentPatchStartAsync(encounter.EncounterTime, ct);
        var (previousPatchHasRecords, previousPatchBestDps) = patchStart.HasValue
            ? await GetPreviousPatchBestDpsAsync(encounter, includedAccounts, patchStart.Value, ct)
            : (false, 0);

        // Get player performances for this encounter, sorted by DPS descending
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => x.pe.EncounterId == encounter.Id)
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .OrderByDescending(x => x.pe.Dps)
            .ToListAsync(ct);

        // Track who broke the record (so we don't double-notify for top 5)
        var recordBreakers = new HashSet<string>();

        // On first clear (no previous records), only the top DPS gets the record
        // On subsequent clears, notify anyone who beats the previous best
        foreach (var current in playerEncounters)
        {
            var isFirstClear = previousTop5.Count == 0;
            var beatsRecord = current.pe.Dps > previousBestDps;
            var isTopDps = current == playerEncounters.First();

            // First clear: only top DPS gets record. Otherwise: anyone who beats previous best
            if ((isFirstClear && isTopDps) || (!isFirstClear && beatsRecord))
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
                continue;
            }

            // Patch record: didn't beat all-time, but beat (or set) the current patch best.
            // Mirrors the overall logic — first patch clear: top DPS only; otherwise: anyone who beats patch best.
            if (!patchStart.HasValue) continue;
            var isPatchFirstClear = !previousPatchHasRecords;
            var beatsPatchRecord = current.pe.Dps > previousPatchBestDps;
            if ((isPatchFirstClear && isTopDps) || (!isPatchFirstClear && beatsPatchRecord))
            {
                var payload = new RecordPayload(
                    "DPS",
                    encounter.BossName,
                    encounter.IsCM,
                    current.p.AccountName,
                    current.pe.Profession,
                    current.pe.Dps,
                    previousPatchHasRecords ? (double?)previousPatchBestDps : null,
                    encounter.LogUrl,
                    IsCurrentPatch: true
                );
                await QueueNotificationAsync("record_broken", payload, ct);
            }
        }

        // Build combined leaderboard: previous top 5 + current encounter players
        // This ensures we calculate correct ranks when multiple players from same encounter enter top 5
        var combinedLeaderboard = previousTop5
            .Select(x => new { x.p.AccountName, x.pe.Profession, x.pe.Dps, IsNew = false })
            .Concat(playerEncounters.Select(x => new { x.p.AccountName, x.pe.Profession, x.pe.Dps, IsNew = true }))
            .OrderByDescending(x => x.Dps)
            .Select((x, index) => new { x.AccountName, x.Profession, x.Dps, x.IsNew, Rank = index + 1 })
            .ToList();

        // Notify for new entries in positions 2-5 (position 1 is handled as record breaker above)
        foreach (var entry in combinedLeaderboard.Where(x => x.IsNew && x.Rank >= 2 && x.Rank <= 5))
        {
            // Skip if they already broke the record
            if (recordBreakers.Contains(entry.AccountName))
                continue;

            var payload = new Top5Payload(
                "DPS",
                encounter.BossName,
                encounter.IsCM,
                entry.AccountName,
                entry.Profession,
                entry.Dps,
                entry.Rank,
                encounter.LogUrl
            );

            await QueueNotificationAsync("top_5", payload, ct);
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

        // Patch-scoped previous best boon DPS (current patch only)
        var patchStart = await GetCurrentPatchStartAsync(encounter.EncounterTime, ct);
        var (previousPatchHasRecords, previousPatchBestDps) = patchStart.HasValue
            ? await GetPreviousPatchBestBoonDpsAsync(encounter, includedAccounts, patchStart.Value, ct)
            : (false, 0);

        // Get boon DPS performances for this encounter, sorted by DPS descending
        var boonPlayers = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => x.pe.EncounterId == encounter.Id)
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                        (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .OrderByDescending(x => x.pe.Dps)
            .ToListAsync(ct);

        // On first clear (no previous records), only the top boon DPS gets the record
        // On subsequent clears, notify anyone who beats the previous best
        foreach (var current in boonPlayers)
        {
            var isFirstClear = previousBest == null;
            var beatsRecord = current.pe.Dps > previousBestDps;
            var isTopBoonDps = current == boonPlayers.First();

            if ((isFirstClear && isTopBoonDps) || (!isFirstClear && beatsRecord))
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
                continue;
            }

            // Patch record: didn't beat all-time, but beat (or set) the current patch best.
            if (!patchStart.HasValue) continue;
            var isPatchFirstClear = !previousPatchHasRecords;
            var beatsPatchRecord = current.pe.Dps > previousPatchBestDps;
            if ((isPatchFirstClear && isTopBoonDps) || (!isPatchFirstClear && beatsPatchRecord))
            {
                var payload = new RecordPayload(
                    "Boon DPS",
                    encounter.BossName,
                    encounter.IsCM,
                    current.p.AccountName,
                    current.pe.Profession,
                    current.pe.Dps,
                    previousPatchHasRecords ? (double?)previousPatchBestDps : null,
                    encounter.LogUrl,
                    IsCurrentPatch: true
                );
                await QueueNotificationAsync("record_broken", payload, ct);
            }
        }
    }

    private async Task<DateTimeOffset?> GetCurrentPatchStartAsync(DateTimeOffset asOf, CancellationToken ct)
    {
        var patch = await _db.LeaderboardPatches
            .Where(p => p.StartDate <= asOf)
            .OrderByDescending(p => p.StartDate)
            .FirstOrDefaultAsync(ct);
        return patch?.StartDate;
    }

    private async Task<(bool HasRecords, int BestDps)> GetPreviousPatchBestDpsAsync(
        EncounterEntity encounter,
        List<string> includedAccounts,
        DateTimeOffset patchStart,
        CancellationToken ct)
    {
        var top = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == encounter.TriggerId
                     && x.e.IsCM == encounter.IsCM
                     && x.e.Success
                     && x.e.Id != encounter.Id
                     && x.e.EncounterTime < encounter.EncounterTime
                     && x.e.EncounterTime >= patchStart)
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .OrderByDescending(x => x.pe.Dps)
            .Select(x => (int?)x.pe.Dps)
            .FirstOrDefaultAsync(ct);

        return (top.HasValue, top ?? 0);
    }

    private async Task<(bool HasRecords, int BestDps)> GetPreviousPatchBestBoonDpsAsync(
        EncounterEntity encounter,
        List<string> includedAccounts,
        DateTimeOffset patchStart,
        CancellationToken ct)
    {
        var top = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.TriggerId == encounter.TriggerId
                     && x.e.IsCM == encounter.IsCM
                     && x.e.Success
                     && x.e.Id != encounter.Id
                     && x.e.EncounterTime < encounter.EncounterTime
                     && x.e.EncounterTime >= patchStart)
            .Where(x => includedAccounts.Contains(x.p.AccountName))
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                        (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .OrderByDescending(x => x.pe.Dps)
            .Select(x => (int?)x.pe.Dps)
            .FirstOrDefaultAsync(ct);

        return (top.HasValue, top ?? 0);
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
        if (encounter.FurthestPhase == null && encounter.BossHealthPercentRemaining == null)
            return;

        // Get previous encounters to find the best using canonical phase ordering
        var previousEncounters = await _db.Encounters
            .Where(e => e.TriggerId == HtcmTriggerId
                     && e.IsCM
                     && e.Id != encounter.Id
                     && e.EncounterTime < encounter.EncounterTime
                     && e.FurthestPhase != null)
            .Select(e => new { e.FurthestPhase, e.BossHealthPercentRemaining })
            .ToListAsync(ct);

        // Find the previous best using canonical phase ordering
        var previousBestPhaseIndex = previousEncounters.Count > 0
            ? previousEncounters.Max(e => HtcmProgService.GetCanonicalPhaseIndex(e.FurthestPhase))
            : 0;
        var previousBestHp = previousEncounters.Count > 0
            ? previousEncounters.Min(e => e.BossHealthPercentRemaining ?? 100)
            : 100m;

        // Compare current encounter using canonical ordering
        var currentPhaseIndex = HtcmProgService.GetCanonicalPhaseIndex(encounter.FurthestPhase);
        var isNewBestPhase = currentPhaseIndex > previousBestPhaseIndex;

        var isNewBestHp = previousEncounters.Count == 0 ||
            (encounter.BossHealthPercentRemaining ?? 100) < previousBestHp;

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
        string? LogUrl,
        bool IsCurrentPatch = false
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
