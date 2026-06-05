using GW2RaidStats.Core.Events;
using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using LinqToDB.Async;

namespace GW2RaidStats.Infrastructure.Services;

public class EventSignupService
{
    private readonly RaidStatsDb _db;

    public EventSignupService(RaidStatsDb db)
    {
        _db = db;
    }

    /// <summary>
    /// Signups for one event, joined with the players table for account name resolution.
    /// Two queries (signups + bulk player lookup) instead of a LeftJoin in SQL — events
    /// rarely have more than 10–20 signups so the overhead is trivial and the code is
    /// easier to read than left-join projection gymnastics.
    /// </summary>
    public async Task<List<EventSignupRow>> ListForEventAsync(Guid eventId, CancellationToken ct = default)
    {
        var signups = await _db.EventSignups
            .Where(s => s.EventId == eventId)
            .OrderBy(s => s.SignedUpAt)
            .ToListAsync(ct);

        var playerIds = signups
            .Where(s => s.PlayerId.HasValue)
            .Select(s => s.PlayerId!.Value)
            .Distinct()
            .ToList();

        var players = playerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await _db.Players
                .Where(p => playerIds.Contains(p.Id))
                .ToListAsync(ct))
                .ToDictionary(p => p.Id, p => p.AccountName);

        return signups.Select(s => new EventSignupRow(
            s.Id,
            s.EventId,
            s.DiscordUserId,
            s.PlayerId,
            s.PlayerId.HasValue && players.TryGetValue(s.PlayerId.Value, out var name) ? name : null,
            s.SlotId,
            s.Status,
            s.SignedUpAt
        )).ToList();
    }

    /// <summary>
    /// Sign up for a specific role slot. Overflow rules (any → user goes to Reserve):
    ///   * the slot itself is at its per-slot capacity, OR
    ///   * <paramref name="enforceBoonCaps"/> is true AND the chosen slot's role or boon
    ///     tag is already at the squad-wide cap of 2 (heals / boondps / quicks / alacs).
    /// All "current count" checks exclude the requesting user so moving slots within
    /// the same tag doesn't double-count and falsely overflow.
    /// </summary>
    public async Task<SignupResult> JoinSlotAsync(
        Guid eventId, ulong discordUserId, string slotId,
        List<RoleSlot> allSlots, bool enforceBoonCaps,
        CancellationToken ct = default)
    {
        var slot = allSlots.FirstOrDefault(s => s.Id == slotId);
        if (slot == null)
        {
            // Slot vanished between embed render and click — treat as reserve.
            var entity = await UpsertAsync(eventId, discordUserId, slotId: null, status: "Reserve", ct);
            return new SignupResult(entity, OverflowedToReserve: true, OverflowReason: "That slot no longer exists");
        }

        var currentInSlot = await CountAcceptedAsync(eventId, new List<string> { slotId }, discordUserId, ct);
        if (currentInSlot >= slot.Count)
        {
            var entity = await UpsertAsync(eventId, discordUserId, slotId: null, status: "Reserve", ct);
            return new SignupResult(entity, OverflowedToReserve: true, OverflowReason: $"**{slot.Label}** is full");
        }

        if (enforceBoonCaps)
        {
            // Squad-wide caps derived from the role/boon tags. Cap is the standard 2 for
            // all four groups (heals, boondps, quicks, alacs).
            const int boonCap = 2;
            if (!string.IsNullOrEmpty(slot.Role))
            {
                var roleSlotIds = allSlots.Where(s => s.Role == slot.Role).Select(s => s.Id).ToList();
                var inRole = await CountAcceptedAsync(eventId, roleSlotIds, discordUserId, ct);
                if (inRole >= boonCap)
                {
                    var entity = await UpsertAsync(eventId, discordUserId, slotId: null, status: "Reserve", ct);
                    return new SignupResult(entity, OverflowedToReserve: true,
                        OverflowReason: $"The squad already has 2 {FormatGroupName(slot.Role)}");
                }
            }
            if (!string.IsNullOrEmpty(slot.Boon))
            {
                var boonSlotIds = allSlots.Where(s => s.Boon == slot.Boon).Select(s => s.Id).ToList();
                var inBoon = await CountAcceptedAsync(eventId, boonSlotIds, discordUserId, ct);
                if (inBoon >= boonCap)
                {
                    var entity = await UpsertAsync(eventId, discordUserId, slotId: null, status: "Reserve", ct);
                    return new SignupResult(entity, OverflowedToReserve: true,
                        OverflowReason: $"The squad already has 2 {FormatGroupName(slot.Boon)}");
                }
            }
        }

        var ok = await UpsertAsync(eventId, discordUserId, slotId, "Accepted", ct);
        return new SignupResult(ok, OverflowedToReserve: false);
    }

    private async Task<int> CountAcceptedAsync(
        Guid eventId, List<string> slotIds, ulong discordUserId, CancellationToken ct)
    {
        if (slotIds.Count == 0) return 0;
        return await _db.EventSignups
            .Where(s => s.EventId == eventId
                     && s.SlotId != null
                     && slotIds.Contains(s.SlotId!)
                     && s.Status == "Accepted"
                     && s.DiscordUserId != (long)discordUserId)
            .CountAsync(ct);
    }

    private static string FormatGroupName(string tag) => tag switch
    {
        "heal" => "healers",
        "boondps" => "boon DPS",
        "quick" => "Quickness providers",
        "alac" => "Alacrity providers",
        _ => tag
    };

    public async Task<SignupResult> JoinReserveAsync(Guid eventId, ulong discordUserId, CancellationToken ct = default)
    {
        var entity = await UpsertAsync(eventId, discordUserId, slotId: null, status: "Reserve", ct);
        return new SignupResult(entity, OverflowedToReserve: false);
    }

    /// <summary>Generic accept for events with no role slots defined.</summary>
    public async Task<SignupResult> JoinAcceptAsync(Guid eventId, ulong discordUserId, CancellationToken ct = default)
    {
        var entity = await UpsertAsync(eventId, discordUserId, slotId: null, status: "Accepted", ct);
        return new SignupResult(entity, OverflowedToReserve: false);
    }

    /// <summary>Returns the number of signup rows deleted (0 if the user wasn't signed up).</summary>
    public async Task<int> DropAsync(Guid eventId, ulong discordUserId, CancellationToken ct = default)
    {
        return await _db.EventSignups
            .Where(s => s.EventId == eventId && s.DiscordUserId == (long)discordUserId)
            .DeleteAsync(ct);
    }

    private async Task<EventSignupEntity> UpsertAsync(
        Guid eventId, ulong discordUserId, string? slotId, string status, CancellationToken ct)
    {
        var existing = await _db.EventSignups
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.DiscordUserId == (long)discordUserId, ct);

        // Resolve player_id from the link table — null if the user hasn't linked their GW2 account.
        var playerId = await _db.DiscordUserLinks
            .Where(l => l.DiscordUserId == (long)discordUserId)
            .Select(l => (Guid?)l.PlayerId)
            .FirstOrDefaultAsync(ct);

        var now = DateTimeOffset.UtcNow;
        if (existing == null)
        {
            var entity = new EventSignupEntity
            {
                Id = Guid.NewGuid(),
                EventId = eventId,
                DiscordUserId = (long)discordUserId,
                PlayerId = playerId,
                SlotId = slotId,
                Status = status,
                SignedUpAt = now,
                UpdatedAt = now
            };
            await _db.InsertAsync(entity, token: ct);
            return entity;
        }
        else
        {
            existing.PlayerId = playerId;
            existing.SlotId = slotId;
            existing.Status = status;
            existing.UpdatedAt = now;
            await _db.UpdateAsync(existing, token: ct);
            return existing;
        }
    }
}

public record SignupResult(EventSignupEntity Signup, bool OverflowedToReserve, string? OverflowReason = null);

public record EventSignupRow(
    Guid Id,
    Guid EventId,
    long DiscordUserId,
    Guid? PlayerId,
    string? AccountName,
    string? SlotId,
    string Status,
    DateTimeOffset SignedUpAt);
