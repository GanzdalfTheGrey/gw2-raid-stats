using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using GW2RaidStats.Infrastructure.Services;
using GW2RaidStats.Infrastructure.Services.Achievements;
using LinqToDB;
using Microsoft.AspNetCore.Mvc;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/discord")]
public class AdminDiscordController : ControllerBase
{
    private readonly RaidStatsDb _db;
    private readonly AchievementOrchestrator _orchestrator;
    private readonly RecordNotificationService _recordService;

    public AdminDiscordController(
        RaidStatsDb db,
        AchievementOrchestrator orchestrator,
        RecordNotificationService recordService)
    {
        _db = db;
        _orchestrator = orchestrator;
        _recordService = recordService;
    }

    [HttpPost("post-session-summary")]
    public async Task<IActionResult> PostSessionSummary(CancellationToken ct)
    {
        // Check for flawless wing achievements before posting summary
        var flawlessAwarded = await _orchestrator.CheckFlawlessWingsForTodayAsync(notify: true, ct);

        // Queue a session_complete notification
        var notification = new NotificationQueueEntity
        {
            Id = Guid.NewGuid(),
            NotificationType = "session_complete",
            Payload = "{}", // SessionNotificationHandler fetches data itself
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(notification, token: ct);

        return Ok(new {
            success = true,
            message = "Session summary queued for Discord",
            flawlessWingsAwarded = flawlessAwarded
        });
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
