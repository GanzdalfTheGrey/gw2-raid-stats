using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("player_achievements")]
public class PlayerAchievementEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("player_id"), NotNull]
    public Guid PlayerId { get; set; }

    [Column("achievement_code"), NotNull]
    public string AchievementCode { get; set; } = null!;

    [Column("achieved_at"), NotNull]
    public DateTimeOffset AchievedAt { get; set; }

    [Column("context", DataType = LinqToDB.DataType.Json)]
    public string? Context { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }

    // Associations
    [Association(ThisKey = nameof(PlayerId), OtherKey = nameof(PlayerEntity.Id))]
    public PlayerEntity? Player { get; set; }
}
