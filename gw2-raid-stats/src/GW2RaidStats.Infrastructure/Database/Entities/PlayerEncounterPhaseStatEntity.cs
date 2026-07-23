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
    // Actual % of phase active time the player had Debilitated up at any stack
    // count (0-100), sourced from EI's BuffUptimesActive.Presence. Mirrors the
    // "Uptime" column in the EI HTML report.
    [Column("debilitated_uptime_pct")] public decimal? DebilitatedUptimePct { get; set; }

    // Average stack count of Debilitated over the phase active time (0-5 for
    // this buff). Sourced from EI's BuffUptimesActive.Uptime. EI HTML report
    // shows this in the "Avg Active" column for stacking buffs.
    [Column("debilitated_avg_stacks")] public decimal? DebilitatedAvgStacks { get; set; }

    // Number of Debilitated stacks gained during the phase: count of EI
    // "Debilitated" mechanic events (one per application) inside the phase window.
    [Column("debilitated_stacks")] public int? DebilitatedStacks { get; set; }

    // % of phase active time the player *had* the boon (0-100), from EI's
    // BuffUptimesActive.Presence. Recorded for every player, not just givers — the
    // MVP boon category scores a giver on their subgroup's average received uptime.
    [Column("quickness_uptime_pct")] public decimal? QuicknessUptimePct { get; set; }
    [Column("alacrity_uptime_pct")] public decimal? AlacrityUptimePct { get; set; }

    [Column("created_at"), NotNull] public DateTimeOffset CreatedAt { get; set; }
}
