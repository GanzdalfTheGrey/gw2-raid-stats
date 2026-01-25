using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("players")]
public class PlayerEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("account_name"), NotNull]
    public string AccountName { get; set; } = null!;

    [Column("display_name")]
    public string? DisplayName { get; set; }

    [Column("first_seen"), NotNull]
    public DateTimeOffset FirstSeen { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }
}
