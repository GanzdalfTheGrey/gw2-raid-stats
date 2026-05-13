using GW2RaidStats.Core;
using GW2RaidStats.Infrastructure.Database;
using LinqToDB;
using LinqToDB.Async;

namespace GW2RaidStats.Infrastructure.Services;

/// <summary>
/// Reports Quickness and Alacrity coverage. Two views over the same data:
/// - Session view: per-encounter, per-sub average self-uptime + the booners who provided each boon.
/// - Player view: that player's two slices — "Generation" (sub avg uptime on encounters where they
///   were tagged as that boon's booner) and "Self" (their own uptime on encounters where they weren't).
///
/// Generator tagging comes from the existing PlayerEncounter.Role column populated by LogImportService:
///   heal_quick / dps_quick → Quickness generator
///   heal_alac  / dps_alac  → Alacrity generator
///   heal_*  / pure_dps     → neither for that boon
/// </summary>
public class BoonCoverageService
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayers;

    public BoonCoverageService(RaidStatsDb db, IncludedPlayerService includedPlayers)
    {
        _db = db;
        _includedPlayers = includedPlayers;
    }

    // --- Session view ---

    public async Task<List<EncounterBoonCoverage>> GetCoverageForEncountersAsync(
        IReadOnlyList<Guid> encounterIds, CancellationToken ct = default)
    {
        if (encounterIds.Count == 0) return new();

        var encounters = await _db.Encounters
            .Where(e => encounterIds.Contains(e.Id))
            .Select(e => new
            {
                e.Id,
                e.TriggerId,
                e.BossName,
                e.IsCM,
                e.Success,
                e.DurationMs,
                e.EncounterTime
            })
            .ToListAsync(ct);

        // Pull each player_encounter row in one shot; join account name in
        var rows = await (
            from pe in _db.PlayerEncounters
            join p in _db.Players on pe.PlayerId equals p.Id
            where encounterIds.Contains(pe.EncounterId)
            select new
            {
                pe.EncounterId,
                pe.SquadGroup,
                pe.Profession,
                pe.Role,
                pe.QuicknessSelfUptime,
                pe.AlacritySelfUptime,
                pe.QuicknessGeneration,
                pe.AlacracityGeneration,
                AccountName = p.AccountName
            })
            .ToListAsync(ct);

        var result = new List<EncounterBoonCoverage>(encounters.Count);
        foreach (var enc in encounters.OrderBy(e => e.EncounterTime))
        {
            var encRows = rows.Where(r => r.EncounterId == enc.Id).ToList();

            var subs = encRows
                .Where(r => r.SquadGroup.HasValue)
                .GroupBy(r => r.SquadGroup!.Value)
                .OrderBy(g => g.Key)
                .Select(g => new SubBoonCoverage(
                    SubGroup: g.Key,
                    PlayerCount: g.Count(),
                    AvgQuicknessUptime: NullableMean(g.Select(r => r.QuicknessSelfUptime)),
                    AvgAlacrityUptime: NullableMean(g.Select(r => r.AlacritySelfUptime))))
                .ToList();

            var generators = encRows
                .Where(r => r.SquadGroup.HasValue && r.Role != null && IsBoonRole(r.Role))
                .Select(r => new BoonGeneratorInfo(
                    AccountName: r.AccountName,
                    Profession: r.Profession,
                    Role: r.Role!,
                    SubGroup: r.SquadGroup!.Value,
                    Boon: BoonFromRole(r.Role!),
                    GenerationPct: BoonFromRole(r.Role!) == "Quickness"
                        ? r.QuicknessGeneration
                        : r.AlacracityGeneration))
                .OrderBy(g => g.SubGroup)
                .ThenBy(g => g.Boon)
                .ToList();

            result.Add(new EncounterBoonCoverage(
                EncounterId: enc.Id,
                BossName: WingMapping.CanonicalBossName(enc.TriggerId, enc.BossName),
                IsCM: enc.IsCM,
                Success: enc.Success,
                DurationMs: enc.DurationMs,
                EncounterTime: enc.EncounterTime,
                Subs: subs,
                Generators: generators));
        }
        return result;
    }

    // --- Player view ---

    public async Task<PlayerBoonCoverage> GetPlayerCoverageAsync(
        Guid playerId,
        DateTimeOffset? rangeStart,
        DateTimeOffset? rangeEnd,
        CancellationToken ct = default)
    {
        // 1. Player's own PEs in range, joined to encounter for boss/time
        var playerRows = await (
            from pe in _db.PlayerEncounters
            join e in _db.Encounters on pe.EncounterId equals e.Id
            where pe.PlayerId == playerId
                  && e.DurationMs >= 30_000 // ignore fights under 30s — usually res-pulls or aborted attempts that skew averages
                  && (rangeStart == null || e.EncounterTime >= rangeStart)
                  && (rangeEnd == null || e.EncounterTime <= rangeEnd)
            select new
            {
                pe.EncounterId,
                pe.SquadGroup,
                pe.Profession,
                pe.Role,
                pe.QuicknessSelfUptime,
                pe.AlacritySelfUptime,
                e.TriggerId,
                e.BossName,
                e.IsCM,
                e.EncounterTime
            })
            .ToListAsync(ct);

        if (playerRows.Count == 0)
        {
            return new PlayerBoonCoverage(
                Quickness: BoonSummary.Empty,
                Alacrity: BoonSummary.Empty,
                GuildAverages: GuildBoonAverages.Empty,
                PerBoss: new(),
                PerProfession: new());
        }

        var encounterIds = playerRows.Select(r => r.EncounterId).Distinct().ToList();

        // 2. Sub-mates' self-uptime for the Generation slice
        var subMateRows = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId))
            .Select(pe => new
            {
                pe.EncounterId,
                pe.SquadGroup,
                pe.QuicknessSelfUptime,
                pe.AlacritySelfUptime
            })
            .ToListAsync(ct);

        // Index: (encounterId, subGroup) → [(Q, A)] for sub-mate aggregation
        var subIndex = subMateRows
            .Where(r => r.SquadGroup.HasValue)
            .GroupBy(r => (r.EncounterId, r.SquadGroup!.Value))
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => (Q: r.QuicknessSelfUptime, A: r.AlacritySelfUptime)).ToList());

        // 3. Per-row contribution: which slice each row goes into for Q and for A
        var perRow = new List<(int TriggerId, string BossName, bool IsCM, string Profession, string Role,
                              decimal? QGen, decimal? QSelf, decimal? AGen, decimal? ASelf)>();

        foreach (var row in playerRows)
        {
            decimal? qGen = null, qSelf = null, aGen = null, aSelf = null;

            var subKey = (row.EncounterId, row.SquadGroup ?? -1);
            var subMembers = row.SquadGroup.HasValue && subIndex.TryGetValue(subKey, out var s) ? s : null;

            // Quickness slice
            if (row.Role != null && IsQuicknessRole(row.Role))
            {
                qGen = subMembers != null ? NullableMean(subMembers.Select(x => x.Q)) : null;
            }
            else
            {
                qSelf = row.QuicknessSelfUptime;
            }

            // Alacrity slice
            if (row.Role != null && IsAlacrityRole(row.Role))
            {
                aGen = subMembers != null ? NullableMean(subMembers.Select(x => x.A)) : null;
            }
            else
            {
                aSelf = row.AlacritySelfUptime;
            }

            perRow.Add((row.TriggerId, row.BossName, row.IsCM, row.Profession, row.Role ?? "",
                       qGen, qSelf, aGen, aSelf));
        }

        // 4. Aggregations
        var quickness = AggregateBoon(
            perRow.Select(r => r.QGen),
            perRow.Select(r => r.QSelf));
        var alacrity = AggregateBoon(
            perRow.Select(r => r.AGen),
            perRow.Select(r => r.ASelf));

        // Group by trigger ID, not raw boss name — EI sometimes writes "Cardinal Adina 1" etc.
        // for split-log artifacts. Trigger ID is the canonical boss identity.
        var perBoss = perRow
            .GroupBy(r => (r.TriggerId, r.IsCM))
            .Select(g => new BoonPerBoss(
                BossName: WingMapping.CanonicalBossName(g.Key.TriggerId, g.First().BossName),
                IsCM: g.Key.IsCM,
                Encounters: g.Count(),
                Quickness: AggregateBoon(g.Select(r => r.QGen), g.Select(r => r.QSelf)),
                Alacrity: AggregateBoon(g.Select(r => r.AGen), g.Select(r => r.ASelf))))
            .OrderBy(b => b.BossName)
            .ToList();

        // Profession bucket includes the role (e.g., "Mechanist as dps_quick") so the same character
        // played in different roles doesn't get blurred together — directly answers the
        // "Mirage looks low" / "Mech is fine" comparison the user asked for.
        var perProfession = perRow
            .Where(r => !string.IsNullOrEmpty(r.Profession))
            .GroupBy(r => (r.Profession, r.Role))
            .Select(g => new BoonPerProfession(
                Profession: g.Key.Profession,
                Role: g.Key.Role,
                Encounters: g.Count(),
                Quickness: AggregateBoon(g.Select(r => r.QGen), g.Select(r => r.QSelf)),
                Alacrity: AggregateBoon(g.Select(r => r.AGen), g.Select(r => r.ASelf))))
            .OrderBy(p => p.Profession)
            .ThenBy(p => p.Role)
            .ToList();

        var guildAverages = await GetGuildAveragesAsync(rangeStart, rangeEnd, ct);

        return new PlayerBoonCoverage(
            Quickness: quickness,
            Alacrity: alacrity,
            GuildAverages: guildAverages,
            PerBoss: perBoss,
            PerProfession: perProfession);
    }

    /// <summary>
    /// Mean Q-Gen / Q-Self / A-Gen / A-Self uptime across all included guild members in range.
    /// Sub-mate index for the Generation slices uses ALL squad members on the encounter
    /// (including pugs) — that's what "the sub's uptime you delivered" means. The contributing
    /// rows are filtered to guildies so pug performance doesn't muddy the comparison.
    /// </summary>
    private async Task<GuildBoonAverages> GetGuildAveragesAsync(
        DateTimeOffset? rangeStart, DateTimeOffset? rangeEnd, CancellationToken ct)
    {
        var includedAccounts = await _includedPlayers.GetIncludedAccountNamesAsync(ct);
        if (includedAccounts.Count == 0) return GuildBoonAverages.Empty;

        var guildRows = await (
            from pe in _db.PlayerEncounters
            join e in _db.Encounters on pe.EncounterId equals e.Id
            join p in _db.Players on pe.PlayerId equals p.Id
            where includedAccounts.Contains(p.AccountName)
                  && e.DurationMs >= 30_000
                  && (rangeStart == null || e.EncounterTime >= rangeStart)
                  && (rangeEnd == null || e.EncounterTime <= rangeEnd)
            select new
            {
                pe.EncounterId,
                pe.SquadGroup,
                pe.Role,
                pe.QuicknessSelfUptime,
                pe.AlacritySelfUptime
            })
            .ToListAsync(ct);

        if (guildRows.Count == 0) return GuildBoonAverages.Empty;

        var encIds = guildRows.Select(r => r.EncounterId).Distinct().ToList();
        var subMateRows = await _db.PlayerEncounters
            .Where(pe => encIds.Contains(pe.EncounterId))
            .Select(pe => new { pe.EncounterId, pe.SquadGroup, pe.QuicknessSelfUptime, pe.AlacritySelfUptime })
            .ToListAsync(ct);

        var subIndex = subMateRows
            .Where(r => r.SquadGroup.HasValue)
            .GroupBy(r => (r.EncounterId, r.SquadGroup!.Value))
            .ToDictionary(g => g.Key, g => g.Select(r => (Q: r.QuicknessSelfUptime, A: r.AlacritySelfUptime)).ToList());

        var qGen = new List<decimal>();
        var qSelf = new List<decimal>();
        var aGen = new List<decimal>();
        var aSelf = new List<decimal>();

        foreach (var row in guildRows)
        {
            var subKey = (row.EncounterId, row.SquadGroup ?? -1);
            var subMembers = row.SquadGroup.HasValue && subIndex.TryGetValue(subKey, out var s) ? s : null;

            if (row.Role != null && IsQuicknessRole(row.Role))
            {
                var avg = subMembers != null ? NullableMean(subMembers.Select(x => x.Q)) : null;
                if (avg.HasValue) qGen.Add(avg.Value);
            }
            else if (row.QuicknessSelfUptime.HasValue)
            {
                qSelf.Add(row.QuicknessSelfUptime.Value);
            }

            if (row.Role != null && IsAlacrityRole(row.Role))
            {
                var avg = subMembers != null ? NullableMean(subMembers.Select(x => x.A)) : null;
                if (avg.HasValue) aGen.Add(avg.Value);
            }
            else if (row.AlacritySelfUptime.HasValue)
            {
                aSelf.Add(row.AlacritySelfUptime.Value);
            }
        }

        return new GuildBoonAverages(
            QuicknessGenAvg: qGen.Count > 0 ? qGen.Average() : null,
            QuicknessSelfAvg: qSelf.Count > 0 ? qSelf.Average() : null,
            AlacrityGenAvg: aGen.Count > 0 ? aGen.Average() : null,
            AlacritySelfAvg: aSelf.Count > 0 ? aSelf.Average() : null);
    }

    // --- Trends view ---

    public async Task<List<BoonTrendBucket>> GetPlayerTrendsAsync(
        Guid playerId,
        DateTimeOffset? rangeStart,
        DateTimeOffset? rangeEnd,
        CancellationToken ct = default)
    {
        // Same data load as the player coverage query — we just bucket the per-row results
        // by week instead of aggregating overall.
        var playerRows = await (
            from pe in _db.PlayerEncounters
            join e in _db.Encounters on pe.EncounterId equals e.Id
            where pe.PlayerId == playerId
                  && e.DurationMs >= 30_000 // ignore fights under 30s — usually res-pulls or aborted attempts that skew averages
                  && (rangeStart == null || e.EncounterTime >= rangeStart)
                  && (rangeEnd == null || e.EncounterTime <= rangeEnd)
            select new
            {
                pe.EncounterId,
                pe.SquadGroup,
                pe.Role,
                pe.QuicknessSelfUptime,
                pe.AlacritySelfUptime,
                e.EncounterTime
            })
            .ToListAsync(ct);

        if (playerRows.Count == 0) return new();

        var encounterIds = playerRows.Select(r => r.EncounterId).Distinct().ToList();
        var subMateRows = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId))
            .Select(pe => new
            {
                pe.EncounterId,
                pe.SquadGroup,
                pe.QuicknessSelfUptime,
                pe.AlacritySelfUptime
            })
            .ToListAsync(ct);

        var subIndex = subMateRows
            .Where(r => r.SquadGroup.HasValue)
            .GroupBy(r => (r.EncounterId, r.SquadGroup!.Value))
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => (Q: r.QuicknessSelfUptime, A: r.AlacritySelfUptime)).ToList());

        // Per-row Q/A contributions, tagged with the week bucket
        var perRow = new List<(DateTimeOffset WeekStart, decimal? QGen, decimal? QSelf, decimal? AGen, decimal? ASelf)>();
        foreach (var row in playerRows)
        {
            var subKey = (row.EncounterId, row.SquadGroup ?? -1);
            var subMembers = row.SquadGroup.HasValue && subIndex.TryGetValue(subKey, out var s) ? s : null;

            decimal? qGen = null, qSelf = null, aGen = null, aSelf = null;
            if (row.Role != null && IsQuicknessRole(row.Role))
                qGen = subMembers != null ? NullableMean(subMembers.Select(x => x.Q)) : null;
            else
                qSelf = row.QuicknessSelfUptime;

            if (row.Role != null && IsAlacrityRole(row.Role))
                aGen = subMembers != null ? NullableMean(subMembers.Select(x => x.A)) : null;
            else
                aSelf = row.AlacritySelfUptime;

            perRow.Add((WeekStartOf(row.EncounterTime), qGen, qSelf, aGen, aSelf));
        }

        // Group into weekly buckets and average each metric within the bucket
        return perRow
            .GroupBy(r => r.WeekStart)
            .OrderBy(g => g.Key)
            .Select(g => new BoonTrendBucket(
                WeekStart: g.Key,
                QuicknessGenAvg: NullableMean(g.Select(r => r.QGen)),
                QuicknessGenEncounters: g.Count(r => r.QGen.HasValue),
                QuicknessSelfAvg: NullableMean(g.Select(r => r.QSelf)),
                QuicknessSelfEncounters: g.Count(r => r.QSelf.HasValue),
                AlacrityGenAvg: NullableMean(g.Select(r => r.AGen)),
                AlacrityGenEncounters: g.Count(r => r.AGen.HasValue),
                AlacritySelfAvg: NullableMean(g.Select(r => r.ASelf)),
                AlacritySelfEncounters: g.Count(r => r.ASelf.HasValue)))
            .ToList();
    }

    private static DateTimeOffset WeekStartOf(DateTimeOffset dt)
    {
        // ISO-style week start = Monday in local time of the timestamp
        var local = dt.LocalDateTime.Date;
        var daysSinceMonday = ((int)local.DayOfWeek + 6) % 7;
        var monday = local.AddDays(-daysSinceMonday);
        return new DateTimeOffset(monday, dt.Offset);
    }

    // --- helpers ---

    private static BoonSummary AggregateBoon(IEnumerable<decimal?> genValues, IEnumerable<decimal?> selfValues)
    {
        var gen = genValues.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var self = selfValues.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return new BoonSummary(
            GenerationEncounters: gen.Count,
            GenerationAvg: gen.Count > 0 ? gen.Average() : null,
            SelfEncounters: self.Count,
            SelfAvg: self.Count > 0 ? self.Average() : null);
    }

    private static decimal? NullableMean(IEnumerable<decimal?> values)
    {
        var nonNull = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return nonNull.Count == 0 ? null : nonNull.Average();
    }

    private static bool IsBoonRole(string role) =>
        IsQuicknessRole(role) || IsAlacrityRole(role);

    private static bool IsQuicknessRole(string role) =>
        role == "heal_quick" || role == "dps_quick";

    private static bool IsAlacrityRole(string role) =>
        role == "heal_alac" || role == "dps_alac";

    private static string BoonFromRole(string role) =>
        IsQuicknessRole(role) ? "Quickness" : "Alacrity";
}

