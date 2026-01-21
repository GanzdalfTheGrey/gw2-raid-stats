namespace GW2RaidStats.Core.Models;

public class Encounter
{
    public Guid Id { get; set; }
    public int TriggerId { get; set; }
    public required string BossName { get; set; }
    public int? Wing { get; set; }
    public bool IsCM { get; set; }
    public bool IsLegendaryCM { get; set; }
    public bool Success { get; set; }
    public int DurationMs { get; set; }
    public DateTimeOffset EncounterTime { get; set; }
    public string? RecordedBy { get; set; }
    public string? LogUrl { get; set; }
    public string? JsonHash { get; set; }
    public string? IconUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
