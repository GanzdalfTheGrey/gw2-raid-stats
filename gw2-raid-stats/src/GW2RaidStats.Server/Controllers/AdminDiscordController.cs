using System.Text.Json;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using GW2RaidStats.Infrastructure.Services;
using GW2RaidStats.Infrastructure.Services.Achievements;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/discord")]
public class AdminDiscordController : ControllerBase
{
    private readonly RaidStatsDb _db;
    private readonly AchievementOrchestrator _orchestrator;
    private readonly RecordNotificationService _recordService;
    private readonly StatsService _statsService;

    public AdminDiscordController(
        RaidStatsDb db,
        AchievementOrchestrator orchestrator,
        RecordNotificationService recordService,
        StatsService statsService)
    {
        _db = db;
        _orchestrator = orchestrator;
        _recordService = recordService;
        _statsService = statsService;
    }

    /// <summary>
    /// Queues the Discord summaries for the most recent session. A prog night on HTCM
    /// gets its own summary; a night with other bosses gets the regular one. A mixed
    /// night queues both, with the HTCM pulls left out of the regular summary.
    /// </summary>
    [HttpPost("post-session-summary")]
    public async Task<IActionResult> PostSessionSummary(CancellationToken ct)
    {
        // Check for flawless wing achievements before posting summary
        var flawlessAwarded = await _orchestrator.CheckFlawlessWingsForTodayAsync(notify: true, ct);

        var range = await _statsService.GetLatestSessionRangeAsync(ct);
        if (range == null)
        {
            return Ok(new { success = false, message = "No encounters found to summarise" });
        }

        var (sessionStart, sessionEnd) = range.Value;
        var htcmProgIds = await _statsService.GetHtcmProgEncounterIdsAsync(sessionStart, sessionEnd, ct);

        var htcmQueued = false;
        if (htcmProgIds.Count > 0)
        {
            // Key the summary off an actual HTCM encounter's date so it matches how the
            // HTCM prog page groups sessions.
            var htcmSessionDate = await _db.Encounters
                .Where(e => htcmProgIds.Contains(e.Id))
                .OrderBy(e => e.EncounterTime)
                .Select(e => e.EncounterTime)
                .FirstOrDefaultAsync(ct);

            await QueueAsync("htcm_session_summary",
                JsonSerializer.Serialize(new { SessionDate = htcmSessionDate.Date }), ct);
            htcmQueued = true;
        }

        // Only queue the regular summary when the night had something other than HTCM prog.
        var nonHtcmCount = await _db.Encounters
            .Where(e => e.EncounterTime >= sessionStart
                     && e.EncounterTime < sessionEnd
                     && !htcmProgIds.Contains(e.Id))
            .CountAsync(ct);

        var sessionQueued = false;
        if (nonHtcmCount > 0)
        {
            // SessionNotificationHandler fetches its own data
            await QueueAsync("session_complete", "{}", ct);
            sessionQueued = true;
        }

        var queued = new List<string>();
        if (htcmQueued) queued.Add("HTCM progress summary");
        if (sessionQueued) queued.Add("session summary");

        return Ok(new {
            success = true,
            message = queued.Count > 0
                ? $"{string.Join(" + ", queued)} queued for Discord"
                : "Nothing to summarise for the last session",
            htcmSummaryQueued = htcmQueued,
            sessionSummaryQueued = sessionQueued,
            flawlessWingsAwarded = flawlessAwarded
        });
    }

    /// <summary>
    /// TEMPORARY — testing aid. Posts the HTCM summary for the most recent night that had
    /// HTCM CM attempts, ignoring the usual rules (no kill required, doesn't have to be the
    /// latest session, and no regular summary is queued alongside). Remove once the HTCM
    /// summary format is settled; the real trigger is post-session-summary.
    /// </summary>
    [HttpPost("post-htcm-summary")]
    public async Task<IActionResult> PostHtcmSummary(CancellationToken ct)
    {
        var latestHtcm = await _db.Encounters
            .Where(e => e.TriggerId == HtcmProgService.HtcmTriggerId
                     && e.IsCM
                     && e.DurationMs >= HtcmProgService.MinDurationMs)
            .OrderByDescending(e => e.EncounterTime)
            .Select(e => e.EncounterTime)
            .FirstOrDefaultAsync(ct);

        if (latestHtcm == default)
        {
            return Ok(new { success = false, message = "No HTCM attempts found" });
        }

        var sessionDate = latestHtcm.Date;
        await QueueAsync("htcm_session_summary",
            JsonSerializer.Serialize(new { SessionDate = sessionDate }), ct);

        return Ok(new {
            success = true,
            sessionDate,
            message = $"HTCM summary for {sessionDate:yyyy-MM-dd} queued for Discord"
        });
    }

    private async Task QueueAsync(string notificationType, string payload, CancellationToken ct)
    {
        await _db.InsertAsync(new NotificationQueueEntity
        {
            Id = Guid.NewGuid(),
            NotificationType = notificationType,
            Payload = payload,
            CreatedAt = DateTimeOffset.UtcNow
        }, token: ct);
    }

    /// <summary>
    /// Re-run the record-broken checks for every kill in the most-recent session and
    /// enqueue Discord toots for each one. Useful when records were missed (bot offline
    /// at import time, or new record-detection logic deployed after the session).
    /// </summary>
    [HttpPost("recheck-last-session")]
    public async Task<IActionResult> RecheckLastSession(CancellationToken ct)
    {
        var enqueued = await _recordService.RecheckLastSessionRecordsAsync(ct);
        return Ok(new {
            success = true,
            enqueued,
            message = $"{enqueued} record notification(s) queued for Discord"
        });
    }
}
