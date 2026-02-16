using GW2RaidStats.Infrastructure.Database.Entities;

namespace GW2RaidStats.Infrastructure.Services.Achievements.Checkers;

/// <summary>
/// Interface for achievement checkers. Each implementation checks a category of achievements.
/// </summary>
public interface IAchievementChecker
{
    /// <summary>
    /// Check for achievements based on the provided context.
    /// Returns a list of achievements that should be awarded.
    /// </summary>
    Task<List<AchievementUnlock>> CheckAsync(AchievementCheckContext context, CancellationToken ct);
}

/// <summary>
/// Context passed to achievement checkers containing all data needed for evaluation.
/// </summary>
public record AchievementCheckContext
{
    /// <summary>
    /// The encounter being evaluated
    /// </summary>
    public required EncounterEntity Encounter { get; init; }

    /// <summary>
    /// All players in this encounter with their stats
    /// </summary>
    public required List<PlayerEncounterData> Players { get; init; }

    /// <summary>
    /// Account names of guild members (for filtering pugs)
    /// </summary>
    public required HashSet<string> IncludedAccounts { get; init; }

    /// <summary>
    /// Whether to send Discord notifications for unlocks
    /// </summary>
    public bool Notify { get; init; } = true;

    /// <summary>
    /// Get only guild member players from this encounter
    /// </summary>
    public IEnumerable<PlayerEncounterData> GuildMembers =>
        Players.Where(p => IncludedAccounts.Contains(p.Player.AccountName));

    /// <summary>
    /// Number of guild members in this encounter
    /// </summary>
    public int GuildMemberCount => GuildMembers.Count();
}

/// <summary>
/// Combined player and encounter data for achievement checking
/// </summary>
public record PlayerEncounterData(
    PlayerEncounterEntity PlayerEncounter,
    PlayerEntity Player
)
{
    // Convenience accessors
    public Guid PlayerId => Player.Id;
    public string AccountName => Player.AccountName;
    public string Profession => PlayerEncounter.Profession;
    public int Dps => PlayerEncounter.Dps;
    public int Deaths => PlayerEncounter.Deaths;
    public int Downs => PlayerEncounter.Downs;
    public int Resurrects => PlayerEncounter.Resurrects;
    public int? BreakbarDamage => PlayerEncounter.BreakbarDamage.HasValue ? (int)PlayerEncounter.BreakbarDamage.Value : null;
    public decimal? QuicknessGeneration => PlayerEncounter.QuicknessGeneration;
    public decimal? AlacracityGeneration => PlayerEncounter.AlacracityGeneration;
    public string? Role => PlayerEncounter.Role;
    public int? SquadGroup => PlayerEncounter.SquadGroup;
}

/// <summary>
/// Represents an achievement that should be unlocked
/// </summary>
public record AchievementUnlock(
    /// <summary>
    /// Achievement code (e.g., "the_carry", "flawless_wing_1")
    /// </summary>
    string Code,

    /// <summary>
    /// Player ID for personal achievements, null for guild achievements
    /// </summary>
    Guid? PlayerId,

    /// <summary>
    /// Optional context data to store with the achievement (serialized to JSON)
    /// </summary>
    object? Context,

    /// <summary>
    /// When the achievement was earned
    /// </summary>
    DateTimeOffset AchievedAt
)
{
    /// <summary>
    /// Whether this is a guild achievement
    /// </summary>
    public bool IsGuildAchievement => PlayerId == null;
}
