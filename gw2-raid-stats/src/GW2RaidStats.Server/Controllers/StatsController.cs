using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly StatsService _statsService;

    public StatsController(StatsService statsService)
    {
        _statsService = statsService;
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardStats>> GetDashboardStats(CancellationToken ct)
    {
        var stats = await _statsService.GetDashboardStatsAsync(ct);
        return Ok(stats);
    }

    [HttpGet("recent")]
    public async Task<ActionResult<List<RecentEncounter>>> GetRecentEncounters(
        [FromQuery] int count = 10,
        CancellationToken ct = default)
    {
        var encounters = await _statsService.GetRecentEncountersAsync(count, ct);
        return Ok(encounters);
    }

    [HttpGet("weekly-highlights")]
    public async Task<ActionResult<WeeklyHighlights>> GetWeeklyHighlights(CancellationToken ct)
    {
        var highlights = await _statsService.GetWeeklyHighlightsAsync(ct);
        return Ok(highlights);
    }
}
