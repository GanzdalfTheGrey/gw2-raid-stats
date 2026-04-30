using Microsoft.AspNetCore.Mvc;
using GW2RaidStats.Core.Roles;
using GW2RaidStats.Infrastructure.Services;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/mechanic-roles")]
public class MechanicRolesController : ControllerBase
{
    private readonly MechanicRoleCatalogService _catalog;

    public MechanicRolesController(MechanicRoleCatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpGet]
    public async Task<ActionResult<List<MechanicRoleDto>>> GetAll(CancellationToken ct)
    {
        return Ok(await _catalog.GetAllAsync(ct));
    }
}

[ApiController]
[Route("api/admin/mechanic-roles")]
public class AdminMechanicRolesController : ControllerBase
{
    private readonly MechanicRoleCatalogService _catalog;

    public AdminMechanicRolesController(MechanicRoleCatalogService catalog)
    {
        _catalog = catalog;
    }

    [HttpPost]
    public async Task<ActionResult<MechanicRoleDto>> Add(
        [FromBody] AddMechanicRoleRequest request,
        CancellationToken ct)
    {
        try
        {
            var dto = await _catalog.AddAsync(
                request.TriggerId,
                request.BossName,
                request.Name,
                request.Constraint,
                request.MinCount,
                request.MaxCount,
                request.SortOrder,
                ct);
            return Ok(dto);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<MechanicRoleDto>> Update(
        Guid id,
        [FromBody] UpdateMechanicRoleRequest request,
        CancellationToken ct)
    {
        try
        {
            var dto = await _catalog.UpdateAsync(
                id,
                request.Name,
                request.Constraint,
                request.MinCount,
                request.MaxCount,
                request.SortOrder,
                ct);
            return dto == null ? NotFound() : Ok(dto);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Remove(Guid id, CancellationToken ct)
    {
        var removed = await _catalog.RemoveAsync(id, ct);
        return removed ? NoContent() : NotFound();
    }
}

public record AddMechanicRoleRequest(
    int TriggerId,
    string BossName,
    string Name,
    MechanicConstraint Constraint,
    int MinCount,
    int MaxCount,
    int SortOrder);

public record UpdateMechanicRoleRequest(
    string Name,
    MechanicConstraint Constraint,
    int MinCount,
    int MaxCount,
    int SortOrder);
