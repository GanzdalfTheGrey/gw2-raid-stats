using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/ignored-bosses")]
public class IgnoredBossesController : ControllerBase
{
    private readonly IgnoredBossService _ignoredBossService;

    public IgnoredBossesController(IgnoredBossService ignoredBossService)
    {
        _ignoredBossService = ignoredBossService;
    }

    /// <summary>
    /// Get all ignored bosses
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<IgnoredBossDto>>> GetAll(CancellationToken ct)
    {
        var bosses = await _ignoredBossService.GetAllAsync(ct);
        return Ok(bosses);
    }

    /// <summary>
    /// Get available bosses that can be ignored
    /// </summary>
    [HttpGet("available")]
    public async Task<ActionResult<List<AvailableBossDto>>> GetAvailable(CancellationToken ct)
    {
        var bosses = await _ignoredBossService.GetAvailableBossesAsync(ct);
        return Ok(bosses);
    }

    /// <summary>
    /// Add a boss to the ignored list
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<IgnoredBossDto>> Add(
        [FromBody] AddIgnoredBossRequest request,
        CancellationToken ct)
    {
        var result = await _ignoredBossService.AddAsync(
            request.TriggerId,
            request.BossName,
            request.IsCM,
            request.Reason,
            ct);

        return Ok(result);
    }

    /// <summary>
    /// Remove a boss from the ignored list
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Remove(Guid id, CancellationToken ct)
    {
        var removed = await _ignoredBossService.RemoveAsync(id, ct);
        if (!removed)
        {
            return NotFound();
        }
        return NoContent();
    }
}

public record AddIgnoredBossRequest(
    int TriggerId,
    string BossName,
    bool IsCM,
    string? Reason
);
