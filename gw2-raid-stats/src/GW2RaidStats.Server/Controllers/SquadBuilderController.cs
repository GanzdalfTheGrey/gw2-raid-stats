using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Core.Roles;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/squad")]
public class SquadBuilderController : ControllerBase
{
    private readonly SquadRandomizerService _randomizer;
    private readonly RaidStatsDb _db;

    public SquadBuilderController(SquadRandomizerService randomizer, RaidStatsDb db)
    {
        _randomizer = randomizer;
        _db = db;
    }

    /// <summary>
    /// Build (or re-build) a squad composition. Pass Locks to pin certain players to roles
    /// for re-randomize. Pass a subset of BossTriggerIds to compute a tail segment after a
    /// reset prompt.
    /// </summary>
    [HttpPost("build")]
    public async Task<ActionResult<SquadBuildResult>> Build(
        [FromBody] SquadBuildRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await _randomizer.BuildAsync(request, ct);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Enqueue a Discord notification posting the supplied squad composition.
    /// </summary>
    [HttpPost("publish")]
    public async Task<ActionResult> Publish(
        [FromBody] SquadPublishPayload payload,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await _db.InsertAsync(new NotificationQueueEntity
        {
            Id = Guid.NewGuid(),
            NotificationType = "squad_composition",
            Payload = json,
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessedAt = null
        }, token: ct);

        return Accepted();
    }
}

/// <summary>
/// Payload posted from the Squad Builder to be picked up by the Discord bot.
/// Self-contained so the bot's handler can build the embed without re-fetching squad state.
/// </summary>
public record SquadPublishPayload(
    string Title,
    string BossesText,
    List<SquadPublishSubGroup> SubGroups,
    int PugDpsCount,
    List<SquadPublishBoss> PerBoss,
    List<string> Warnings,
    List<SquadPublishSwap> Swaps,
    string? CommanderName);

public record SquadPublishSubGroup(
    int Index,
    List<SquadPublishSlot> Slots);

public record SquadPublishSlot(
    string Kind,
    string? Role,
    Guid? PlayerId,
    string? AccountName,
    bool IsPug);

public record SquadPublishBoss(
    string BossName,
    List<SquadPublishMechanic> Mechanics,
    bool IsResetSegment);

public record SquadPublishMechanic(
    string Name,
    List<SquadPublishMechanicSlot> AssignedPlayers);

public record SquadPublishMechanicSlot(
    Guid? PlayerId,
    string? AccountName);

public record SquadPublishSwap(
    string FromBossName,
    List<SquadPublishSwapEntry> Entries);

public record SquadPublishSwapEntry(
    string AccountName,
    string? FromRole,
    string? ToRole);
