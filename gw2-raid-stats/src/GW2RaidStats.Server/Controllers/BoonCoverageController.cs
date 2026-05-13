using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/boon-coverage")]
public class BoonCoverageController : ControllerBase
{
    private readonly BoonCoverageService _service;

    public BoonCoverageController(BoonCoverageService service)
    {
        _service = service;
    }

    // POST so we can accept arbitrarily-long encounter ID lists without query-string limits.
    [HttpPost("encounters")]
    public async Task<ActionResult<List<EncounterBoonCoverage>>> GetEncounterCoverage(
        [FromBody] EncounterCoverageRequest request,
        CancellationToken ct)
    {
        if (request.EncounterIds is null || request.EncounterIds.Count == 0)
            return Ok(new List<EncounterBoonCoverage>());

        var result = await _service.GetCoverageForEncountersAsync(request.EncounterIds, ct);
        return Ok(result);
    }

    [HttpGet("player/{playerId:guid}")]
    public async Task<ActionResult<PlayerBoonCoverage>> GetPlayerCoverage(
        Guid playerId,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var result = await _service.GetPlayerCoverageAsync(playerId, from, to, ct);
        return Ok(result);
    }

    [HttpGet("player/{playerId:guid}/trends")]
    public async Task<ActionResult<List<BoonTrendBucket>>> GetPlayerTrends(
        Guid playerId,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var result = await _service.GetPlayerTrendsAsync(playerId, from, to, ct);
        return Ok(result);
    }
}

public record EncounterCoverageRequest(List<Guid> EncounterIds);
