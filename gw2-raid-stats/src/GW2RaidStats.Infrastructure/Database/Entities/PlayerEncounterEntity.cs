using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("player_encounters")]
public class PlayerEncounterEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("player_id"), NotNull]
    public Guid PlayerId { get; set; }

    [Column("encounter_id"), NotNull]
    public Guid EncounterId { get; set; }

    [Column("character_name"), NotNull]
    public string CharacterName { get; set; } = null!;

    [Column("profession"), NotNull]
    public string Profession { get; set; } = null!;

    [Column("squad_group")]
    public int? SquadGroup { get; set; }

    // DPS stats
    [Column("dps"), NotNull]
    public int Dps { get; set; }

    [Column("damage"), NotNull]
    public long Damage { get; set; }

    [Column("power_dps")]
    public int? PowerDps { get; set; }

    [Column("condi_dps")]
    public int? CondiDps { get; set; }

    [Column("breakbar_damage")]
    public decimal? BreakbarDamage { get; set; }

    // Defense stats
    [Column("deaths"), NotNull]
    public int Deaths { get; set; }

    [Column("death_duration_ms")]
    public int DeathDurationMs { get; set; }

    [Column("downs"), NotNull]
    public int Downs { get; set; }

    [Column("down_duration_ms")]
    public int DownDurationMs { get; set; }

    [Column("damage_taken")]
    public long DamageTaken { get; set; }

    // Support stats
    [Column("resurrects")]
    public int Resurrects { get; set; }

    [Column("resurrect_time")]
    public decimal ResurrectTime { get; set; }

    [Column("condi_cleanse")]
    public int CondiCleanse { get; set; }

    [Column("boon_strips")]
    public int BoonStrips { get; set; }

    // Boon generation (percentage uptime generated for squad, 0-100+)
    [Column("quickness_generation")]
    public decimal? QuicknessGeneration { get; set; }

    [Column("alacrity_generation")]
    public decimal? AlacracityGeneration { get; set; }

    // Boon self-uptime (% of fight time the player had the boon on themselves, from EI's
    // "Phase active duration" view — buffUptimesActive in JSON). Used to compute sub-group
    // average uptime for the Generation metric, and to flag positioning issues via the Self
    // metric when the player wasn't the boon generator.
    [Column("quickness_self_uptime")]
    public decimal? QuicknessSelfUptime { get; set; }

    [Column("alacrity_self_uptime")]
    public decimal? AlacritySelfUptime { get; set; }

    // Tier-2 boon self-uptime (same EI buffUptimesActive source). MightAvgStacks is average
    // stacks 0-25; the rest are percentage uptime 0-100.
    [Column("might_avg_stacks")]
    public decimal? MightAvgStacks { get; set; }

    [Column("fury_uptime")]
    public decimal? FuryUptime { get; set; }

    [Column("regeneration_uptime")]
    public decimal? RegenerationUptime { get; set; }

    [Column("protection_uptime")]
    public decimal? ProtectionUptime { get; set; }

    [Column("swiftness_uptime")]
    public decimal? SwiftnessUptime { get; set; }

    // Average distance to the squad's centroid (EI statsAll.stackDist; GW2 units, lower = tighter).
    [Column("stack_distance")]
    public decimal? StackDistance { get; set; }

    // Healing stats (from extension - requires arcdps healing extension)
    [Column("healing")]
    public int Healing { get; set; }

    [Column("healing_power_healing")]
    public int HealingPowerHealing { get; set; }

    [Column("hps")]
    public int Hps { get; set; }

    // Character attribute - Healing Power stat (always available)
    [Column("healing_power_stat")]
    public int HealingPowerStat { get; set; }

    // Role classification for Wing Master achievement tracking
    // Values: heal_alac, heal_quick, dps_alac, dps_quick, pure_dps
    [Column("role")]
    public string? Role { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }

    // Associations
    [Association(ThisKey = nameof(PlayerId), OtherKey = nameof(PlayerEntity.Id))]
    public PlayerEntity? Player { get; set; }

    [Association(ThisKey = nameof(EncounterId), OtherKey = nameof(EncounterEntity.Id))]
    public EncounterEntity? Encounter { get; set; }
}
