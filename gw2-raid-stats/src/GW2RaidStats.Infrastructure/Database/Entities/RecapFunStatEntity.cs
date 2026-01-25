using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("recap_fun_stats")]
public class RecapFunStatEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("mechanic_name"), NotNull]
    public string MechanicName { get; set; } = null!;

    [Column("display_title"), NotNull]
    public string DisplayTitle { get; set; } = null!;

    [Column("description")]
    public string? Description { get; set; }

    [Column("is_positive"), NotNull]
    public bool IsPositive { get; set; }

    [Column("display_order"), NotNull]
    public int DisplayOrder { get; set; }

    [Column("is_enabled"), NotNull]
    public bool IsEnabled { get; set; } = true;

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at"), NotNull]
    public DateTimeOffset UpdatedAt { get; set; }
}
