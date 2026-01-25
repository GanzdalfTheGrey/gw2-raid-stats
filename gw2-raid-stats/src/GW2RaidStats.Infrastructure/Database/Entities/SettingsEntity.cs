using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("settings")]
public class SettingsEntity
{
    [Column("key"), PrimaryKey]
    public string Key { get; set; } = null!;

    [Column("value"), NotNull]
    public string Value { get; set; } = null!;

    [Column("updated_at"), NotNull]
    public DateTimeOffset UpdatedAt { get; set; }
}
