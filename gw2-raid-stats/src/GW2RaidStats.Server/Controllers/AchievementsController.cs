using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Services;
using GW2RaidStats.Infrastructure.Services.Achievements;
using LinqToDB;
using LinqToDB.Async;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/achievements")]
public class AchievementsController : ControllerBase
{
    private readonly AchievementQueryService _queryService;
    private readonly IncludedPlayerService _includedPlayerService;
    private readonly RaidStatsDb _db;

    public AchievementsController(
        AchievementQueryService queryService,
        IncludedPlayerService includedPlayerService,
        RaidStatsDb db)
    {
        _queryService = queryService;
        _includedPlayerService = includedPlayerService;
        _db = db;
    }

    /// <summary>
    /// Get all achievement definitions
    /// </summary>
    [HttpGet("definitions")]
    public ActionResult<AchievementDefinitionsResponse> GetDefinitions()
    {
        var personal = AchievementDefinitions.Personal
            .Select(a => new AchievementDefinitionDto(
                a.Code,
                a.Name,
                a.Description,
                a.Category.ToString()))
            .ToList();

        var guild = AchievementDefinitions.Guild
            .Select(a => new AchievementDefinitionDto(
                a.Code,
                a.Name,
                a.Description,
                a.Category.ToString()))
            .ToList();

        return Ok(new AchievementDefinitionsResponse(personal, guild));
    }

    /// <summary>
    /// Get achievements for a player by account name
    /// </summary>
    [HttpGet("player/{accountName}")]
    public async Task<ActionResult<PlayerAchievementsResponse>> GetPlayerAchievements(
        string accountName,
        CancellationToken ct)
    {
        // Find player by account name
        var player = await _db.Players
            .FirstOrDefaultAsync(p => p.AccountName == accountName, ct);

        if (player == null)
            return NotFound(new { message = "Player not found" });

        var earned = await _queryService.GetPlayerAchievementsAsync(player.Id, ct);
        var progress = await _queryService.GetProgressAsync(player.Id, ct);

        return Ok(new PlayerAchievementsResponse(
            earned.Count,
            AchievementDefinitions.Personal.Count,
            earned,
            progress));
    }

    /// <summary>
    /// Get achievement progress for a player by account name
    /// </summary>
    [HttpGet("player/{accountName}/progress")]
    public async Task<ActionResult<List<AchievementProgressDto>>> GetProgress(
        string accountName,
        CancellationToken ct)
    {
        var player = await _db.Players
            .FirstOrDefaultAsync(p => p.AccountName == accountName, ct);

        if (player == null)
            return NotFound(new { message = "Player not found" });

        var progress = await _queryService.GetProgressAsync(player.Id, ct);
        return Ok(progress);
    }

    /// <summary>
    /// Get detailed Wing Master progress for a player showing missing boss/role combos
    /// </summary>
    [HttpGet("player/{accountName}/wing-master")]
    public async Task<ActionResult<List<WingMasterDetailedProgressDto>>> GetWingMasterProgress(
        string accountName,
        CancellationToken ct)
    {
        var player = await _db.Players
            .FirstOrDefaultAsync(p => p.AccountName == accountName, ct);

        if (player == null)
            return NotFound(new { message = "Player not found" });

        var progress = await _queryService.GetWingMasterDetailedProgressAsync(player.Id, ct);
        return Ok(progress);
    }

    /// <summary>
    /// Get detailed completion progress for a player showing missing bosses
    /// </summary>
    [HttpGet("player/{accountName}/completion")]
    public async Task<ActionResult<List<CompletionDetailedProgressDto>>> GetCompletionProgress(
        string accountName,
        CancellationToken ct)
    {
        var player = await _db.Players
            .FirstOrDefaultAsync(p => p.AccountName == accountName, ct);

        if (player == null)
            return NotFound(new { message = "Player not found" });

        var progress = await _queryService.GetCompletionDetailedProgressAsync(player.Id, ct);
        return Ok(progress);
    }

    /// <summary>
    /// Get all guild achievements
    /// </summary>
    [HttpGet("guild")]
    public async Task<ActionResult<List<GuildAchievementDto>>> GetGuildAchievements(CancellationToken ct)
    {
        var achievements = await _queryService.GetGuildAchievementsAsync(ct);
        return Ok(achievements);
    }

