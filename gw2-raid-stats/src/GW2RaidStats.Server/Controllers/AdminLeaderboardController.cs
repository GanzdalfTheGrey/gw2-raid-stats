using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/leaderboard")]
public class AdminLeaderboardController : ControllerBase
{
    private readonly RaidStatsDb _db;

    public AdminLeaderboardController(RaidStatsDb db)
    {
        _db = db;
    }

    [HttpGet("patches")]
    public async Task<ActionResult<List<LeaderboardPatchDto>>> GetPatches(CancellationToken ct)
    {
        var patches = await _db.LeaderboardPatches
            .OrderByDescending(p => p.StartDate)
            .Select(p => new LeaderboardPatchDto(p.Id, p.Name, p.StartDate))
            .ToListAsync(ct);
        return Ok(patches);
    }

    [HttpPost("patches")]
    public async Task<IActionResult> CreatePatch([FromBody] CreatePatchRequest request, CancellationToken ct)
    {
        var entity = new LeaderboardPatchEntity
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            StartDate = request.StartDate,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(entity, token: ct);

        return Ok(new { success = true, id = entity.Id });
    }

    [HttpDelete("patches/{id:guid}")]
    public async Task<IActionResult> DeletePatch(Guid id, CancellationToken ct)
    {
        var deleted = await _db.LeaderboardPatches
            .Where(p => p.Id == id)
            .DeleteAsync(ct);

        if (deleted == 0)
            return NotFound();

        return Ok(new { success = true });
    }
}

public record CreatePatchRequest(string Name, DateTimeOffset StartDate);
