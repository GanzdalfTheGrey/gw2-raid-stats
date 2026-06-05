using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using LinqToDB.Async;

namespace GW2RaidStats.Infrastructure.Services;

public class EventTemplateService
{
    private readonly RaidStatsDb _db;

    public EventTemplateService(RaidStatsDb db)
    {
        _db = db;
    }

    public async Task<List<EventTemplateEntity>> ListAsync(CancellationToken ct = default)
        => await _db.EventTemplates
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

    public async Task<EventTemplateEntity?> GetAsync(Guid id, CancellationToken ct = default)
        => await _db.EventTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<EventTemplateEntity> CreateAsync(EventTemplateEntity entity, CancellationToken ct = default)
    {
        if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        entity.CreatedAt = now;
        entity.UpdatedAt = now;
        await _db.InsertAsync(entity, token: ct);
        return entity;
    }

    public async Task UpdateAsync(EventTemplateEntity entity, CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.UpdateAsync(entity, token: ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await _db.EventTemplates.Where(t => t.Id == id).DeleteAsync(ct);

    /// <summary>
    /// Compute the next time this template would fire, expressed as a UTC DateTimeOffset.
    /// "Next" = the soonest future occurrence (today's slot if it's still future, otherwise
    /// the same weekday in the following week). DST-aware via TimeZoneInfo so a template
    /// stored as "Monday 19:00 America/Chicago" stays at 19:00 Chicago across DST shifts.
    /// </summary>
    public static DateTimeOffset ComputeNextOccurrence(int dayOfWeek, TimeSpan timeOfDay, string timezone, DateTimeOffset now)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var nowInTz = TimeZoneInfo.ConvertTime(now, tz);

        var todayAtTime = nowInTz.Date + timeOfDay;
        var daysToAdd = ((dayOfWeek - (int)nowInTz.DayOfWeek) + 7) % 7;
        var candidate = todayAtTime.AddDays(daysToAdd);
        if (candidate <= nowInTz.DateTime)
        {
            candidate = candidate.AddDays(7);
        }

        // candidate is "wall clock" time in the template's TZ — round-trip through UTC.
        var utc = TimeZoneInfo.ConvertTimeToUtc(candidate, tz);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
