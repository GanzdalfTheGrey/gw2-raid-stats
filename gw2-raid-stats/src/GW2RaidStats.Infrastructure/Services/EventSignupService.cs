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
    /// Sign up for a specific role slot. If the slot is already at capacity (excluding
    /// the requesting user), the user is placed in Reserve and OverflowedToReserve=true
    /// so callers can ephemerally explain the bump.
    /// </summary>
    public async Task<SignupResult> JoinSlotAsync(
        Guid eventId, ulong discordUserId, string slotId, int slotCapacity, CancellationToken ct = default)
    {
        var currentInSlot = await _db.EventSignups
            .Where(s => s.EventId == eventId
                     && s.SlotId == slotId
                     && s.Status == "Accepted"
                     && s.DiscordUserId != (long)discordUserId)
            .CountAsync(ct);

        var overflow = currentInSlot >= slotCapacity;
        var entity = await UpsertAsync(
            eventId, discordUserId,
            overflow ? null : slotId,
            overflow ? "Reserve" : "Accepted",
            ct);
        return new SignupResult(entity, overflow);
    }

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

public record SignupResult(EventSignupEntity Signup, bool OverflowedToReserve);

public record EventSignupRow(
    Guid Id,
    Guid EventId,
    long DiscordUserId,
    Guid? PlayerId,
    string? AccountName,
    string? SlotId,
    string Status,
    DateTimeOffset SignedUpAt);
