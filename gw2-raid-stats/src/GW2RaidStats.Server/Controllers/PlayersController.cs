using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/players")]
public class PlayersController : ControllerBase
{
    private readonly PlayerProfileService _profileService;

    public PlayersController(PlayerProfileService profileService)
    {
        _profileService = profileService;
    }

    /// <summary>
    /// Get all players sorted by encounter count
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<PlayerSearchResult>>> GetAll(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var players = await _profileService.GetAllPlayersAsync(limit, offset, ct);
        return Ok(players);
    }

    /// <summary>
    /// Search for players by account name
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<List<PlayerSearchResult>>> Search(
        [FromQuery] string? q,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var players = await _profileService.SearchPlayersAsync(q, limit, ct);
        return Ok(players);
    }

    /// <summary>
    /// Get a player's profile by account name
    /// </summary>
    [HttpGet("{accountName}")]
    public async Task<ActionResult<PlayerProfile>> GetProfile(string accountName, CancellationToken ct)
    {
        var profile = await _profileService.GetProfileAsync(accountName, ct);
        if (profile == null)
        {
            return NotFound();
        }
        return Ok(profile);
    }
}
