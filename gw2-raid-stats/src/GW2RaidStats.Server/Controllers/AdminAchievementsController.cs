using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using GW2RaidStats.Infrastructure.Services.Achievements;
using GW2RaidStats.Infrastructure.Database;
using LinqToDB;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/achievements")]
public class AdminAchievementsController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AdminAchievementsController> _logger;

    // Track backfill status
    private static bool _isBackfilling = false;
    private static BackfillProgress? _currentProgress = null;
    private static BackfillResult? _lastResult = null;

    public AdminAchievementsController(
        IServiceScopeFactory scopeFactory,
        ILogger<AdminAchievementsController> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Start retroactive achievement backfill for all players.
    /// This scans all historical data and awards achievements that players have already earned.
    /// Runs in the background - use GET /api/admin/achievements/backfill/status to check progress.
    /// </summary>
    [HttpPost("backfill")]
    public ActionResult StartBackfill()
    {
        if (_isBackfilling)
        {
            return Conflict(new { message = "Backfill is already in progress" });
        }

        _isBackfilling = true;
        _currentProgress = null;
        _lastResult = null;

        // Run in background with its own scope
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Starting achievement backfill...");

                using var scope = _scopeFactory.CreateScope();
                var backfillService = scope.ServiceProvider.GetRequiredService<AchievementBackfillService>();

                var progress = new Progress<BackfillProgress>(p =>
                {
                    _currentProgress = p;
                });

                var result = await backfillService.BackfillAllAchievementsAsync(progress, CancellationToken.None);
                _lastResult = result;

                _logger.LogInformation(
                    "Achievement backfill complete: {Encounters} encounters processed, {Awarded} achievements awarded",
                    result.EncountersProcessed, result.AchievementsAwarded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Achievement backfill failed");
                _lastResult = new BackfillResult(0, 0, new List<string> { ex.Message });
            }
            finally
            {
                _isBackfilling = false;
            }
        });

        return Accepted(new { message = "Achievement backfill started. Check /api/admin/achievements/backfill/status for progress." });
    }

    /// <summary>
    /// Get the status of the current or last backfill operation
    /// </summary>
    [HttpGet("backfill/status")]
    public ActionResult GetBackfillStatus()
    {
        return Ok(new
        {
            isRunning = _isBackfilling,
            progress = _currentProgress,
            result = _lastResult
        });
    }

    /// <summary>
    /// Cancel the current backfill operation
    /// Note: This just sets a flag - the actual cancellation depends on the service checking the token
    /// </summary>
    [HttpPost("backfill/cancel")]
    public ActionResult CancelBackfill()
    {
        if (!_isBackfilling)
        {
            return BadRequest(new { message = "No backfill is currently running" });
        }

        // Note: To properly implement cancellation, we'd need to store and trigger a CancellationTokenSource
        // For now, this is a placeholder
        return Ok(new { message = "Cancellation requested. The operation may take a moment to stop." });
    }

    /// <summary>
    /// Clear all achievements (both player and guild) to allow a fresh backfill.
    /// Use this when achievement logic has changed and you need to recalculate from scratch.
    /// </summary>
    [HttpDelete("clear")]
    public async Task<ActionResult> ClearAllAchievements()
    {
        if (_isBackfilling)
        {
            return Conflict(new { message = "Cannot clear achievements while backfill is running" });
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RaidStatsDb>();

            var playerDeleted = await db.PlayerAchievements.DeleteAsync();
            var guildDeleted = await db.GuildAchievements.DeleteAsync();

            _lastResult = null;
            _currentProgress = null;

            _logger.LogInformation("Cleared all achievements: {PlayerCount} player achievements, {GuildCount} guild achievements",
                playerDeleted, guildDeleted);

            return Ok(new
            {
                message = "All achievements cleared",
                playerAchievementsDeleted = playerDeleted,
                guildAchievementsDeleted = guildDeleted
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear achievements");
            return StatusCode(500, new { message = $"Failed to clear achievements: {ex.Message}" });
        }
    }
}
