using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("mechanic_roles")]
public class MechanicRoleEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("trigger_id"), NotNull]
    public int TriggerId { get; set; }

    [Column("boss_name"), NotNull]
    public string BossName { get; set; } = null!;

    [Column("name"), NotNull]
    public string Name { get; set; } = null!;

    [Column("slot_constraint"), NotNull]
    public int SlotConstraint { get; set; }

    [Column("min_count"), NotNull]
    public int MinCount { get; set; }

    [Column("max_count"), NotNull]
    public int MaxCount { get; set; }

    [Column("sort_order"), NotNull]
    public int SortOrder { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }
}
