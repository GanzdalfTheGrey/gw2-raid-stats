using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/logs")]
public class AdminLogsController : ControllerBase
{
    private readonly LogSearchService _logSearchService;

    public AdminLogsController(LogSearchService logSearchService)
    {
        _logSearchService = logSearchService;
    }

    /// <summary>
    /// Delete multiple encounter logs by their IDs
    /// </summary>
    [HttpPost("delete")]
    public async Task<ActionResult<DeleteLogsResult>> DeleteLogs(
        [FromBody] DeleteLogsRequest request,
        CancellationToken ct = default)
    {
        if (request.EncounterIds == null || request.EncounterIds.Count == 0)
        {
            return BadRequest("No encounter IDs provided");
        }

        if (request.EncounterIds.Count > 100)
        {
            return BadRequest("Cannot delete more than 100 logs at once");
        }

        var result = await _logSearchService.DeleteLogsAsync(
            request.EncounterIds,
            request.DeleteFiles,
            ct);

        return Ok(result);
    }

    /// <summary>
    /// Delete a single encounter log by ID
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<DeleteLogsResult>> DeleteLog(
        Guid id,
        [FromQuery] bool deleteFiles = false,
        CancellationToken ct = default)
    {
        var result = await _logSearchService.DeleteLogsAsync(
            new List<Guid> { id },
            deleteFiles,
            ct);

        if (result.EncountersDeleted == 0)
        {
            return NotFound();
        }

        return Ok(result);
    }
}

public record DeleteLogsRequest(
    List<Guid> EncounterIds,
    bool DeleteFiles = false
);
