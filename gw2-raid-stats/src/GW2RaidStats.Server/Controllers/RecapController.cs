using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/recap")]
public class RecapController : ControllerBase
{
    private readonly RecapService _recapService;

    public RecapController(RecapService recapService)
    {
        _recapService = recapService;
    }

    /// <summary>
    /// Get available years for recap
    /// </summary>
    [HttpGet("years")]
    public async Task<ActionResult<List<int>>> GetAvailableYears(CancellationToken ct)
    {
        var years = await _recapService.GetAvailableYearsAsync(ct);
        return Ok(years);
    }

    /// <summary>
    /// Get yearly recap data
    /// </summary>
    [HttpGet("{year}")]
    public async Task<ActionResult<YearlyRecap>> GetYearlyRecap(int year, CancellationToken ct)
    {
        var recap = await _recapService.GetYearlyRecapAsync(year, ct);
        return Ok(recap);
    }
}
