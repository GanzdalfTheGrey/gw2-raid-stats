using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/logs")]
public class LogsController : ControllerBase
{
    private readonly LogSearchService _logSearchService;

    public LogsController(LogSearchService logSearchService)
    {
        _logSearchService = logSearchService;
    }

    /// <summary>
    /// Search and filter encounter logs
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<LogSearchResult>> SearchLogs(
        [FromQuery] string? bossName = null,
        [FromQuery] int? triggerId = null,
        [FromQuery] int? wing = null,
        [FromQuery] bool? isCM = null,
        [FromQuery] bool? success = null,
        [FromQuery] DateTimeOffset? fromDate = null,
        [FromQuery] DateTimeOffset? toDate = null,
        [FromQuery] string? recordedBy = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool sortDescending = true,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        // Clamp page size to reasonable limits
        pageSize = Math.Clamp(pageSize, 10, 100);

        var request = new LogSearchRequest(
            bossName,
            triggerId,
            wing,
            isCM,
            success,
            fromDate,
            toDate,
            recordedBy,
            sortBy,
            sortDescending,
            page,
            pageSize
        );

        var result = await _logSearchService.SearchLogsAsync(request, ct);
        return Ok(result);
    }

    /// <summary>
    /// Get list of unique boss names for filter dropdown
    /// </summary>
    [HttpGet("boss-names")]
    public async Task<ActionResult<List<string>>> GetBossNames(CancellationToken ct = default)
    {
        var names = await _logSearchService.GetUniqueBossNamesAsync(ct);
        return Ok(names);
    }

    /// <summary>
    /// Get list of unique wings for filter dropdown
    /// </summary>
    [HttpGet("wings")]
    public async Task<ActionResult<List<int>>> GetWings(CancellationToken ct = default)
    {
        var wings = await _logSearchService.GetUniqueWingsAsync(ct);
        return Ok(wings);
    }
}
