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
[Route("api/admin/event-templates")]
public class EventTemplatesController : ControllerBase
{
    private readonly EventTemplateService _templates;
    private readonly EventService _events;
    private readonly RaidStatsDb _db;

    public EventTemplatesController(EventTemplateService templates, EventService events, RaidStatsDb db)
    {
        _templates = templates;
        _events = events;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<EventTemplateDto>>> List(CancellationToken ct)
    {
        var entities = await _templates.ListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return Ok(entities.Select(t => MapToDto(t, now)).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EventTemplateDto>> Get(Guid id, CancellationToken ct)
    {
        var t = await _templates.GetAsync(id, ct);
        if (t == null) return NotFound();
        return Ok(MapToDto(t, DateTimeOffset.UtcNow));
    }

    [HttpPost]
    public async Task<ActionResult<EventTemplateDto>> Create([FromBody] EventTemplateCreateDto body, CancellationToken ct)
    {
        var entity = new EventTemplateEntity
        {
            GuildId = body.GuildId,
            Name = body.Name,
            Description = body.Description,
            DayOfWeek = body.DayOfWeek,
            TimeOfDay = body.TimeOfDay,
            Timezone = string.IsNullOrEmpty(body.Timezone) ? "UTC" : body.Timezone,
            RoleSlotsJson = SerializeSlots(body.RoleSlots),
            EnforceBoonCaps = body.EnforceBoonCaps,
            Active = body.Active
        };
        var created = await _templates.CreateAsync(entity, ct);
        return Ok(MapToDto(created, DateTimeOffset.UtcNow));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult> Update(Guid id, [FromBody] EventTemplateCreateDto body, CancellationToken ct)
    {
        var entity = await _templates.GetAsync(id, ct);
        if (entity == null) return NotFound();
        entity.GuildId = body.GuildId;
        entity.Name = body.Name;
        entity.Description = body.Description;
        entity.DayOfWeek = body.DayOfWeek;
        entity.TimeOfDay = body.TimeOfDay;
        entity.Timezone = string.IsNullOrEmpty(body.Timezone) ? "UTC" : body.Timezone;
        entity.RoleSlotsJson = SerializeSlots(body.RoleSlots);
        entity.EnforceBoonCaps = body.EnforceBoonCaps;
        entity.Active = body.Active;
        await _templates.UpdateAsync(entity, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _templates.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Spawn (or refresh) the event for this template's next occurrence and queue a Discord
    /// post. If an event already exists for (template_id, next_occurrence) we refresh its
    /// snapshot from the current template fields and re-queue the post — that way the user
    /// can edit the template and re-click "Post" to push updates to the live event.
    /// </summary>
    [HttpPost("{id:guid}/post-next")]
    public async Task<ActionResult> PostNext(Guid id, CancellationToken ct)
    {
        var t = await _templates.GetAsync(id, ct);
        if (t == null) return NotFound();

        if (!TimeSpan.TryParse(t.TimeOfDay, out var timeOfDay))
        {
            return BadRequest($"Template's time_of_day is not a valid TimeSpan: '{t.TimeOfDay}'");
        }
        var nextAt = EventTemplateService.ComputeNextOccurrence(t.DayOfWeek, timeOfDay, t.Timezone, DateTimeOffset.UtcNow);

        var existing = await _db.Events
            .FirstOrDefaultAsync(e => e.TemplateId == id && e.ScheduledAt == nextAt, ct);

        EventEntity ev;
        if (existing == null)
        {
            ev = await _events.CreateAsync(new EventEntity
            {
                TemplateId = t.Id,
                GuildId = t.GuildId,
                Title = t.Name,
                Description = t.Description,
                ScheduledAt = nextAt,
                Timezone = t.Timezone,
                RoleSlotsJson = t.RoleSlotsJson,
                EnforceBoonCaps = t.EnforceBoonCaps
            }, ct);
        }
        else
        {
            // Refresh snapshot — keeps the live event aligned with the latest template.
            existing.Title = t.Name;
            existing.Description = t.Description;
            existing.RoleSlotsJson = t.RoleSlotsJson;
            existing.EnforceBoonCaps = t.EnforceBoonCaps;
            existing.Status = "Scheduled";
            await _events.UpdateAsync(existing, ct);
            ev = existing;
        }

        var notification = new NotificationQueueEntity
        {
            Id = Guid.NewGuid(),
            NotificationType = "event_post",
            Payload = JsonSerializer.Serialize(new { EventId = ev.Id }),
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _db.InsertAsync(notification, token: ct);
        return Ok(new { eventId = ev.Id, scheduledAt = nextAt, created = existing == null });
    }

    private static string? SerializeSlots(List<RoleSlot>? slots) =>
        slots == null || slots.Count == 0 ? null : JsonSerializer.Serialize(slots);

    private static List<RoleSlot>? DeserializeSlots(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<List<RoleSlot>>(json);

    private static EventTemplateDto MapToDto(EventTemplateEntity t, DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        if (TimeSpan.TryParse(t.TimeOfDay, out var tod))
        {
            try { next = EventTemplateService.ComputeNextOccurrence(t.DayOfWeek, tod, t.Timezone, now); }
            catch { /* bad timezone string — leave next null */ }
        }
        return new EventTemplateDto(
            t.Id, t.GuildId, t.Name, t.Description,
            t.DayOfWeek, t.TimeOfDay, t.Timezone,
            DeserializeSlots(t.RoleSlotsJson),
            t.EnforceBoonCaps, t.Active, next);
    }
}

public record EventTemplateDto(
    Guid Id, long GuildId, string Name, string? Description,
    int DayOfWeek, string TimeOfDay, string Timezone,
    List<RoleSlot>? RoleSlots, bool EnforceBoonCaps, bool Active,
    DateTimeOffset? NextOccurrence);

public record EventTemplateCreateDto(
    long GuildId, string Name, string? Description,
    int DayOfWeek, string TimeOfDay, string? Timezone,
    List<RoleSlot>? RoleSlots, bool EnforceBoonCaps, bool Active);
