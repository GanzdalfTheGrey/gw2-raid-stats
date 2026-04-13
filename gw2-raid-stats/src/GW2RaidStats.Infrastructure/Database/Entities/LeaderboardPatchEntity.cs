using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("leaderboard_patches")]
public class LeaderboardPatchEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("name"), NotNull]
    public string Name { get; set; } = null!;

    [Column("start_date"), NotNull]
    public DateTimeOffset StartDate { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }
}
