using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("event_reminder_preferences")]
public class EventReminderPreferenceEntity
{
    [Column("discord_user_id"), PrimaryKey] public long DiscordUserId { get; set; }
    [Column("enabled"), NotNull] public bool Enabled { get; set; }
    [Column("updated_at"), NotNull] public DateTimeOffset UpdatedAt { get; set; }
}
