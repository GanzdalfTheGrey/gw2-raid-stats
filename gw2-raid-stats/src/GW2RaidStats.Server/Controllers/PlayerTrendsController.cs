using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/players")]
public class PlayerTrendsController : ControllerBase
{
    private readonly PlayerTrendsService _trends;

    public PlayerTrendsController(PlayerTrendsService trends)
    {
        _trends = trends;
    }

    /// <summary>
    /// Time-series data for a single player on a single boss in a given role
    /// (DPS / Boon DPS / Heal Boon) over a chosen time range.
    /// </summary>
    [HttpGet("{accountName}/trends")]
    public async Task<ActionResult<PlayerTrendsDto>> GetTrends(
        string accountName,
        [FromQuery] int triggerId,
        [FromQuery] bool isCm = false,
        [FromQuery] string role = "DPS",
        [FromQuery] string range = "all",
        CancellationToken ct = default)
    {
        var dto = await _trends.GetTrendsAsync(accountName, triggerId, isCm, role, range, ct);
        if (dto == null) return NotFound();
        return Ok(dto);
    }

    /// <summary>
    /// List of bosses the player has killed, with kill counts. Used by the trends page
    /// to populate the boss dropdown and default to the most-killed.
    /// </summary>
    [HttpGet("{accountName}/trends/bosses")]
    public async Task<ActionResult<List<BossEncounterCountDto>>> GetBosses(
        string accountName,
        CancellationToken ct = default)
    {
        var list = await _trends.GetBossEncounterCountsAsync(accountName, ct);
        return Ok(list);
    }
}
