using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace GW2RaidStats.Infrastructure.Services;

public class LogSearchService
{
    private readonly RaidStatsDb _db;
    private readonly StorageOptions _storageOptions;
    private readonly ILogger<LogSearchService> _logger;

    public LogSearchService(RaidStatsDb db, StorageOptions storageOptions, ILogger<LogSearchService> logger)
    {
        _db = db;
        _storageOptions = storageOptions;
        _logger = logger;
    }

    public async Task<LogSearchResult> SearchLogsAsync(LogSearchRequest request, CancellationToken ct = default)
    {
        var query = _db.Encounters.AsQueryable();

        // Apply filters
        if (!string.IsNullOrWhiteSpace(request.BossName))
        {
            query = query.Where(e => e.BossName.Contains(request.BossName));
        }

        if (request.TriggerId.HasValue)
        {
            query = query.Where(e => e.TriggerId == request.TriggerId.Value);
        }

        if (request.Wing.HasValue)
        {
            query = query.Where(e => e.Wing == request.Wing.Value);
        }

        if (request.IsCM.HasValue)
        {
            query = query.Where(e => e.IsCM == request.IsCM.Value);
        }

        if (request.Success.HasValue)
        {
            query = query.Where(e => e.Success == request.Success.Value);
        }

        if (request.FromDate.HasValue)
        {
            query = query.Where(e => e.EncounterTime >= request.FromDate.Value);
        }

        if (request.ToDate.HasValue)
        {
            // Add 1 day to include the entire end date
            var endDate = request.ToDate.Value.AddDays(1);
            query = query.Where(e => e.EncounterTime < endDate);
        }

        if (!string.IsNullOrWhiteSpace(request.RecordedBy))
        {
            query = query.Where(e => e.RecordedBy != null && e.RecordedBy.Contains(request.RecordedBy));
        }

        // Get total count before pagination
        var totalCount = await query.CountAsync(ct);

        // Apply sorting
        query = request.SortBy?.ToLower() switch
        {
            "bossname" => request.SortDescending ? query.OrderByDescending(e => e.BossName) : query.OrderBy(e => e.BossName),
            "duration" => request.SortDescending ? query.OrderByDescending(e => e.DurationMs) : query.OrderBy(e => e.DurationMs),
            "success" => request.SortDescending ? query.OrderByDescending(e => e.Success) : query.OrderBy(e => e.Success),
            _ => request.SortDescending ? query.OrderByDescending(e => e.EncounterTime) : query.OrderBy(e => e.EncounterTime) // Default: newest first
        };

        // Apply pagination
        var logs = await query
            .Skip(request.Page * request.PageSize)
            .Take(request.PageSize)
            .Select(e => new LogEntry(
                e.Id,
                e.TriggerId,
                e.BossName,
                e.Wing,
                e.IsCM,
                e.IsLegendaryCM,
                e.Success,
                e.DurationMs / 1000.0,
                e.EncounterTime,
                e.RecordedBy,
                e.LogUrl,
                e.IconUrl
            ))
            .ToListAsync(ct);

        return new LogSearchResult(
            logs,
            totalCount,
            request.Page,
            request.PageSize,
            (int)Math.Ceiling((double)totalCount / request.PageSize)
        );
    }

