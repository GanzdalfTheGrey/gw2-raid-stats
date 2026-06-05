using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("event_templates")]
public class EventTemplateEntity
{
    [Column("id"), PrimaryKey] public Guid Id { get; set; }
    [Column("guild_id"), NotNull] public long GuildId { get; set; }
    [Column("name"), NotNull] public string Name { get; set; } = "";
    [Column("description")] public string? Description { get; set; }

    // 0 = Sunday … 6 = Saturday, matching System.DayOfWeek.
    [Column("day_of_week"), NotNull] public int DayOfWeek { get; set; }

    // Stored as "HH:mm:ss" so the column round-trips a TimeSpan without timezone weirdness.
    [Column("time_of_day"), NotNull] public string TimeOfDay { get; set; } = "00:00:00";

    [Column("timezone"), NotNull] public string Timezone { get; set; } = "UTC";
    [Column("role_slots_json")] public string? RoleSlotsJson { get; set; }
    [Column("enforce_boon_caps"), NotNull] public bool EnforceBoonCaps { get; set; }
    [Column("active"), NotNull] public bool Active { get; set; } = true;
    [Column("created_at"), NotNull] public DateTimeOffset CreatedAt { get; set; }
    [Column("updated_at"), NotNull] public DateTimeOffset UpdatedAt { get; set; }
}
