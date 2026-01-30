using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("notification_queue")]
public class NotificationQueueEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("notification_type"), NotNull]
    public string NotificationType { get; set; } = null!;

    [Column("payload", DataType = LinqToDB.DataType.Json), NotNull]
    public string Payload { get; set; } = null!;

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("processed_at")]
    public DateTimeOffset? ProcessedAt { get; set; }
}
