using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("mechanic_events")]
public class MechanicEventEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("encounter_id"), NotNull]
    public Guid EncounterId { get; set; }

    [Column("player_id")]
    public Guid? PlayerId { get; set; }

    [Column("mechanic_name"), NotNull]
    public string MechanicName { get; set; } = null!;

    [Column("mechanic_full_name")]
    public string? MechanicFullName { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("event_time_ms"), NotNull]
    public int EventTimeMs { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }

    // Associations
    [Association(ThisKey = nameof(EncounterId), OtherKey = nameof(EncounterEntity.Id))]
    public EncounterEntity? Encounter { get; set; }

    [Association(ThisKey = nameof(PlayerId), OtherKey = nameof(PlayerEntity.Id))]
    public PlayerEntity? Player { get; set; }
}
