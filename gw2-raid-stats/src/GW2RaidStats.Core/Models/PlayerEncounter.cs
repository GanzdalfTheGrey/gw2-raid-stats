namespace GW2RaidStats.Core.Models;

public class PlayerEncounter
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public Guid EncounterId { get; set; }
    public required string CharacterName { get; set; }
    public required string Profession { get; set; }
    public int? SquadGroup { get; set; }

    // DPS stats
    public int Dps { get; set; }
    public long Damage { get; set; }
    public int? PowerDps { get; set; }
    public int? CondiDps { get; set; }
    public decimal? BreakbarDamage { get; set; }

    // Defense stats
    public int Deaths { get; set; }
    public int DeathDurationMs { get; set; }
    public int Downs { get; set; }
    public int DownDurationMs { get; set; }
    public long DamageTaken { get; set; }

    // Support stats
    public int Resurrects { get; set; }
    public int CondiCleanse { get; set; }
    public int BoonStrips { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Navigation properties
    public Player? Player { get; set; }
    public Encounter? Encounter { get; set; }
}
