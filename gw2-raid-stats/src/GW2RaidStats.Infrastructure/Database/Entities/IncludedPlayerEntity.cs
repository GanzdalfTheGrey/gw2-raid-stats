using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("included_players")]
public class IncludedPlayerEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("account_name"), NotNull]
    public string AccountName { get; set; } = null!;

    [Column("reason")]
    public string? Reason { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }
}
