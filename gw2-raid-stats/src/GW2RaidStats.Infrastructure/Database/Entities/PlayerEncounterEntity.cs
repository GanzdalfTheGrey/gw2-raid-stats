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

    [Column("condi_cleanse")]
    public int CondiCleanse { get; set; }

    [Column("boon_strips")]
    public int BoonStrips { get; set; }

    // Boon generation (percentage uptime generated for squad, 0-100+)
    [Column("quickness_generation")]
    public decimal? QuicknessGeneration { get; set; }

    [Column("alacrity_generation")]
    public decimal? AlacracityGeneration { get; set; }

    // Healing stats
    [Column("healing")]
    public int Healing { get; set; }

    [Column("healing_power_healing")]
    public int HealingPowerHealing { get; set; }

    [Column("hps")]
    public int Hps { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }

    // Associations
    [Association(ThisKey = nameof(PlayerId), OtherKey = nameof(PlayerEntity.Id))]
    public PlayerEntity? Player { get; set; }

    [Association(ThisKey = nameof(EncounterId), OtherKey = nameof(EncounterEntity.Id))]
    public EncounterEntity? Encounter { get; set; }
}
