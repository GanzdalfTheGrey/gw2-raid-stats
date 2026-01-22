using Microsoft.AspNetCore.Mvc;
using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly RaidStatsDb _db;

    public HealthController(RaidStatsDb db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult> GetHealth(CancellationToken ct)
    {
        try
        {
            // Test database connectivity with a simple query
            _ = await _db.Encounters.Take(1).CountAsync(ct);

            return Ok(new
            {
                status = "healthy",
                timestamp = DateTimeOffset.UtcNow,
                database = "connected"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new
            {
                status = "unhealthy",
                timestamp = DateTimeOffset.UtcNow,
                database = "disconnected",
                error = ex.Message
            });
        }
    }
}