    /// <summary>
    /// Get achievement leaderboard (players with most achievements)
    /// </summary>
    [HttpGet("leaderboard")]
    public async Task<ActionResult<List<AchievementLeaderboardEntry>>> GetLeaderboard(
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var leaderboard = await _db.PlayerAchievements
            .InnerJoin(_db.Players, (pa, p) => pa.PlayerId == p.Id, (pa, p) => new { pa, p })
            .GroupBy(x => new { x.p.Id, x.p.AccountName })
            .Select(g => new AchievementLeaderboardEntry(
                g.Key.AccountName,
                g.Count(),
                g.Max(x => x.pa.AchievedAt)))
            .OrderByDescending(x => x.AchievementCount)
            .ThenByDescending(x => x.LatestAchievement)
            .Take(limit)
            .ToListAsync(ct);

        return Ok(leaderboard);
    }

    /// <summary>
    /// Get statistics about achievements
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<AchievementStatsResponse>> GetStats(CancellationToken ct)
    {
        var totalPersonal = AchievementDefinitions.Personal.Count;
        var totalGuild = AchievementDefinitions.Guild.Count;

        var earnedPersonal = await _db.PlayerAchievements
            .Select(pa => pa.AchievementCode)
            .Distinct()
            .CountAsync(ct);

        var earnedGuild = await _db.GuildAchievements
            .CountAsync(ct);

        var totalPlayers = await _db.PlayerAchievements
            .Select(pa => pa.PlayerId)
            .Distinct()
            .CountAsync(ct);

        var totalAchievementsAwarded = await _db.PlayerAchievements.CountAsync(ct);

        // Get rarest achievements
        var rarestPersonal = await _db.PlayerAchievements
            .GroupBy(pa => pa.AchievementCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .OrderBy(x => x.Count)
            .Take(5)
            .ToListAsync(ct);

        var rarest = rarestPersonal.Select(r =>
        {
            var def = AchievementDefinitions.Personal.FirstOrDefault(d => d.Code == r.Code);
            return new RarestAchievementDto(
                r.Code,
                def?.Name ?? r.Code,
                r.Count);
        }).ToList();

        return Ok(new AchievementStatsResponse(
            totalPersonal,
            totalGuild,
            earnedPersonal,
            earnedGuild,
            totalPlayers,
            totalAchievementsAwarded,
            rarest));
    }

    /// <summary>
    /// Get rarest personal achievements with player counts
    /// </summary>
    [HttpGet("rarest")]
    public async Task<ActionResult<RarestAchievementsResponse>> GetRarestAchievements(
        [FromQuery] int limit = 3,
        CancellationToken ct = default)
    {
        // Get total included players (guild members + auto-included)
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var totalIncludedPlayers = includedAccounts.Count;

        // Get achievement counts
        var achievementCounts = await _db.PlayerAchievements
            .GroupBy(pa => pa.AchievementCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var countDict = achievementCounts.ToDictionary(x => x.Code, x => x.Count);

        // Build list of all achievements with their earned counts (rarest first)
        var rarest = AchievementDefinitions.Personal
            .Select(def => new RarestPersonalAchievementDto(
                def.Code,
                def.Name,
                def.Description,
                countDict.GetValueOrDefault(def.Code, 0),
                totalIncludedPlayers))
            .Where(a => a.EarnedCount > 0) // Only show achievements that someone has earned
            .OrderBy(a => a.EarnedCount)
            .Take(limit)
            .ToList();

        return Ok(new RarestAchievementsResponse(rarest, totalIncludedPlayers));
    }
}

// Response DTOs
public record AchievementDefinitionsResponse(
    List<AchievementDefinitionDto> Personal,
    List<AchievementDefinitionDto> Guild);

public record AchievementDefinitionDto(
    string Code,
    string Name,
    string Description,
    string Category);

public record PlayerAchievementsResponse(
    int TotalEarned,
    int TotalAvailable,
    List<PlayerAchievementDto> Earned,
    List<AchievementProgressDto> InProgress);

public record AchievementLeaderboardEntry(
    string AccountName,
    int AchievementCount,
    DateTimeOffset LatestAchievement);

public record AchievementStatsResponse(
    int TotalPersonal,
    int TotalGuild,
    int EarnedPersonalUnique,
    int EarnedGuild,
    int PlayersWithAchievements,
    int TotalAchievementsAwarded,
    List<RarestAchievementDto> RarestAchievements);

public record RarestAchievementDto(
    string Code,
    string Name,
    int EarnedCount);

public record RarestAchievementsResponse(
    List<RarestPersonalAchievementDto> Rarest,
    int TotalIncludedPlayers);

public record RarestPersonalAchievementDto(
    string Code,
    string Name,
    string Description,
    int EarnedCount,
    int TotalPlayers);