    public async Task<List<string>> GetUniqueBossNamesAsync(CancellationToken ct = default)
    {
        return await _db.Encounters
            .Select(e => e.BossName)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);
    }

    public async Task<List<int>> GetUniqueWingsAsync(CancellationToken ct = default)
    {
        return await _db.Encounters
            .Where(e => e.Wing != null)
            .Select(e => e.Wing!.Value)
            .Distinct()
            .OrderBy(w => w)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Delete encounters by their IDs. Also deletes associated player encounters and mechanic events.
    /// </summary>
    public async Task<DeleteLogsResult> DeleteLogsAsync(List<Guid> encounterIds, bool deleteFiles = false, CancellationToken ct = default)
    {
        if (encounterIds.Count == 0)
            return new DeleteLogsResult(0, 0, new List<string>());

        var errors = new List<string>();
        var filePaths = new List<string>();

        // Get file paths before deletion if we need to delete files
        if (deleteFiles)
        {
            filePaths = await _db.Encounters
                .Where(e => encounterIds.Contains(e.Id) && e.FilesPath != null)
                .Select(e => e.FilesPath!)
                .ToListAsync(ct);
        }

        // Delete mechanic events first (foreign key constraint)
        var mechanicsDeleted = await _db.MechanicEvents
            .Where(me => encounterIds.Contains(me.EncounterId))
            .DeleteAsync(ct);

        // Delete player encounters (foreign key constraint)
        var playerEncountersDeleted = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId))
            .DeleteAsync(ct);

        // Delete encounters
        var encountersDeleted = await _db.Encounters
            .Where(e => encounterIds.Contains(e.Id))
            .DeleteAsync(ct);

        // Delete files from disk if requested
        if (deleteFiles)
        {
            foreach (var path in filePaths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to delete files at {path}: {ex.Message}");
                }
            }
        }

        return new DeleteLogsResult(encountersDeleted, playerEncountersDeleted, errors);
    }

    /// <summary>
    /// Get a single log entry by ID
    /// </summary>
    public async Task<LogEntry?> GetLogByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Encounters
            .Where(e => e.Id == id)
            .Select(e => new LogEntry(
                e.Id,
                e.TriggerId,
                e.BossName,
                e.Wing,
                e.IsCM,
                e.IsLegendaryCM,
                e.Success,
                e.DurationMs / 1000.0,
                e.EncounterTime,
                e.RecordedBy,
                e.LogUrl,
                e.IconUrl
            ))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Re-parse encounters by copying their .zevtc files to the pending queue and deleting DB records.
    /// The processor will then re-parse them with the current GW2EI version.
    /// </summary>
    public async Task<ReparseLogsResult> ReparseLogsAsync(ReparseRequest request, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var encounterIds = new List<Guid>();

        // Build list of encounter IDs to reparse
        if (request.EncounterIds != null && request.EncounterIds.Count > 0)
        {
            encounterIds.AddRange(request.EncounterIds);
        }

        if (!string.IsNullOrWhiteSpace(request.BossName))
        {
            var bossEncounterIds = await _db.Encounters
                .Where(e => e.BossName.Contains(request.BossName))
                .Select(e => e.Id)
                .ToListAsync(ct);
            encounterIds.AddRange(bossEncounterIds);
        }

        // Remove duplicates
        encounterIds = encounterIds.Distinct().ToList();

        if (encounterIds.Count == 0)
        {
            return new ReparseLogsResult(0, 0, new List<string> { "No encounters found to reparse" });
        }

        _logger.LogInformation("Reparsing {Count} encounters", encounterIds.Count);

        // Get encounter data including file paths
        var encounters = await _db.Encounters
            .Where(e => encounterIds.Contains(e.Id))
            .Select(e => new { e.Id, e.FilesPath, e.BossName, e.EncounterTime })
            .ToListAsync(ct);

        var queued = 0;

        // Copy .zevtc files to pending queue
        foreach (var encounter in encounters)
        {
            if (string.IsNullOrEmpty(encounter.FilesPath))
            {
                errors.Add($"Encounter {encounter.Id} ({encounter.BossName}) has no file path");
                continue;
            }

            var zevtcPath = Path.Combine(_storageOptions.EncountersPath, encounter.FilesPath, "log.zevtc");
            if (!File.Exists(zevtcPath))
            {
                errors.Add($"Encounter {encounter.Id} ({encounter.BossName}): .zevtc file not found at {zevtcPath}");
                continue;
            }

            try
            {
                // Generate unique filename to avoid conflicts
                var timestamp = encounter.EncounterTime.ToString("yyyyMMdd-HHmmss");
                var destFileName = $"{timestamp}_{encounter.Id:N}.zevtc";
                var destPath = Path.Combine(_storageOptions.PendingPath, destFileName);

                File.Copy(zevtcPath, destPath, overwrite: true);
                queued++;
                _logger.LogDebug("Queued {Boss} for reparse: {Path}", encounter.BossName, destPath);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to queue {encounter.BossName}: {ex.Message}");
            }
        }

        // Delete encounters from database (files are kept since we copied to queue)
        var deleteResult = await DeleteLogsAsync(encounterIds, deleteFiles: false, ct);

        _logger.LogInformation("Reparse complete: {Queued} queued, {Deleted} deleted from DB, {Errors} errors",
            queued, deleteResult.EncountersDeleted, errors.Count);

        return new ReparseLogsResult(queued, deleteResult.EncountersDeleted, errors);
    }

    /// <summary>
    /// Get list of encounters that can be reparsed for a given boss name
    /// </summary>
    public async Task<List<LogEntry>> GetEncountersForBossAsync(string bossName, CancellationToken ct = default)
    {
        return await _db.Encounters
            .Where(e => e.BossName.Contains(bossName))
            .OrderByDescending(e => e.EncounterTime)
            .Select(e => new LogEntry(
                e.Id,
                e.TriggerId,
                e.BossName,
                e.Wing,
                e.IsCM,
                e.IsLegendaryCM,
                e.Success,
                e.DurationMs / 1000.0,
                e.EncounterTime,
                e.RecordedBy,
                e.LogUrl,
                e.IconUrl
            ))
            .ToListAsync(ct);
    }
}

public record LogSearchRequest(
    string? BossName = null,
    int? TriggerId = null,
    int? Wing = null,
    bool? IsCM = null,
    bool? Success = null,
    DateTimeOffset? FromDate = null,
    DateTimeOffset? ToDate = null,
    string? RecordedBy = null,
    string? SortBy = null,
    bool SortDescending = true,
    int Page = 0,
    int PageSize = 25
);

public record LogSearchResult(
    List<LogEntry> Logs,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages
);

public record LogEntry(
    Guid Id,
    int TriggerId,
    string BossName,
    int? Wing,
    bool IsCM,
    bool IsLegendaryCM,
    bool Success,
    double DurationSeconds,
    DateTimeOffset EncounterTime,
    string? RecordedBy,
    string? LogUrl,
    string? IconUrl
);

public record DeleteLogsResult(
    int EncountersDeleted,
    int PlayerEncountersDeleted,
    List<string> Errors
);

public record ReparseLogsResult(
    int EncountersQueued,
    int EncountersDeleted,
    List<string> Errors
);

public record ReparseRequest(
    List<Guid>? EncounterIds = null,
    string? BossName = null
);
