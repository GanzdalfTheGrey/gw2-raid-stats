using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

/// <summary>
/// Per-player raid-night availability. One row per player.
/// Status values: 0 = unavailable (red), 1 = maybe / one-day-a-week (yellow),
/// 2 = available (green). NULL = not set.
/// </summary>
[Table("player_availability")]
public class PlayerAvailabilityEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("player_id"), NotNull]
    public Guid PlayerId { get; set; }

    [Column("monday_status")]
    public int? MondayStatus { get; set; }

    [Column("tuesday_status")]
    public int? TuesdayStatus { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("updated_at"), NotNull]
    public DateTimeOffset UpdatedAt { get; set; }
}
