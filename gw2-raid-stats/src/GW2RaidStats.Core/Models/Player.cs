namespace GW2RaidStats.Core.Models;

public class Player
{
    public Guid Id { get; set; }
    public required string AccountName { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
