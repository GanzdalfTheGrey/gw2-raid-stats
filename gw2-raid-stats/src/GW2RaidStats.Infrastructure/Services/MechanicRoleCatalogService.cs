using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Core.Roles;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services;

public class MechanicRoleCatalogService
{
    private readonly RaidStatsDb _db;

    public MechanicRoleCatalogService(RaidStatsDb db)
    {
        _db = db;
    }

    public async Task<List<MechanicRoleDto>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.MechanicRoles
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
    }

    public async Task<MechanicRoleDto> AddAsync(
        int triggerId,
        string bossName,
        string name,
        MechanicConstraint constraint,
        int minCount,
        int maxCount,
        int sortOrder,
        CancellationToken ct = default)
    {
        if (minCount < 1) throw new ArgumentException("MinCount must be >= 1", nameof(minCount));
        if (maxCount < minCount) throw new ArgumentException("MaxCount must be >= MinCount", nameof(maxCount));

        var entity = new MechanicRoleEntity
        {
            Id = Guid.NewGuid(),
            TriggerId = triggerId,
            BossName = bossName,
            Name = name,
            SlotConstraint = (int)constraint,
            MinCount = minCount,
            MaxCount = maxCount,
            SortOrder = sortOrder,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _db.InsertAsync(entity, token: ct);

        return ToDto(entity);
    }

    public async Task<MechanicRoleDto?> UpdateAsync(
        Guid id,
        string name,
        MechanicConstraint constraint,
        int minCount,
        int maxCount,
        int sortOrder,
        CancellationToken ct = default)
    {
        if (minCount < 1) throw new ArgumentException("MinCount must be >= 1", nameof(minCount));
        if (maxCount < minCount) throw new ArgumentException("MaxCount must be >= MinCount", nameof(maxCount));

        var rows = await _db.MechanicRoles
            .Where(m => m.Id == id)
            .Set(m => m.Name, name)
            .Set(m => m.SlotConstraint, (int)constraint)
            .Set(m => m.MinCount, minCount)
            .Set(m => m.MaxCount, maxCount)
            .Set(m => m.SortOrder, sortOrder)
            .UpdateAsync(ct);

        if (rows == 0) return null;

        var entity = await _db.MechanicRoles.FirstOrDefaultAsync(m => m.Id == id, ct);
        return entity == null ? null : ToDto(entity);
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var deleted = await _db.MechanicRoles
            .Where(m => m.Id == id)
            .DeleteAsync(ct);
        return deleted > 0;
    }

    private static MechanicRoleDto ToDto(MechanicRoleEntity m) => new(
        m.Id,
        m.TriggerId,
        m.BossName,
        m.Name,
        (MechanicConstraint)m.SlotConstraint,
        m.MinCount,
        m.MaxCount,
        m.SortOrder);
}

public record MechanicRoleDto(
    Guid Id,
    int TriggerId,
    string BossName,
    string Name,
    MechanicConstraint Constraint,
    int MinCount,
    int MaxCount,
    int SortOrder);
