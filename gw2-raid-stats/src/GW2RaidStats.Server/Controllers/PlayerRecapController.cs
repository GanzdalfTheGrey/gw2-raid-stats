using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/player-recap")]
public class PlayerRecapController : ControllerBase
{
    private readonly PlayerRecapService _playerRecapService;

    public PlayerRecapController(PlayerRecapService playerRecapService)
    {
        _playerRecapService = playerRecapService;
    }

    /// <summary>
    /// Get available years for a player's recap
    /// </summary>
    [HttpGet("{playerId:guid}/years")]
    public async Task<ActionResult<List<int>>> GetAvailableYears(Guid playerId, CancellationToken ct)
    {
        var years = await _playerRecapService.GetAvailableYearsAsync(playerId, ct);
        return Ok(years);
    }

    /// <summary>
    /// Get yearly recap data for a specific player
    /// </summary>
    [HttpGet("{playerId:guid}/{year}")]
    public async Task<ActionResult<PlayerYearlyRecap>> GetPlayerYearlyRecap(
        Guid playerId,
        int year,
        [FromQuery] bool includeIgnoredBosses = false,
        CancellationToken ct = default)
    {
        var recap = await _playerRecapService.GetPlayerYearlyRecapAsync(playerId, year, includeIgnoredBosses, ct);

        if (recap == null)
            return NotFound();

        return Ok(recap);
    }
}
