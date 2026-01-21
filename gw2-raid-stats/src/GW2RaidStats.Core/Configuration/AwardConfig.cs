namespace GW2RaidStats.Core.Configuration;

public class AwardConfig
{
    public List<Award> Awards { get; set; } = [];
}

public class Award
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string Type { get; set; }  // MechanicCount, StatSum, StatMax
    public string? Mechanic { get; set; }
    public string? Stat { get; set; }
    public string? BossFilter { get; set; }
    public string Icon { get; set; } = "emoji_events";
}
