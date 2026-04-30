using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Core.Roles;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services;

public class PlayerRolesService
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;

    public PlayerRolesService(RaidStatsDb db, IncludedPlayerService includedPlayerService)
    {
        _db = db;
        _includedPlayerService = includedPlayerService;
    }

    /// <summary>
    /// Get all role capabilities for a single player.
    /// </summary>
    public async Task<PlayerRoleCapabilitiesDto?> GetForPlayerAsync(Guid playerId, CancellationToken ct = default)
    {
        var player = await _db.Players.FirstOrDefaultAsync(p => p.Id == playerId, ct);
        if (player == null) return null;

        var rows = await _db.PlayerRoleCapabilities
            .Where(c => c.PlayerId == playerId)
            .ToListAsync(ct);

        var mechanicIds = rows.Where(r => r.MechanicRoleId.HasValue)
            .Select(r => r.MechanicRoleId!.Value)
            .ToList();

        var mechanicLookup = mechanicIds.Count == 0
            ? new Dictionary<Guid, MechanicRoleEntity>()
            : (await _db.MechanicRoles
                .Where(m => mechanicIds.Contains(m.Id))
                .ToListAsync(ct))
              .ToDictionary(m => m.Id);

        var generic = rows
            .Where(r => r.GenericRole.HasValue)
            .Select(r => new GenericRoleCapabilityDto(
                (GenericRole)r.GenericRole!.Value,
                (RoleCapabilityStatus)r.Status,
                r.Notes,
                r.UpdatedAt))
            .OrderBy(g => (int)g.Role)
            .ToList();

        var mechanic = rows
            .Where(r => r.MechanicRoleId.HasValue && mechanicLookup.ContainsKey(r.MechanicRoleId!.Value))
            .Select(r =>
            {
                var m = mechanicLookup[r.MechanicRoleId!.Value];
                return new MechanicRoleCapabilityDto(
                    m.Id,
                    m.TriggerId,
                    m.BossName,
                    m.Name,
                    (RoleCapabilityStatus)r.Status,
                    r.Notes,
                    r.UpdatedAt);
            })
            .OrderBy(m => m.BossName)
            .ThenBy(m => m.Name)
            .ToList();

        return new PlayerRoleCapabilitiesDto(player.Id, player.AccountName, generic, mechanic);
    }

    /// <summary>
    /// Get role capabilities matrix across all included guild members.
    /// </summary>
    public async Task<RolesMatrixDto> GetMatrixAsync(CancellationToken ct = default)
    {
        var includedNames = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);

        var rawPlayers = await _db.Players
            .Where(p => includedNames.Contains(p.AccountName))
            .Select(p => new { p.Id, p.AccountName })
            .ToListAsync(ct);

        // Case-insensitive sort (Postgres default collation puts uppercase before lowercase)
        var players = rawPlayers
            .OrderBy(p => p.AccountName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var playerIds = players.Select(p => p.Id).ToList();

        var caps = playerIds.Count == 0
            ? new List<PlayerRoleCapabilityEntity>()
            : await _db.PlayerRoleCapabilities
                .Where(c => playerIds.Contains(c.PlayerId))
                .ToListAsync(ct);

        var mechanics = await _db.MechanicRoles
            .OrderBy(m => m.BossName)
            .ThenBy(m => m.SortOrder)
            .ThenBy(m => m.Name)
            .Select(m => new MechanicRoleDto(
                m.Id,
                m.TriggerId,
                m.BossName,
                m.Name,
                (MechanicConstraint)m.SlotConstraint,
                m.MinCount,
                m.MaxCount,
                m.SortOrder))
            .ToListAsync(ct);

        var capsByPlayer = caps.GroupBy(c => c.PlayerId).ToDictionary(g => g.Key, g => g.ToList());

        var rows = players.Select(p =>
        {
            var rowCaps = capsByPlayer.GetValueOrDefault(p.Id) ?? new List<PlayerRoleCapabilityEntity>();
            var generic = rowCaps
                .Where(c => c.GenericRole.HasValue)
                .ToDictionary(c => c.GenericRole!.Value, c => (RoleCapabilityStatus)c.Status);
            var mechanic = rowCaps
                .Where(c => c.MechanicRoleId.HasValue)
                .ToDictionary(c => c.MechanicRoleId!.Value, c => (RoleCapabilityStatus)c.Status);
            return new RolesMatrixRowDto(p.Id, p.AccountName, generic, mechanic);
        }).ToList();

        return new RolesMatrixDto(mechanics, rows);
    }

    /// <summary>
    /// Set a generic role capability. Pass null status to clear it (delete the row).
    /// </summary>
    public async Task SetGenericAsync(
        Guid playerId,
        GenericRole role,
        RoleCapabilityStatus? status,
        string? notes,
        CancellationToken ct = default)
    {
        var existing = await _db.PlayerRoleCapabilities
            .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.GenericRole == (int)role, ct);

        if (status == null)
        {
            if (existing != null)
            {
                await _db.PlayerRoleCapabilities
                    .Where(c => c.Id == existing.Id)
                    .DeleteAsync(ct);
            }
            return;
        }

        if (existing == null)
        {
            await _db.InsertAsync(new PlayerRoleCapabilityEntity
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                GenericRole = (int)role,
                MechanicRoleId = null,
                Status = (int)status,
                Notes = notes,
                UpdatedAt = DateTimeOffset.UtcNow
            }, token: ct);
        }
        else
        {
            await _db.PlayerRoleCapabilities
                .Where(c => c.Id == existing.Id)
                .Set(c => c.Status, (int)status)
                .Set(c => c.Notes, notes)
                .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);
        }
    }

    /// <summary>
    /// Set a mechanic role capability. Pass null status to clear it.
    /// </summary>
    public async Task SetMechanicAsync(
        Guid playerId,
        Guid mechanicRoleId,
        RoleCapabilityStatus? status,
        string? notes,
        CancellationToken ct = default)
    {
        var existing = await _db.PlayerRoleCapabilities
            .FirstOrDefaultAsync(c => c.PlayerId == playerId && c.MechanicRoleId == mechanicRoleId, ct);

        if (status == null)
        {
            if (existing != null)
            {
                await _db.PlayerRoleCapabilities
                    .Where(c => c.Id == existing.Id)
                    .DeleteAsync(ct);
            }
            return;
        }

        if (existing == null)
        {
            await _db.InsertAsync(new PlayerRoleCapabilityEntity
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                GenericRole = null,
                MechanicRoleId = mechanicRoleId,
                Status = (int)status,
                Notes = notes,
                UpdatedAt = DateTimeOffset.UtcNow
            }, token: ct);
        }
        else
        {
            await _db.PlayerRoleCapabilities
                .Where(c => c.Id == existing.Id)
                .Set(c => c.Status, (int)status)
                .Set(c => c.Notes, notes)
                .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);
        }
    }

    public async Task<Guid?> ResolvePlayerIdAsync(string accountName, CancellationToken ct = default)
    {
        return await _db.Players
            .Where(p => p.AccountName == accountName)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);
    }
}

public record PlayerRoleCapabilitiesDto(
    Guid PlayerId,
    string AccountName,
    List<GenericRoleCapabilityDto> Generic,
    List<MechanicRoleCapabilityDto> Mechanic);

public record GenericRoleCapabilityDto(
    GenericRole Role,
    RoleCapabilityStatus Status,
    string? Notes,
    DateTimeOffset UpdatedAt);

public record MechanicRoleCapabilityDto(
    Guid MechanicRoleId,
    int TriggerId,
    string BossName,
    string Name,
    RoleCapabilityStatus Status,
    string? Notes,
    DateTimeOffset UpdatedAt);

public record RolesMatrixDto(
    List<MechanicRoleDto> Mechanics,
    List<RolesMatrixRowDto> Rows);

public record RolesMatrixRowDto(
    Guid PlayerId,
    string AccountName,
    Dictionary<int, RoleCapabilityStatus> GenericByRole,
    Dictionary<Guid, RoleCapabilityStatus> MechanicById);
