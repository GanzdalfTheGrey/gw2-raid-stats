using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/recap-fun-stats")]
public class RecapFunStatsController : ControllerBase
{
    private readonly RecapFunStatsService _funStatsService;

    public RecapFunStatsController(RecapFunStatsService funStatsService)
    {
        _funStatsService = funStatsService;
    }

    /// <summary>
    /// Get all available mechanics from the database
    /// </summary>
    [HttpGet("available-mechanics")]
    public async Task<ActionResult<List<AvailableMechanic>>> GetAvailableMechanics(CancellationToken ct)
    {
        var mechanics = await _funStatsService.GetAvailableMechanicsAsync(ct);
        return Ok(mechanics);
    }

    /// <summary>
    /// Get all configured fun stats
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<RecapFunStatDto>>> GetAll(CancellationToken ct)
    {
        var stats = await _funStatsService.GetAllAsync(ct);
        return Ok(stats);
    }

    /// <summary>
    /// Add a new fun stat
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<RecapFunStatDto>> Add(
        [FromBody] AddFunStatRequest request,
        CancellationToken ct)
    {
        var result = await _funStatsService.AddAsync(
            request.MechanicName,
            request.DisplayTitle,
            request.Description,
            request.IsPositive,
            ct);

        return Ok(result);
    }

    /// <summary>
    /// Update a fun stat
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult> Update(
        Guid id,
        [FromBody] UpdateFunStatRequest request,
        CancellationToken ct)
    {
        var updated = await _funStatsService.UpdateAsync(
            id,
            request.DisplayTitle,
            request.Description,
            request.IsPositive,
            request.IsEnabled,
            ct);

        if (!updated)
        {
            return NotFound();
        }

        return NoContent();
    }

    /// <summary>
    /// Update display order
    /// </summary>
    [HttpPut("order")]
    public async Task<ActionResult> UpdateOrder(
        [FromBody] UpdateOrderRequest request,
        CancellationToken ct)
    {
        await _funStatsService.UpdateOrderAsync(request.OrderedIds, ct);
        return NoContent();
    }

    /// <summary>
    /// Delete a fun stat
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await _funStatsService.RemoveAsync(id, ct);
        if (!deleted)
        {
            return NotFound();
        }
        return NoContent();
    }
}

public record AddFunStatRequest(
    string MechanicName,
    string DisplayTitle,
    string? Description,
    bool IsPositive
);

public record UpdateFunStatRequest(
    string DisplayTitle,
    string? Description,
    bool IsPositive,
    bool IsEnabled
);

public record UpdateOrderRequest(List<Guid> OrderedIds);
