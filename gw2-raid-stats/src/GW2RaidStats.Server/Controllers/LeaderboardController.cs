using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LeaderboardController : ControllerBase
{
    private readonly LeaderboardService _leaderboardService;

    public LeaderboardController(LeaderboardService leaderboardService)
    {
        _leaderboardService = leaderboardService;
    }

    /// <summary>
    /// Get list of all bosses with kill counts
    /// </summary>
    [HttpGet("bosses")]
    public async Task<ActionResult<List<BossInfo>>> GetBosses(CancellationToken ct)
    {
        var bosses = await _leaderboardService.GetBossListAsync(ct);
        return Ok(bosses);
    }

    /// <summary>
    /// Get all boss records with top DPS for the leaderboard table
    /// </summary>
    [HttpGet("all")]
    public async Task<ActionResult<List<BossRecord>>> GetAllBossRecords(CancellationToken ct)
    {
        var records = await _leaderboardService.GetAllBossRecordsAsync(ct);
        return Ok(records);
    }

    /// <summary>
    /// Get leaderboard for a specific boss
    /// </summary>
    [HttpGet("boss/{triggerId}")]
    public async Task<ActionResult<BossLeaderboard>> GetBossLeaderboard(
        int triggerId,
        [FromQuery] bool cm = false,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        var leaderboard = await _leaderboardService.GetBossLeaderboardAsync(triggerId, cm, limit, ct);
        return Ok(leaderboard);
    }

    /// <summary>
    /// Get top DPS for a specific boss
    /// </summary>
    [HttpGet("boss/{triggerId}/top-dps")]
    public async Task<ActionResult<List<LeaderboardEntry>>> GetTopDps(
        int triggerId,
        [FromQuery] bool cm = false,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        var entries = await _leaderboardService.GetTopDpsForBossAsync(triggerId, cm, limit, ct);
        return Ok(entries);
    }

    /// <summary>
    /// Get top boon DPS for a specific boss
    /// </summary>
    [HttpGet("boss/{triggerId}/top-boon-dps")]
    public async Task<ActionResult<List<LeaderboardEntry>>> GetTopBoonDps(
        int triggerId,
        [FromQuery] bool cm = false,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        var entries = await _leaderboardService.GetTopBoonDpsForBossAsync(triggerId, cm, limit, ct);
        return Ok(entries);
    }

    /// <summary>
    /// Debug: Get all unique trigger IDs and boss names in the database
    /// </summary>
    [HttpGet("debug/trigger-ids")]
    public async Task<ActionResult<List<TriggerIdInfo>>> GetTriggerIds(CancellationToken ct)
    {
        var triggerIds = await _leaderboardService.GetAllTriggerIdsAsync(ct);
        return Ok(triggerIds);
    }
}
