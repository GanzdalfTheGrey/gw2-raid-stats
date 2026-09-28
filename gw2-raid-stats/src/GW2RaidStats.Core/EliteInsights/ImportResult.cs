namespace GW2RaidStats.Core.EliteInsights;

public record ImportResult(
    bool Success,
    Guid? EncounterId,
    string FileName,
    string? BossName,
    string? Error,
    bool WasDuplicate,
    // Deliberately not imported (late start, non-boss event) - not a failure worth keeping
    bool WasSkipped = false
);

public record BulkImportResult(
    int TotalFiles,
    int Imported,
    int Duplicates,
    int Failed,
    TimeSpan Duration,
    List<ImportResult> Results
);

public record ImportProgress(
    int Current,
    int Total,
    string CurrentFile,
    int Imported,
    int Duplicates,
    int Failed
);