// --- DTOs ---

public record EncounterBoonCoverage(
    Guid EncounterId,
    string BossName,
    bool IsCM,
    bool Success,
    int DurationMs,
    DateTimeOffset EncounterTime,
    List<SubBoonCoverage> Subs,
    List<BoonGeneratorInfo> Generators);

public record SubBoonCoverage(
    int SubGroup,
    int PlayerCount,
    decimal? AvgQuicknessUptime,
    decimal? AvgAlacrityUptime);

public record BoonGeneratorInfo(
    string AccountName,
    string Profession,
    string Role,
    int SubGroup,
    string Boon,
    decimal? GenerationPct);

public record PlayerBoonCoverage(
    BoonSummary Quickness,
    BoonSummary Alacrity,
    GuildBoonAverages GuildAverages,
    List<BoonPerBoss> PerBoss,
    List<BoonPerProfession> PerProfession);

public record GuildBoonAverages(
    decimal? QuicknessGenAvg,
    decimal? QuicknessSelfAvg,
    decimal? AlacrityGenAvg,
    decimal? AlacritySelfAvg)
{
    public static GuildBoonAverages Empty => new(null, null, null, null);
}

public record BoonSummary(
    int GenerationEncounters,
    decimal? GenerationAvg,
    int SelfEncounters,
    decimal? SelfAvg)
{
    public static BoonSummary Empty => new(0, null, 0, null);
}

public record BoonPerBoss(
    string BossName,
    bool IsCM,
    int Encounters,
    BoonSummary Quickness,
    BoonSummary Alacrity);

public record BoonPerProfession(
    string Profession,
    string Role,
    int Encounters,
    BoonSummary Quickness,
    BoonSummary Alacrity);

public record BoonTrendBucket(
    DateTimeOffset WeekStart,
    decimal? QuicknessGenAvg, int QuicknessGenEncounters,
    decimal? QuicknessSelfAvg, int QuicknessSelfEncounters,
    decimal? AlacrityGenAvg, int AlacrityGenEncounters,
    decimal? AlacritySelfAvg, int AlacritySelfEncounters);
