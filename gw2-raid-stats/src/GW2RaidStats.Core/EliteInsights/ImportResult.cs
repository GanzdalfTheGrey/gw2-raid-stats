namespace GW2RaidStats.Core.EliteInsights;

public record ImportResult(
    bool Success,
    Guid? EncounterId,
    string FileName,
    string? BossName,
    string? Error,
    bool WasDuplicate
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
