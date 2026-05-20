using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using GW2RaidStats.Infrastructure.Services;
using GW2RaidStats.Infrastructure.Services.Import;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/logs")]
public class AdminLogsController : ControllerBase
{
    private readonly LogSearchService _logSearchService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AdminLogsController> _logger;

    // Track rescan status
    private static bool _isRescanning = false;
    private static RescanResult? _lastRescanResult = null;
    private static RescanProgress? _rescanProgress = null;

    public AdminLogsController(
        LogSearchService logSearchService,
        IServiceScopeFactory scopeFactory,
        ILogger<AdminLogsController> logger)
    {
        _logSearchService = logSearchService;
        _scopeFactory = scopeFactory;
        _logger = logger;
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

    /// <summary>
    /// Rescan all stored JSON files and update database records with any missing attributes.
    /// Runs in the background - use GET /api/admin/logs/rescan/status to check progress.
    /// </summary>
    [HttpPost("rescan")]
    public ActionResult StartRescan()
    {
        if (_isRescanning)
        {
            return Conflict(new { message = "Rescan is already in progress" });
        }

        _isRescanning = true;
        _lastRescanResult = null;
        _rescanProgress = null;

        // Run in background with its own scope (so DB context doesn't get disposed)
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Starting background rescan...");

                // Create a new scope for background work
                using var scope = _scopeFactory.CreateScope();
                var rescanService = scope.ServiceProvider.GetRequiredService<RescanService>();

                // Capture per-encounter progress into a static field the status endpoint reads.
                var progress = new Progress<RescanProgress>(p => _rescanProgress = p);
                var result = await rescanService.RescanAllAsync(progress, ct: default);
                _lastRescanResult = result;
                _logger.LogInformation("Rescan complete: {Updated} updated, {Skipped} skipped", result.Updated, result.Skipped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rescan failed");
                _lastRescanResult = new RescanResult(0, 0, 0, new List<string> { ex.Message }, null);
            }
            finally
            {
                _isRescanning = false;
            }
        });

        return Accepted(new { message = "Rescan started. Check /api/admin/logs/rescan/status for progress." });
    }

    /// <summary>
    /// Get the status of the current or last rescan operation
    /// </summary>
    [HttpGet("rescan/status")]
    public ActionResult GetRescanStatus()
    {
        return Ok(new
        {
            isRunning = _isRescanning,
            progress = _rescanProgress,
            result = _lastRescanResult
        });
    }

    /// <summary>
    /// Re-parse encounters by copying their .zevtc files to the pending queue.
    /// The processor will then re-parse them with the current GW2EI version.
    /// </summary>
    [HttpPost("reparse")]
    public async Task<ActionResult<ReparseLogsResult>> ReparseLogs(
        [FromBody] ReparseRequest request,
        CancellationToken ct = default)
    {
        if ((request.EncounterIds == null || request.EncounterIds.Count == 0) &&
            string.IsNullOrWhiteSpace(request.BossName))
        {
            return BadRequest("Must provide either encounter IDs or a boss name");
        }

        var result = await _logSearchService.ReparseLogsAsync(request, ct);

        if (result.EncountersQueued == 0 && result.Errors.Count > 0)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Get encounters that match a boss name (for preview before reparse)
    /// </summary>
    [HttpGet("reparse/preview")]
    public async Task<ActionResult<List<LogEntry>>> GetReparsePreview(
        [FromQuery] string bossName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bossName))
        {
            return BadRequest("Boss name is required");
        }

        var encounters = await _logSearchService.GetEncountersForBossAsync(bossName, ct);
        return Ok(encounters);
    }
}

public record DeleteLogsRequest(
    List<Guid> EncounterIds,
    bool DeleteFiles = false
);
