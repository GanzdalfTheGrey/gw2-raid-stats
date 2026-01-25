using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("ignored_bosses")]
public class IgnoredBossEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("trigger_id"), NotNull]
    public int TriggerId { get; set; }

    [Column("boss_name"), NotNull]
    public string BossName { get; set; } = null!;

    [Column("is_cm"), NotNull]
    public bool IsCM { get; set; }

    [Column("reason")]
    public string? Reason { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }
}
