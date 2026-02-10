using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("app_version")]
public class AppVersionEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("version"), NotNull]
    public string Version { get; set; } = string.Empty;

    [Column("broadcast_at"), NotNull]
    public DateTimeOffset BroadcastAt { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }
}
