using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HtcmProgController : ControllerBase
{
    private readonly HtcmProgService _htcmProgService;

    public HtcmProgController(HtcmProgService htcmProgService)
    {
        _htcmProgService = htcmProgService;
    }

    /// <summary>
    /// Get all available sessions (days) with HTCM attempts
    /// </summary>
    [HttpGet("sessions")]
    public async Task<ActionResult<List<HtcmSession>>> GetSessions(CancellationToken ct)
    {
        var sessions = await _htcmProgService.GetAvailableSessionsAsync(ct);
        return Ok(sessions);
    }

    /// <summary>
    /// Get detailed summary for a specific session
    /// </summary>
    [HttpGet("sessions/{date}")]
    public async Task<ActionResult<HtcmSessionDetail>> GetSessionDetail(DateTime date, CancellationToken ct)
    {
        var detail = await _htcmProgService.GetSessionDetailAsync(date, ct);
        if (detail == null)
            return NotFound();
        return Ok(detail);
    }

    /// <summary>
    /// Get progression data for all sessions (for charts)
    /// </summary>
    [HttpGet("progression")]
    public async Task<ActionResult<HtcmProgressionData>> GetProgressionData(CancellationToken ct)
    {
        var data = await _htcmProgService.GetProgressionDataAsync(ct);
        return Ok(data);
    }

    /// <summary>
    /// Get mechanic trends across sessions
    /// </summary>
    [HttpGet("mechanics")]
    public async Task<ActionResult<List<HtcmMechanicTrend>>> GetMechanicTrends(CancellationToken ct)
    {
        var trends = await _htcmProgService.GetMechanicTrendsAsync(ct);
        return Ok(trends);
    }

    /// <summary>
    /// Get overall phase DPS averages across all sessions
    /// </summary>
    [HttpGet("phase-dps")]
    public async Task<ActionResult<List<HtcmPhaseDpsAverage>>> GetOverallPhaseDps(CancellationToken ct)
    {
        var averages = await _htcmProgService.GetOverallPhaseDpsAsync(ct);
        return Ok(averages);
    }

    /// <summary>
    /// Get phase DPS trends across sessions
    /// </summary>
    [HttpGet("phase-dps-trends")]
    public async Task<ActionResult<List<HtcmPhaseDpsTrend>>> GetPhaseDpsTrends(CancellationToken ct)
    {
        var trends = await _htcmProgService.GetPhaseDpsTrendsAsync(ct);
        return Ok(trends);
    }

    /// <summary>
    /// Get all unique mechanics from HTCM encounters (for discovery/debugging)
    /// </summary>
    [HttpGet("all-mechanics")]
    public async Task<ActionResult<List<HtcmMechanicInfo>>> GetAllMechanics(CancellationToken ct)
    {
        var mechanics = await _htcmProgService.GetAllMechanicsAsync(ct);
        return Ok(mechanics);
    }
}
