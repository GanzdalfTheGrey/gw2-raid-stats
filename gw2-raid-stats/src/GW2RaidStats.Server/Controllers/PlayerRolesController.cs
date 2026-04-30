using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Core.Roles;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api")]
public class PlayerRolesController : ControllerBase
{
    private readonly PlayerRolesService _service;

    public PlayerRolesController(PlayerRolesService service)
    {
        _service = service;
    }

    [HttpGet("players/{accountName}/roles")]
    public async Task<ActionResult<PlayerRoleCapabilitiesDto>> GetForPlayer(
        string accountName,
        CancellationToken ct)
    {
        var playerId = await _service.ResolvePlayerIdAsync(accountName, ct);
        if (playerId == null) return NotFound();

        var dto = await _service.GetForPlayerAsync(playerId.Value, ct);
        return dto == null ? NotFound() : Ok(dto);
    }

    [HttpGet("roles/matrix")]
    public async Task<ActionResult<RolesMatrixDto>> GetMatrix(CancellationToken ct)
    {
        return Ok(await _service.GetMatrixAsync(ct));
    }
}

[ApiController]
[Route("api/admin/players")]
public class AdminPlayerRolesController : ControllerBase
{
    private readonly PlayerRolesService _service;

    public AdminPlayerRolesController(PlayerRolesService service)
    {
        _service = service;
    }

    [HttpPut("{accountName}/roles/generic/{role}")]
    public async Task<ActionResult> SetGeneric(
        string accountName,
        GenericRole role,
        [FromBody] SetRoleStatusRequest request,
        CancellationToken ct)
    {
        var playerId = await _service.ResolvePlayerIdAsync(accountName, ct);
        if (playerId == null) return NotFound();

        await _service.SetGenericAsync(playerId.Value, role, request.Status, request.Notes, ct);
        return NoContent();
    }

    [HttpPut("{accountName}/roles/mechanic/{mechanicRoleId:guid}")]
    public async Task<ActionResult> SetMechanic(
        string accountName,
        Guid mechanicRoleId,
        [FromBody] SetRoleStatusRequest request,
        CancellationToken ct)
    {
        var playerId = await _service.ResolvePlayerIdAsync(accountName, ct);
        if (playerId == null) return NotFound();

        await _service.SetMechanicAsync(playerId.Value, mechanicRoleId, request.Status, request.Notes, ct);
        return NoContent();
    }
}

public record SetRoleStatusRequest(RoleCapabilityStatus? Status, string? Notes);
