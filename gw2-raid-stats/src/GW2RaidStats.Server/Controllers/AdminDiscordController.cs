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

        // Re-fire individual record toots for everything broken during the session.
        // Useful after a backlog import or when the bot was offline at import time —
        // the user clicking "post session summary" gets the full set of records announced
        // alongside the summary embed.
        await _recordService.RetootSessionRecordsAsync(ct);

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
}
