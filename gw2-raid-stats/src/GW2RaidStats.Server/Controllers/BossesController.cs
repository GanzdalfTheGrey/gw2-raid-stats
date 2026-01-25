using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/bosses")]
public class BossesController : ControllerBase
{
    private readonly BossStatsService _bossStatsService;

    public BossesController(BossStatsService bossStatsService)
    {
        _bossStatsService = bossStatsService;
    }

    /// <summary>
    /// Get all bosses with summary stats
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<BossSummary>>> GetAllBosses(
        [FromQuery] bool includeIgnored = false,
        CancellationToken ct = default)
    {
        var bosses = await _bossStatsService.GetAllBossesAsync(includeIgnored, ct);
        return Ok(bosses);
    }

    /// <summary>
    /// Get detailed stats for a specific boss
    /// </summary>
    [HttpGet("{triggerId:int}")]
    public async Task<ActionResult<BossDetail>> GetBossDetail(
        int triggerId,
        [FromQuery] bool isCM = false,
        CancellationToken ct = default)
    {
        var detail = await _bossStatsService.GetBossDetailAsync(triggerId, isCM, ct);
        if (detail == null)
        {
            return NotFound();
        }
        return Ok(detail);
    }
}
