using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using LinqToDB.Async;

namespace GW2RaidStats.Infrastructure.Services;

public class EventService
{
    private readonly RaidStatsDb _db;

    public EventService(RaidStatsDb db)
    {
        _db = db;
    }

    // Upcoming = not cancelled AND scheduled within the next ∞ days, with a 6h grace
    // window so the event stays in the upcoming list during the raid itself.
    public async Task<List<EventEntity>> ListUpcomingAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-6);
        return await _db.Events
            .Where(e => e.Status != "Cancelled" && e.ScheduledAt >= cutoff)
            .OrderBy(e => e.ScheduledAt)
            .ToListAsync(ct);
    }

    public async Task<List<EventEntity>> ListPastAsync(int limit = 30, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-6);
        return await _db.Events
            .Where(e => e.Status == "Cancelled" || e.ScheduledAt < cutoff)
            .OrderByDescending(e => e.ScheduledAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<EventEntity?> GetAsync(Guid id, CancellationToken ct = default)
        => await _db.Events.FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<EventEntity> CreateAsync(EventEntity entity, CancellationToken ct = default)
    {
        if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        entity.CreatedAt = now;
        entity.UpdatedAt = now;
        if (string.IsNullOrEmpty(entity.Status)) entity.Status = "Scheduled";
        await _db.InsertAsync(entity, token: ct);
        return entity;
    }

    public async Task UpdateAsync(EventEntity entity, CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.UpdateAsync(entity, token: ct);
    }

    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        var ev = await GetAsync(id, ct);
        if (ev == null) return;
        ev.Status = "Cancelled";
        await UpdateAsync(ev, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // Signups cascade via FK ON DELETE CASCADE.
        await _db.Events.Where(e => e.Id == id).DeleteAsync(ct);
    }
}
