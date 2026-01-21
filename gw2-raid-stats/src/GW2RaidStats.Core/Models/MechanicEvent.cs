namespace GW2RaidStats.Core.Models;

public class MechanicEvent
{
    public Guid Id { get; set; }
    public Guid EncounterId { get; set; }
    public Guid? PlayerId { get; set; }
    public required string MechanicName { get; set; }
    public string? MechanicFullName { get; set; }
    public string? Description { get; set; }
    public int EventTimeMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Navigation properties
    public Encounter? Encounter { get; set; }
    public Player? Player { get; set; }
}
