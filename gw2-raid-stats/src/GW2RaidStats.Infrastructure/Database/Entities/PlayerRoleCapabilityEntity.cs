using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("player_role_capabilities")]
public class PlayerRoleCapabilityEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("player_id"), NotNull]
    public Guid PlayerId { get; set; }

    [Column("generic_role")]
    public int? GenericRole { get; set; }

    [Column("mechanic_role_id")]
    public Guid? MechanicRoleId { get; set; }

    [Column("status"), NotNull]
    public int Status { get; set; }

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("updated_at"), NotNull]
    public DateTimeOffset UpdatedAt { get; set; }
}
