using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("encounter_phase_stats")]
public class EncounterPhaseStatEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("encounter_id"), NotNull]
    public Guid EncounterId { get; set; }

    [Column("phase_index"), NotNull]
    public int PhaseIndex { get; set; }

    [Column("phase_name"), NotNull]
    public string PhaseName { get; set; } = null!;

    [Column("squad_dps"), NotNull]
    public int SquadDps { get; set; }

    [Column("duration_ms"), NotNull]
    public int DurationMs { get; set; }

    // True when every main target of the phase died (e.g. Void Goliath killed), false
    // when one survived (wiped mid-phase), null when the log has no target info or the
    // row predates migration 036.
    [Column("completed")]
    public bool? Completed { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }
}
