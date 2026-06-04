using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("events")]
public class EventEntity
{
    [Column("id"), PrimaryKey] public Guid Id { get; set; }
    [Column("template_id")] public Guid? TemplateId { get; set; }
    [Column("guild_id"), NotNull] public long GuildId { get; set; }
    [Column("channel_id")] public long? ChannelId { get; set; }
    [Column("message_id")] public long? MessageId { get; set; }
    [Column("title"), NotNull] public string Title { get; set; } = "";
    [Column("description")] public string? Description { get; set; }
    [Column("scheduled_at"), NotNull] public DateTimeOffset ScheduledAt { get; set; }
    [Column("timezone"), NotNull] public string Timezone { get; set; } = "UTC";
    [Column("status"), NotNull] public string Status { get; set; } = "Scheduled";
    [Column("role_slots_json")] public string? RoleSlotsJson { get; set; }
    [Column("reminder_sent_at")] public DateTimeOffset? ReminderSentAt { get; set; }
    [Column("created_at"), NotNull] public DateTimeOffset CreatedAt { get; set; }
    [Column("updated_at"), NotNull] public DateTimeOffset UpdatedAt { get; set; }
}
