using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/availability")]
public class AvailabilityController : ControllerBase
{
    private readonly AvailabilityService _service;

    public AvailabilityController(AvailabilityService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<PlayerAvailabilityRow>>> GetAll(CancellationToken ct)
    {
        var rows = await _service.GetAllAsync(ct);
        return Ok(rows);
    }

    [HttpPut("{playerId:guid}")]
    public async Task<ActionResult> Update(
        Guid playerId,
        [FromBody] UpdateAvailabilityRequest request,
        CancellationToken ct)
    {
        await _service.UpsertAsync(
            playerId, request.MondayStatus, request.TuesdayStatus, request.Note, ct);
        return NoContent();
    }
}

public record UpdateAvailabilityRequest(int? MondayStatus, int? TuesdayStatus, string? Note);
