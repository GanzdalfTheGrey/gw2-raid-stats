using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("player_encounter_phase_stats")]
public class PlayerEncounterPhaseStatEntity
{
    [Column("id"), PrimaryKey] public Guid Id { get; set; }
    [Column("encounter_id"), NotNull] public Guid EncounterId { get; set; }
    [Column("player_id"), NotNull] public Guid PlayerId { get; set; }
    [Column("phase_index"), NotNull] public int PhaseIndex { get; set; }
    [Column("phase_name"), NotNull] public string PhaseName { get; set; } = "";
    [Column("dps"), NotNull] public int Dps { get; set; }
    [Column("damage"), NotNull] public long Damage { get; set; }
    [Column("dead_count"), NotNull] public int DeadCount { get; set; }
    [Column("down_count"), NotNull] public int DownCount { get; set; }
    [Column("dead_duration_ms"), NotNull] public int DeadDurationMs { get; set; }
    [Column("down_duration_ms"), NotNull] public int DownDurationMs { get; set; }
    [Column("dead_at_phase_start"), NotNull] public bool DeadAtPhaseStart { get; set; }
    [Column("debilitated_uptime_pct")] public decimal? DebilitatedUptimePct { get; set; }
    [Column("created_at"), NotNull] public DateTimeOffset CreatedAt { get; set; }
}
