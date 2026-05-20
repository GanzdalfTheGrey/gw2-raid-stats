using GW2RaidStats.Infrastructure.Database;
using GW2RaidStats.Infrastructure.Database.Entities;
using LinqToDB;
using LinqToDB.Async;

namespace GW2RaidStats.Infrastructure.Services;

/// <summary>
/// Per-player raid-night (Monday / Tuesday) availability for the admin Availability page.
/// One row per player; the page edits everyone's rows. Status values: 0 = unavailable (red),
/// 1 = maybe / one-day-a-week (yellow), 2 = available (green); null = not set.
/// </summary>
public class AvailabilityService
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayers;

    public AvailabilityService(RaidStatsDb db, IncludedPlayerService includedPlayers)
    {
        _db = db;
        _includedPlayers = includedPlayers;
    }

    /// <summary>
    /// Every active (included) player with their availability row, account-name sorted.
    /// Players without a saved row come back with null statuses / note.
    /// </summary>
    public async Task<List<PlayerAvailabilityRow>> GetAllAsync(CancellationToken ct = default)
    {
        var includedAccounts = (await _includedPlayers.GetIncludedAccountNamesAsync(ct)).ToList();
        if (includedAccounts.Count == 0) return new();

        var players = await _db.Players
            .Where(p => includedAccounts.Contains(p.AccountName))
            .Select(p => new { p.Id, p.AccountName })
            .ToListAsync(ct);

        var availability = await _db.PlayerAvailability.ToListAsync(ct);
        var byPlayer = availability.ToDictionary(a => a.PlayerId);

        return players
            .OrderBy(p => p.AccountName, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                byPlayer.TryGetValue(p.Id, out var a);
                return new PlayerAvailabilityRow(
                    p.Id, p.AccountName, a?.MondayStatus, a?.TuesdayStatus, a?.Note);
            })
            .ToList();
    }

    /// <summary>
    /// Upsert a player's whole availability row. The UI sends all three fields on any change.
    /// </summary>
    public async Task UpsertAsync(
        Guid playerId, int? mondayStatus, int? tuesdayStatus, string? note, CancellationToken ct = default)
    {
        var existing = await _db.PlayerAvailability
            .FirstOrDefaultAsync(a => a.PlayerId == playerId, ct);

        if (existing == null)
        {
            await _db.InsertAsync(new PlayerAvailabilityEntity
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                MondayStatus = mondayStatus,
                TuesdayStatus = tuesdayStatus,
                Note = note,
                UpdatedAt = DateTimeOffset.UtcNow
            }, token: ct);
        }
        else
        {
            await _db.PlayerAvailability
                .Where(a => a.Id == existing.Id)
                .Set(a => a.MondayStatus, mondayStatus)
                .Set(a => a.TuesdayStatus, tuesdayStatus)
                .Set(a => a.Note, note)
                .Set(a => a.UpdatedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(ct);
        }
    }
}

public record PlayerAvailabilityRow(
    Guid PlayerId,
    string AccountName,
    int? MondayStatus,
    int? TuesdayStatus,
    string? Note);
