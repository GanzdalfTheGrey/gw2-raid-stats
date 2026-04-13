using Microsoft.AspNetCore.Mvc;
using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LeaderboardController : ControllerBase
{
    private readonly LeaderboardService _leaderboardService;
    private readonly RaidStatsDb _db;

    public LeaderboardController(LeaderboardService leaderboardService, RaidStatsDb db)
    {
        _leaderboardService = leaderboardService;
        _db = db;
    }

    /// <summary>
    /// Get list of all bosses with kill counts
    /// </summary>
    [HttpGet("bosses")]
    public async Task<ActionResult<List<BossInfo>>> GetBosses(
        [FromQuery] DateTimeOffset? fromDate = null,
        CancellationToken ct = default)
    {
        var bosses = await _leaderboardService.GetBossListAsync(fromDate, ct);
        return Ok(bosses);
    }

    /// <summary>
    /// Get all boss records with top DPS for the leaderboard table
    /// </summary>
    [HttpGet("all")]
    public async Task<ActionResult<List<BossRecord>>> GetAllBossRecords(
        [FromQuery] DateTimeOffset? fromDate = null,
        CancellationToken ct = default)
    {
        var records = await _leaderboardService.GetAllBossRecordsAsync(fromDate, ct);
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
        [FromQuery] DateTimeOffset? fromDate = null,
        CancellationToken ct = default)
    {
        var leaderboard = await _leaderboardService.GetBossLeaderboardAsync(triggerId, cm, limit, fromDate, ct);
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
        [FromQuery] DateTimeOffset? fromDate = null,
        CancellationToken ct = default)
    {
        var entries = await _leaderboardService.GetTopDpsForBossAsync(triggerId, cm, limit, fromDate, ct);
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
        [FromQuery] DateTimeOffset? fromDate = null,
        CancellationToken ct = default)
    {
        var entries = await _leaderboardService.GetTopBoonDpsForBossAsync(triggerId, cm, limit, fromDate, ct);
        return Ok(entries);
    }

    /// <summary>
    /// Get leaderboard patch dates for the patch selector
    /// </summary>
    [HttpGet("patches")]
    public async Task<ActionResult<List<LeaderboardPatchDto>>> GetPatches(CancellationToken ct)
    {
        var patches = await _db.LeaderboardPatches
            .OrderByDescending(p => p.StartDate)
            .Select(p => new LeaderboardPatchDto(p.Id, p.Name, p.StartDate))
            .ToListAsync(ct);
        return Ok(patches);
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

public record LeaderboardPatchDto(Guid Id, string Name, DateTimeOffset StartDate);
