using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using GW2RaidStats.Infrastructure.Services.Achievements;
using GW2RaidStats.Infrastructure.Database;
using LinqToDB;
using LinqToDB.Async;
using System.Text.Json;

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

    /// <summary>
    /// Get all guild achievements with their award status
    /// </summary>
    [HttpGet("guild")]
    public async Task<ActionResult<List<GuildAchievementListItem>>> GetGuildAchievements()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RaidStatsDb>();

            // Get all awarded guild achievements
            var awarded = await db.GuildAchievements
                .Select(ga => new { ga.AchievementCode, ga.CompletionCount, ga.AchievedAt })
                .ToListAsync();

            var awardedDict = awarded.ToDictionary(a => a.AchievementCode, a => (a.CompletionCount, a.AchievedAt));

            // Build list of all guild achievements
            var result = AchievementDefinitions.Guild
                .Select(def => new GuildAchievementListItem(
                    def.Code,
                    def.Name,
                    def.Description,
                    def.Category.ToString(),
                    awardedDict.ContainsKey(def.Code),
                    awardedDict.TryGetValue(def.Code, out var info) ? info.CompletionCount : 0,
                    awardedDict.TryGetValue(def.Code, out var info2) ? info2.AchievedAt : null
                ))
                .OrderBy(a => a.Category)
                .ThenBy(a => a.Name)
                .ToList();

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get guild achievements");
            return StatusCode(500, new { message = $"Failed to get guild achievements: {ex.Message}" });
        }
    }

    /// <summary>
    /// Manually award a guild achievement with Discord notification
    /// </summary>
    [HttpPost("guild/{code}/award")]
    public async Task<ActionResult> AwardGuildAchievement(string code, [FromBody] ManualAwardRequest? request)
    {
        try
        {
            // Verify achievement exists
            var definition = AchievementDefinitions.Guild.FirstOrDefault(a => a.Code == code);
            if (definition == null)
            {
                return NotFound(new { message = $"Guild achievement '{code}' not found" });
            }

            using var scope = _scopeFactory.CreateScope();
            var awardService = scope.ServiceProvider.GetRequiredService<AchievementAwardService>();

            // Build context for the manual award
            var context = new
            {
                manual_award = true,
                reason = request?.Reason ?? "Manually awarded by admin",
                awarded_at = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
            };

            // Award with notification enabled
            await awardService.AwardGuildAchievementAsync(
                code,
                context,
                notify: true,
                CancellationToken.None,
                achievedAt: DateTimeOffset.UtcNow);

            _logger.LogInformation("Manually awarded guild achievement {Code} by admin. Reason: {Reason}",
                code, request?.Reason ?? "No reason provided");

            return Ok(new
            {
                message = $"Guild achievement '{definition.Name}' awarded successfully",
                code = code,
                name = definition.Name,
                notificationSent = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to award guild achievement {Code}", code);
            return StatusCode(500, new { message = $"Failed to award achievement: {ex.Message}" });
        }
    }

    /// <summary>
    /// Remove a guild achievement
    /// </summary>
    [HttpDelete("guild/{code}")]
    public async Task<ActionResult> RemoveGuildAchievement(string code)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RaidStatsDb>();

            var deleted = await db.GuildAchievements
                .Where(ga => ga.AchievementCode == code)
                .DeleteAsync();

            if (deleted == 0)
            {
                return NotFound(new { message = $"Guild achievement '{code}' was not awarded" });
            }

            _logger.LogInformation("Removed guild achievement {Code}", code);

            return Ok(new { message = $"Guild achievement '{code}' removed", deleted });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove guild achievement {Code}", code);
            return StatusCode(500, new { message = $"Failed to remove achievement: {ex.Message}" });
        }
    }
}

public record GuildAchievementListItem(
    string Code,
    string Name,
    string Description,
    string Category,
    bool IsAwarded,
    int CompletionCount,
    DateTimeOffset? AchievedAt);

public record ManualAwardRequest(string? Reason);
