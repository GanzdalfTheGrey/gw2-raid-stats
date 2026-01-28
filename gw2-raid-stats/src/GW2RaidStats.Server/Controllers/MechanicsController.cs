using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MechanicsController : ControllerBase
{
    private readonly MechanicSearchService _mechanicSearchService;

    public MechanicsController(MechanicSearchService mechanicSearchService)
    {
        _mechanicSearchService = mechanicSearchService;
    }

    /// <summary>
    /// Get all available mechanics
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<MechanicInfo>>> GetAllMechanics(CancellationToken ct)
    {
        var mechanics = await _mechanicSearchService.GetAllMechanicsAsync(ct);
        return Ok(mechanics);
    }

    /// <summary>
    /// Search mechanic occurrences by player with optional date range
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<MechanicSearchResult>> SearchMechanic(
        [FromQuery] string mechanicName,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mechanicName))
        {
            return BadRequest("Mechanic name is required");
        }

        DateTimeOffset? from = fromDate.HasValue
            ? new DateTimeOffset(fromDate.Value, TimeSpan.Zero)
            : null;
        DateTimeOffset? to = toDate.HasValue
            ? new DateTimeOffset(toDate.Value, TimeSpan.Zero)
            : null;

        var result = await _mechanicSearchService.SearchMechanicAsync(mechanicName, from, to, ct);
        return Ok(result);
    }
}
