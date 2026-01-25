using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Infrastructure.Services;

public class LogSearchService
{
    private readonly RaidStatsDb _db;

    public LogSearchService(RaidStatsDb db)
    {
        _db = db;
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
            _ => request.SortDescending ? query.OrderBy(e => e.EncounterTime) : query.OrderByDescending(e => e.EncounterTime) // Default: newest first
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
