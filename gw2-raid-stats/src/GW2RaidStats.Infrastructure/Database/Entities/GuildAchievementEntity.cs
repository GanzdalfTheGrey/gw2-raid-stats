using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("guild_achievements")]
public class GuildAchievementEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("achievement_code"), NotNull]
    public string AchievementCode { get; set; } = null!;

    /// <summary>
    /// When the achievement was first earned
    /// </summary>
    [Column("achieved_at"), NotNull]
    public DateTimeOffset AchievedAt { get; set; }

    /// <summary>
    /// Context from the first completion
    /// </summary>
    [Column("context", DataType = LinqToDB.DataType.Json)]
    public string? Context { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// How many times this achievement has been completed
    /// </summary>
    [Column("completion_count"), NotNull]
    public int CompletionCount { get; set; } = 1;

    /// <summary>
    /// When the achievement was most recently completed (for showing the latest log)
    /// </summary>
    [Column("last_achieved_at"), NotNull]
    public DateTimeOffset LastAchievedAt { get; set; }

    /// <summary>
    /// Context from the most recent completion (for linking to the latest log)
    /// </summary>
    [Column("last_context", DataType = LinqToDB.DataType.Json)]
    public string? LastContext { get; set; }
}
