namespace GW2RaidStats.Infrastructure.Services.Achievements;

#region Player Achievement DTOs

public record PlayerAchievementDto(
    string Code,
    string Name,
    string Description,
    string Category,
    DateTimeOffset AchievedAt,
    string? Context,
    int EarnedCount,
    int TotalIncludedPlayers
);

public record AchievementProgressDto(
    string Code,
    string Name,
    string Description,
    string Category,
    int Current,
    int Required,
    string ProgressText
);

#endregion

#region Guild Achievement DTOs

public record GuildAchievementDto(
    string Code,
    string Name,
    string Description,
    string Category,
    DateTimeOffset? AchievedAt,
    bool IsEarned,
    string? Context,
    int CompletionCount,
    DateTimeOffset? LastAchievedAt,
    string? LastContext
);

#endregion

#region Wing Master Progress DTOs

/// <summary>
/// Detailed Wing Master progress showing missing boss/role combos
/// </summary>
public record WingMasterDetailedProgressDto(
    string Code,
    string Name,
    string Description,
    int WingNumber,
    int Completed,
    int Total,
    List<WingMasterBossProgressDto> Bosses
);

public record WingMasterBossProgressDto(
    int TriggerId,
    string BossName,
    List<WingMasterRoleProgressDto> Roles
);

public record WingMasterRoleProgressDto(
    string RoleCode,
    string RoleDisplayName,
    bool Completed
);

#endregion

#region Completion Progress DTOs

/// <summary>
/// Detailed completion progress showing missing bosses
/// </summary>
public record CompletionDetailedProgressDto(
    string Code,
    string Name,
    string Description,
    int Completed,
    int Total,
    List<CompletionBossProgressDto> Bosses
);

public record CompletionBossProgressDto(
    int TriggerId,
    string BossName,
    int? Wing,
    bool Completed
);

#endregion

#region Internal Progress Records

/// <summary>
/// Progress towards Class Completionist achievement
/// </summary>
public record ClassCompletionistProgress(
    string Profession,
    int CompletedSpecs,
    List<string> CompletedSpecsList,
    List<string> MissingSpecs,
    string? TopBoss
);

#endregion
