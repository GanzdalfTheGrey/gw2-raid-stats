using LinqToDB.Mapping;

namespace GW2RaidStats.Infrastructure.Database.Entities;

[Table("encounters")]
public class EncounterEntity
{
    [Column("id"), PrimaryKey]
    public Guid Id { get; set; }

    [Column("trigger_id"), NotNull]
    public int TriggerId { get; set; }

    [Column("boss_name"), NotNull]
    public string BossName { get; set; } = null!;

    [Column("wing")]
    public int? Wing { get; set; }

    [Column("is_cm"), NotNull]
    public bool IsCM { get; set; }

    [Column("is_legendary_cm"), NotNull]
    public bool IsLegendaryCM { get; set; }

    [Column("success"), NotNull]
    public bool Success { get; set; }

    [Column("duration_ms"), NotNull]
    public int DurationMs { get; set; }

    [Column("encounter_time"), NotNull]
    public DateTimeOffset EncounterTime { get; set; }

    [Column("recorded_by")]
    public string? RecordedBy { get; set; }

    [Column("log_url")]
    public string? LogUrl { get; set; }

    [Column("json_hash")]
    public string? JsonHash { get; set; }

    [Column("icon_url")]
    public string? IconUrl { get; set; }

    [Column("files_path")]
    public string? FilesPath { get; set; }

    [Column("original_filename")]
    public string? OriginalFilename { get; set; }

    [Column("created_at"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }
}
