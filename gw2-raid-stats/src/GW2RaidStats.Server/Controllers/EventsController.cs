using GW2RaidStats.Core.Events;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using GW2RaidStats.Infrastructure.Services;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace GW2RaidStats.Server.Controllers;

[ApiController]
[Route("api/admin/events")]
public class EventsController : ControllerBase
{
    private readonly EventService _events;
    private readonly EventSignupService _signups;
    private readonly RaidStatsDb _db;

    public EventsController(EventService events, EventSignupService signups, RaidStatsDb db)
    {
        _events = events;
        _signups = signups;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<EventListDto>>> List([FromQuery] string range = "upcoming", CancellationToken ct = default)
    {
        var entities = range == "past"
            ? await _events.ListPastAsync(ct: ct)
            : await _events.ListUpcomingAsync(ct);

        // Bulk-load signup counts so we don't fan out one query per event.
        var eventIds = entities.Select(e => e.Id).ToList();
        var signupCounts = eventIds.Count == 0
            ? new Dictionary<Guid, int>()
            : (await _db.EventSignups
                .Where(s => eventIds.Contains(s.EventId))
                .GroupBy(s => s.EventId)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToListAsync(ct))
                .ToDictionary(x => x.Id, x => x.Count);

        return Ok(entities.Select(e => MapToList(e, signupCounts.GetValueOrDefault(e.Id))).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EventDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var entity = await _events.GetAsync(id, ct);
        if (entity == null) return NotFound();
        var signups = await _signups.ListForEventAsync(id, ct);
        return Ok(MapToDetail(entity, signups));
    }

    [HttpPost]
    public async Task<ActionResult<EventDetailDto>> Create([FromBody] EventCreateDto body, CancellationToken ct)
    {
        var entity = new EventEntity
        {
            GuildId = body.GuildId,
            Title = body.Title,
            Description = body.Description,
            ScheduledAt = body.ScheduledAt,
            Timezone = string.IsNullOrEmpty(body.Timezone) ? "UTC" : body.Timezone,
            RoleSlotsJson = SerializeSlots(body.RoleSlots),
            EnforceBoonCaps = body.EnforceBoonCaps
        };
        var created = await _events.CreateAsync(entity, ct);
        return Ok(MapToDetail(created, new List<EventSignupRow>()));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult> Update(Guid id, [FromBody] EventCreateDto body, CancellationToken ct)
    {
        var entity = await _events.GetAsync(id, ct);
        if (entity == null) return NotFound();
        entity.GuildId = body.GuildId;
        entity.Title = body.Title;
        entity.Description = body.Description;
        entity.ScheduledAt = body.ScheduledAt;
        entity.Timezone = string.IsNullOrEmpty(body.Timezone) ? "UTC" : body.Timezone;
        entity.RoleSlotsJson = SerializeSlots(body.RoleSlots);
        entity.EnforceBoonCaps = body.EnforceBoonCaps;
        await _events.UpdateAsync(entity, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await _events.CancelAsync(id, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _events.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Queue a notification for the bot to post (or re-post) this event to its
    /// configured Events channel. The actual posting + button wiring lands in Group D.
    /// </summary>
    [HttpPost("{id:guid}/post")]
    public async Task<ActionResult> PostToDiscord(Guid id, CancellationToken ct)
    {
        var ev = await _events.GetAsync(id, ct);
        if (ev == null) return NotFound();

        var notification = new NotificationQueueEntity
        {
            Id = Guid.NewGuid(),
            NotificationType = "event_post",
            Payload = JsonSerializer.Serialize(new { EventId = id }),
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _db.InsertAsync(notification, token: ct);
        return Ok(new { queued = true });
    }

    private static string? SerializeSlots(List<RoleSlot>? slots) =>
        slots == null || slots.Count == 0 ? null : JsonSerializer.Serialize(slots);

    private static List<RoleSlot>? DeserializeSlots(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<List<RoleSlot>>(json);

    private static EventListDto MapToList(EventEntity e, int signupCount) =>
        new(e.Id, e.GuildId, e.Title, e.Description, e.ScheduledAt, e.Timezone, e.Status,
            e.MessageId != null, signupCount, DeserializeSlots(e.RoleSlotsJson), e.EnforceBoonCaps);

    private static EventDetailDto MapToDetail(EventEntity e, List<EventSignupRow> signups) =>
        new(e.Id, e.GuildId, e.Title, e.Description, e.ScheduledAt, e.Timezone, e.Status,
            DeserializeSlots(e.RoleSlotsJson), e.EnforceBoonCaps,
            signups.Select(s => new EventSignupDto(s.DiscordUserId, s.PlayerId, s.AccountName, s.SlotId, s.Status)).ToList());
}

public record EventListDto(
    Guid Id, long GuildId, string Title, string? Description,
    DateTimeOffset ScheduledAt, string Timezone, string Status,
    bool Posted, int SignupCount, List<RoleSlot>? RoleSlots, bool EnforceBoonCaps);

public record EventDetailDto(
    Guid Id, long GuildId, string Title, string? Description,
    DateTimeOffset ScheduledAt, string Timezone, string Status,
    List<RoleSlot>? RoleSlots, bool EnforceBoonCaps, List<EventSignupDto> Signups);

public record EventSignupDto(long DiscordUserId, Guid? PlayerId, string? AccountName, string? SlotId, string Status);

public record EventCreateDto(
    long GuildId, string Title, string? Description,
    DateTimeOffset ScheduledAt, string? Timezone, List<RoleSlot>? RoleSlots, bool EnforceBoonCaps);
