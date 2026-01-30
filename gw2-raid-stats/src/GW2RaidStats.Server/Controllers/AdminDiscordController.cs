using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using Microsoft.AspNetCore.Mvc;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/discord")]
public class AdminDiscordController : ControllerBase
{
    private readonly RaidStatsDb _db;

    public AdminDiscordController(RaidStatsDb db)
    {
        _db = db;
    }

    [HttpPost("post-session-summary")]
    public async Task<IActionResult> PostSessionSummary(CancellationToken ct)
    {
        // Queue a session_complete notification
        var notification = new NotificationQueueEntity
        {
            Id = Guid.NewGuid(),
            NotificationType = "session_complete",
            Payload = "{}", // SessionNotificationHandler fetches data itself
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(notification, token: ct);

        return Ok(new { success = true, message = "Session summary queued for Discord" });
    }
}
