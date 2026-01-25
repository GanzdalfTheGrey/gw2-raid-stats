using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/included-players")]
public class IncludedPlayersController : ControllerBase
{
    private readonly IncludedPlayerService _includedPlayerService;
    private readonly SettingsService _settingsService;

    public IncludedPlayersController(
        IncludedPlayerService includedPlayerService,
        SettingsService settingsService)
    {
        _includedPlayerService = includedPlayerService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Get all manually included players
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<IncludedPlayerDto>>> GetAll(CancellationToken ct)
    {
        var players = await _includedPlayerService.GetAllAsync(ct);
        return Ok(players);
    }

    /// <summary>
    /// Get available players that can be included (with encounter counts)
    /// </summary>
    [HttpGet("available")]
    public async Task<ActionResult<List<AvailablePlayerDto>>> GetAvailable(CancellationToken ct)
    {
        var players = await _includedPlayerService.GetAvailablePlayersAsync(ct);
        return Ok(players);
    }

    /// <summary>
    /// Add a player to the included list
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<IncludedPlayerDto>> Add(
        [FromBody] AddIncludedPlayerRequest request,
        CancellationToken ct)
    {
        var result = await _includedPlayerService.AddAsync(
            request.AccountName,
            request.Reason,
            ct);

        return Ok(result);
    }

    /// <summary>
    /// Remove a player from the included list
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Remove(Guid id, CancellationToken ct)
    {
        var removed = await _includedPlayerService.RemoveAsync(id, ct);
        if (!removed)
        {
            return NotFound();
        }
        return NoContent();
    }

    /// <summary>
    /// Get the auto-include threshold setting
    /// </summary>
    [HttpGet("threshold")]
    public async Task<ActionResult<ThresholdResponse>> GetThreshold(CancellationToken ct)
    {
        var threshold = await _settingsService.GetAutoIncludeThresholdAsync(ct);
        return Ok(new ThresholdResponse(threshold));
    }

    /// <summary>
    /// Update the auto-include threshold setting
    /// </summary>
    [HttpPut("threshold")]
    public async Task<ActionResult> UpdateThreshold(
        [FromBody] UpdateThresholdRequest request,
        CancellationToken ct)
    {
        if (request.Threshold < 0)
        {
            return BadRequest("Threshold must be non-negative");
        }

        await _settingsService.SetAutoIncludeThresholdAsync(request.Threshold, ct);
        return NoContent();
    }

    /// <summary>
    /// Get the recap include all bosses setting
    /// </summary>
    [HttpGet("recap-include-all-bosses")]
    public async Task<ActionResult<RecapBossesResponse>> GetRecapIncludeAllBosses(CancellationToken ct)
    {
        var includeAll = await _settingsService.GetRecapIncludeAllBossesAsync(ct);
        return Ok(new RecapBossesResponse(includeAll));
    }

    /// <summary>
    /// Update the recap include all bosses setting
    /// </summary>
    [HttpPut("recap-include-all-bosses")]
    public async Task<ActionResult> UpdateRecapIncludeAllBosses(
        [FromBody] UpdateRecapBossesRequest request,
        CancellationToken ct)
    {
        await _settingsService.SetRecapIncludeAllBossesAsync(request.IncludeAll, ct);
        return NoContent();
    }
}

public record AddIncludedPlayerRequest(
    string AccountName,
    string? Reason
);

public record ThresholdResponse(int Threshold);

public record UpdateThresholdRequest(int Threshold);

public record RecapBossesResponse(bool IncludeAll);

public record UpdateRecapBossesRequest(bool IncludeAll);
