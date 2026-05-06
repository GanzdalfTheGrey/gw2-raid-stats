using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Infrastructure.Services;

/// <summary>
/// Service for Harvest Temple CM progression tracking
/// </summary>
public class HtcmProgService
{
    private readonly RaidStatsDb _db;

    // HTCM trigger ID
    private const int HtcmTriggerId = 43488;

    // Minimum fight duration to count (30 seconds)
    private const int MinDurationMs = 30000;

    // Canonical phase order for HTCM progression
    // This maps phase names to their canonical progression index
    // Higher index = further into the fight = better progression
    private static readonly Dictionary<string, int> CanonicalPhaseOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        // Full Fight is a meta-phase, not a real progression point
        { "Full Fight", 0 },

        // Phase 1: First purification and first three dragons
        { "Purification 1", 100 },
        { "Heart 1 Breakbar 1", 110 },
        { "Heart 1 Breakbar 2", 111 },
        { "Heart 1 Breakbar 3", 112 },
        { "Jormag", 200 },
        { "Primordus", 300 },
        { "Kralkatorrik", 400 },

        // Phase 2: Void Time Caster (mini-boss)
        { "Void Time Caster Breakbar 1", 450 },
        { "Void Time Caster Breakbar 2", 451 },
        { "Purification 2", 500 },
        { "Void Time Caster", 550 },
        { "Heart 2 Breakbar 1", 560 },
        { "Heart 2 Breakbar 2", 561 },
        { "Heart 2 Breakbar 3", 562 },

        // Phase 3: Mordremoth and Zhaitan with Void Giants
        { "Mordremoth", 600 },
        { "Void Giant 1", 650 },
        { "Void Giant 1 Breakbar 1", 651 },
        { "Void Giant 1 Breakbar 2", 652 },
        { "Void Giant 2", 660 },
        { "Void Giant 2 Breakbar 1", 661 },
        { "Void Giant 2 Breakbar 2", 662 },
        { "Zhaitan", 700 },

        // Phase 4: Soo-Won phases
        { "Purification 3", 750 },
        { "Heart 3 Breakbar 1", 760 },
        { "Heart 3 Breakbar 2", 761 },
        { "Heart 3 Breakbar 3", 762 },
        { "Soo-Won 1", 800 },
        { "Void Amalgamate", 850 },
        { "Void Amalgamate Breakbar 1", 851 },
        { "Void Amalgamate Breakbar 2", 852 },
        { "Soo-Won 2", 900 },

        // Success/completion
        { "Success", 1000 }
    };

    /// <summary>
    /// Gets the canonical progression index for a phase name.
    /// Higher values indicate further progression into the fight.
    /// Tolerates EI naming variations (different punctuation, suffixes, abbreviations) by
    /// normalising and falling back to keyword detection so an unknown variant still maps
    /// to its progression bucket instead of being silently treated as "no progression".
    /// </summary>
    public static int GetCanonicalPhaseIndex(string? phaseName)
    {
        if (string.IsNullOrEmpty(phaseName))
            return 0;

        var name = phaseName.Trim();

        // Try exact match first
        if (CanonicalPhaseOrder.TryGetValue(name, out var index))
            return index;

        // Normalised match (strip spaces/hyphens/case) so "soo-won 1", "SooWon 1",
        // "Soo Won 1" all match "Soo-Won 1".
        var normalisedName = Normalise(name);
        foreach (var (pattern, value) in CanonicalPhaseOrder)
        {
            if (Normalise(pattern) == normalisedName)
                return value;
        }

        // Partial match (longest pattern wins so "Heart 3 Breakbar 1" beats "Heart 3").
        foreach (var (pattern, value) in CanonicalPhaseOrder.OrderByDescending(kv => kv.Key.Length))
        {
            if (name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) ||
                Normalise(name).StartsWith(Normalise(pattern), StringComparison.Ordinal))
                return value;
        }

        // Keyword fallback for variants we haven't explicitly listed. Order matters —
        // later/deeper phases are checked first so a name containing both "Soo" and a
        // number disambiguates correctly.
        foreach (var (keyword, value) in PhaseKeywordFallbacks)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        // Unknown phases are treated as "earlier than any known phase" so they don't
        // steal the "best progression" spot from a Soo-Won pull just because their name
        // wasn't in the map.
        return 1;
    }

    private static string Normalise(string s)
        => s.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");

    // Ordered most-progressed → least-progressed. First Contains-match wins.
    private static readonly (string Keyword, int Value)[] PhaseKeywordFallbacks =
    {
        ("soo-won 2", 900),
        ("soo won 2", 900),
        ("soowon 2",  900),
        ("amalgamate", 850),
        ("soo-won",   800),
        ("soo won",   800),
        ("soowon",    800),
        ("heart 3",   760),
        ("zhaitan",   700),
        ("void giant", 650),
        ("mordremoth", 600),
        ("mordy",     600),
        ("heart 2",   560),
        ("time caster", 550),
        ("kralk",     400),
        ("primordus", 300),
        ("jormag",    200),
        ("heart 1",   110),
    };

    // Key mechanics to track for HTCM
    // Note: These are the short names from Elite Insights mechanics data
    public static readonly string[] TrackedMechanics =
    {
        "Last.L",          // Last Laugh
        "Last.L.Ch",       // Champion Last Laugh
        "Red.B",           // Red Bait
        "Spread.B",        // Spread Bait
        "Spread.O",        // Spread Overlap
        "Void.D",          // Void Debuff
        "ShckWv.H",        // Mordremoth Shockwave
        "Mord.Poi.H",      // Mordremoth Poison
        "Giant.Puke.H",    // Giant Puke
        "Giant.Scream.H",  // Giant Scream
        "Giant.Stomp.H",   // Giant Stomp
        "Kralk.Beam.H",    // Kralkatorrik Beam
        "Kralk.Riv.H",     // Kralkatorrik River
        "Kralk.Met.H",     // Kralkatorrik Meteor
        "Zhai.Poi.H"       // Zhaitan Poison
    };

    public HtcmProgService(RaidStatsDb db)
    {
        _db = db;
    }

    /// <summary>
    /// Get all available sessions (days) with HTCM attempts
    /// </summary>
    public async Task<List<HtcmSession>> GetAvailableSessionsAsync(CancellationToken ct = default)
    {
        // Load encounters first, then group in memory to properly correlate phase name with phase index
        var encounters = await _db.Encounters
            .Where(e => e.TriggerId == HtcmTriggerId && e.IsCM && e.DurationMs >= MinDurationMs)
            .Select(e => new {
                e.EncounterTime,
                e.FurthestPhase,
                e.FurthestPhaseIndex,
                e.BossHealthPercentRemaining,
                e.Success
            })
            .ToListAsync(ct);

        var sessions = encounters
            .GroupBy(e => e.EncounterTime.Date)
            .Select(g =>
            {
                // Use canonical phase ordering to determine best progression
                var bestEntry = g
                    .Select(e => new { e.FurthestPhase, CanonicalIndex = GetCanonicalPhaseIndex(e.FurthestPhase) })
                    .OrderByDescending(x => x.CanonicalIndex)
                    .First();

                return new HtcmSession(
                    g.Key,
                    g.Count(),
                    bestEntry.CanonicalIndex,
                    bestEntry.FurthestPhase ?? "Unknown",
                    g.Min(e => e.BossHealthPercentRemaining) ?? 100,
                    g.Any(e => e.Success)
                );
            })
            .OrderByDescending(s => s.Date)
            .ToList();

        return sessions;
    }

    /// <summary>
    /// Get detailed summary for a specific session (day)
    /// </summary>
    public async Task<HtcmSessionDetail?> GetSessionDetailAsync(DateTime date, CancellationToken ct = default)
    {
        var startOfDay = new DateTimeOffset(date.Date, TimeSpan.Zero);
        var endOfDay = startOfDay.AddDays(1);

        // Get all encounters for this session
        var encounters = await _db.Encounters
            .Where(e => e.TriggerId == HtcmTriggerId &&
                        e.IsCM &&
                        e.DurationMs >= MinDurationMs &&
                        e.EncounterTime >= startOfDay &&
                        e.EncounterTime < endOfDay)
            .OrderBy(e => e.EncounterTime)
            .ToListAsync(ct);

        if (encounters.Count == 0)
            return null;

        // Get encounter IDs for mechanics query
        var encounterIds = encounters.Select(e => e.Id).ToList();

        // Get phase stats for the session
        var phaseStats = await _db.EncounterPhaseStats
            .Where(ps => encounterIds.Contains(ps.EncounterId))
            .ToListAsync(ct);

        // Get DPS data for the session
        var dpsData = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId))
            .GroupBy(pe => pe.EncounterId)
            .Select(g => new {
                EncounterId = g.Key,
                SquadDps = g.Sum(pe => pe.Dps),
                DownCount = g.Sum(pe => pe.Downs),
                DeathCount = g.Sum(pe => pe.Deaths)
            })
            .ToListAsync(ct);

        // Get mechanic events per player for this session (with ICD for grouping)
        var mechanicEvents = await _db.MechanicEvents
            .InnerJoin(_db.Players, (m, p) => m.PlayerId == p.Id, (m, p) => new { m, p })
            .Where(x => encounterIds.Contains(x.m.EncounterId) &&
                        TrackedMechanics.Contains(x.m.MechanicName))
            .OrderBy(x => x.m.EventTimeMs)
            .Select(x => new { x.p.AccountName, x.m.MechanicName, x.m.EventTimeMs })
            .ToListAsync(ct);

        // Group mechanics using ICD (events within ICD ms count as 1 occurrence)
        var mechanicCounts = mechanicEvents
            .GroupBy(x => new { x.AccountName, x.MechanicName })
            .Select(g =>
            {
                var times = g.OrderBy(e => e.EventTimeMs).Select(e => e.EventTimeMs).ToList();
                var icd = MechanicIcdHelper.GetIcd(g.Key.MechanicName);
                var count = MechanicIcdHelper.CountWithIcd(times, icd);
                return new { g.Key.AccountName, g.Key.MechanicName, Count = count };
            })
            .ToList();

        // Get first death per encounter (mechanic name "Dead" in Elite Insights)
        var firstDeaths = await _db.MechanicEvents
            .InnerJoin(_db.Players, (m, p) => m.PlayerId == p.Id, (m, p) => new { m, p })
            .Where(x => encounterIds.Contains(x.m.EncounterId) &&
                        x.m.MechanicName == "Dead")
            .OrderBy(x => x.m.EventTimeMs)
            .ToListAsync(ct);

        var firstDeathByEncounter = firstDeaths
            .GroupBy(x => x.m.EncounterId)
            .ToDictionary(
                g => g.Key,
                g => g.First()
            );

        // Build pull data
        var pulls = new List<HtcmPull>();
        for (int i = 0; i < encounters.Count; i++)
        {
            var encounter = encounters[i];
            var dps = dpsData.FirstOrDefault(d => d.EncounterId == encounter.Id);

            // Get phase stats for this encounter, excluding "Full Fight" (index 0)
            var encounterPhaseStats = phaseStats
                .Where(ps => ps.EncounterId == encounter.Id && ps.PhaseIndex > 0)
                .OrderBy(ps => ps.PhaseIndex)
                .Select(ps => new HtcmPhaseStats(
                    ps.PhaseIndex,
                    ps.PhaseName,
                    ps.SquadDps,
                    TimeSpan.FromMilliseconds(ps.DurationMs)
                ))
                .ToList();

            // Get first death for this encounter
            string? firstDeathPlayer = null;
            TimeSpan? firstDeathTime = null;
            if (firstDeathByEncounter.TryGetValue(encounter.Id, out var firstDeath))
            {
                firstDeathPlayer = firstDeath.p.AccountName;
                firstDeathTime = TimeSpan.FromMilliseconds(firstDeath.m.EventTimeMs);
            }

            pulls.Add(new HtcmPull(
                encounter.Id,
                i + 1,
                encounter.EncounterTime,
                TimeSpan.FromMilliseconds(encounter.DurationMs),
                encounter.FurthestPhase ?? "Unknown",
                encounter.FurthestPhaseIndex ?? 0,
                encounter.BossHealthPercentRemaining ?? 100,
                dps?.SquadDps ?? 0,
                dps?.DownCount ?? 0,
                dps?.DeathCount ?? 0,
                encounter.Success,
                encounter.LogUrl,
                encounterPhaseStats,
                firstDeathPlayer,
                firstDeathTime
            ));
        }

        // Build player mechanic breakdown
        var playerMechanics = mechanicCounts
            .GroupBy(m => m.AccountName)
            .Select(g => new HtcmPlayerMechanics(
                g.Key,
                g.ToDictionary(x => x.MechanicName, x => x.Count)
            ))
            .OrderBy(p => p.AccountName)
            .ToList();

        // Calculate session stats
        // Best phase = furthest phase reached (using canonical phase ordering)
        var bestPull = pulls
            .Select(p => new { Pull = p, CanonicalIndex = GetCanonicalPhaseIndex(p.FurthestPhase) })
            .OrderByDescending(x => x.CanonicalIndex)
            .First();
        var bestPhaseIndex = bestPull.CanonicalIndex;
        var bestPhase = bestPull.Pull.FurthestPhase;
        // Best HP = lowest boss HP remaining
        var bestHpRemaining = pulls.Min(p => p.BossHpRemaining);
        var totalDuration = TimeSpan.FromMilliseconds(encounters.Sum(e => e.DurationMs));

        return new HtcmSessionDetail(
            date,
            pulls.Count,
            bestPhase,
            bestPhaseIndex,
            bestHpRemaining,
            totalDuration,
            pulls.Average(p => p.Duration.TotalSeconds),
            (int)pulls.Average(p => p.SquadDps),
            pulls.Sum(p => p.Downs),
            pulls.Sum(p => p.Deaths),
            pulls.Any(p => p.Success),
            pulls,
            playerMechanics
        );
    }

    /// <summary>
    /// Get progression data for all sessions (for charts)
    /// </summary>
    public async Task<HtcmProgressionData> GetProgressionDataAsync(CancellationToken ct = default)
    {
        // Get all HTCM encounters
        var encounters = await _db.Encounters
            .Where(e => e.TriggerId == HtcmTriggerId && e.IsCM && e.DurationMs >= MinDurationMs)
            .OrderBy(e => e.EncounterTime)
            .Select(e => new {
                e.EncounterTime,
                e.BossHealthPercentRemaining,
                e.FurthestPhase,
                e.FurthestPhaseIndex,
                e.Success,
                e.DurationMs
            })
            .ToListAsync(ct);

        if (encounters.Count == 0)
            return new HtcmProgressionData(
                0, null, null, 100, null,
                new List<HtcmProgressionPoint>(),
                new List<HtcmSessionProgressionPoint>()
            );

        // Build progression points for each pull (using canonical phase ordering)
        var pullPoints = encounters.Select((e, i) => new HtcmProgressionPoint(
            i + 1,
            e.EncounterTime,
            e.BossHealthPercentRemaining ?? 100,
            e.FurthestPhase ?? "Unknown",
            GetCanonicalPhaseIndex(e.FurthestPhase),
            e.Success
        )).ToList();

        // Build session-level progression (using canonical phase ordering)
        var sessionPoints = encounters
            .GroupBy(e => e.EncounterTime.Date)
            .OrderBy(g => g.Key)
            .Select((g, i) =>
            {
                // Find the best phase using canonical ordering
                var bestEntry = g
                    .Select(e => new { e.FurthestPhase, CanonicalIndex = GetCanonicalPhaseIndex(e.FurthestPhase) })
                    .OrderByDescending(x => x.CanonicalIndex)
                    .First();

                return new HtcmSessionProgressionPoint(
                    i + 1,
                    g.Key,
                    g.Min(e => e.BossHealthPercentRemaining) ?? 100,
                    bestEntry.CanonicalIndex,
                    bestEntry.FurthestPhase ?? "Unknown",
                    g.Count(),
                    g.Any(e => e.Success)
                );
            })
            .ToList();

        // Calculate overall stats (using canonical phase ordering)
        var bestHp = encounters.Min(e => e.BossHealthPercentRemaining) ?? 100;
        var overallBest = encounters
            .Select(e => new { e.FurthestPhase, CanonicalIndex = GetCanonicalPhaseIndex(e.FurthestPhase) })
            .OrderByDescending(x => x.CanonicalIndex)
            .First();
        var bestPhaseIndex = overallBest.CanonicalIndex;
        var bestPhase = overallBest.FurthestPhase ?? "Unknown";
        var firstAttempt = encounters.Min(e => e.EncounterTime);

        return new HtcmProgressionData(
            encounters.Count,
            firstAttempt,
            bestPhase,
            bestHp,
            encounters.Any(e => e.Success) ? encounters.Where(e => e.Success).Min(e => e.EncounterTime) : null,
            pullPoints,
            sessionPoints
        );
    }

    /// <summary>
    /// Get overall phase DPS averages across all sessions
    /// </summary>
    public async Task<List<HtcmPhaseDpsAverage>> GetOverallPhaseDpsAsync(CancellationToken ct = default)
    {
        // Get all HTCM encounter IDs
        var encounterIds = await _db.Encounters
            .Where(e => e.TriggerId == HtcmTriggerId && e.IsCM && e.DurationMs >= MinDurationMs)
            .Select(e => e.Id)
            .ToListAsync(ct);

        if (encounterIds.Count == 0)
            return new List<HtcmPhaseDpsAverage>();

        // Get phase stats and calculate averages (exclude "Full Fight" phase index 0)
        var phaseAverages = await _db.EncounterPhaseStats
            .Where(ps => encounterIds.Contains(ps.EncounterId) && ps.PhaseIndex > 0)
            .GroupBy(ps => new { ps.PhaseIndex, ps.PhaseName })
            .Select(g => new HtcmPhaseDpsAverage(
                g.Key.PhaseIndex,
                g.Key.PhaseName,
                (int)g.Average(ps => ps.SquadDps),
                g.Count()
            ))
            .OrderBy(p => p.PhaseIndex)
            .ToListAsync(ct);

        return phaseAverages;
    }

    /// <summary>
    /// Get phase DPS trends across sessions
    /// </summary>
    public async Task<List<HtcmPhaseDpsTrend>> GetPhaseDpsTrendsAsync(CancellationToken ct = default)
    {
        // Get all HTCM encounters with their dates
        var encounters = await _db.Encounters
            .Where(e => e.TriggerId == HtcmTriggerId && e.IsCM && e.DurationMs >= MinDurationMs)
            .Select(e => new { e.Id, SessionDate = e.EncounterTime.Date })
            .ToListAsync(ct);

        if (encounters.Count == 0)
            return new List<HtcmPhaseDpsTrend>();

        var encounterIds = encounters.Select(e => e.Id).ToList();
        var encounterDates = encounters.ToDictionary(e => e.Id, e => e.SessionDate);

        // Get all phase stats (exclude "Full Fight" phase index 0)
        var phaseStats = await _db.EncounterPhaseStats
            .Where(ps => encounterIds.Contains(ps.EncounterId) && ps.PhaseIndex > 0)
            .ToListAsync(ct);

        // Group by phase and session to calculate averages
        var phasesBySession = phaseStats
            .GroupBy(ps => new { ps.PhaseIndex, ps.PhaseName })
            .OrderBy(g => g.Key.PhaseIndex)
            .Select(phaseGroup =>
            {
                var sessionAverages = phaseGroup
                    .GroupBy(ps => encounterDates[ps.EncounterId])
                    .OrderBy(g => g.Key)
                    .Select(sessionGroup => new HtcmPhaseDpsSessionAverage(
                        sessionGroup.Key,
                        (int)sessionGroup.Average(ps => ps.SquadDps),
                        sessionGroup.Count()
                    ))
                    .ToList();

                return new HtcmPhaseDpsTrend(
                    phaseGroup.Key.PhaseIndex,
                    phaseGroup.Key.PhaseName,
                    sessionAverages
                );
            })
            .ToList();

        return phasesBySession;
    }

    /// <summary>
    /// Get all unique mechanics recorded for HTCM encounters (for debugging/discovery)
    /// </summary>
    public async Task<List<HtcmMechanicInfo>> GetAllMechanicsAsync(CancellationToken ct = default)
    {
        // Get all HTCM encounter IDs
        var encounterIds = await _db.Encounters
            .Where(e => e.TriggerId == HtcmTriggerId && e.IsCM && e.DurationMs >= MinDurationMs)
            .Select(e => e.Id)
            .ToListAsync(ct);

        if (encounterIds.Count == 0)
            return new List<HtcmMechanicInfo>();

        // Get all unique mechanics with counts
        var mechanics = await _db.MechanicEvents
            .Where(m => encounterIds.Contains(m.EncounterId))
            .GroupBy(m => new { m.MechanicName, m.MechanicFullName, m.Description })
            .Select(g => new HtcmMechanicInfo(
                g.Key.MechanicName,
                g.Key.MechanicFullName ?? "",
                g.Key.Description ?? "",
                g.Count()
            ))
            .OrderByDescending(m => m.Count)
            .ToListAsync(ct);

        return mechanics;
    }

    /// <summary>
    /// Get mechanic trends across sessions
    /// </summary>
    public async Task<List<HtcmMechanicTrend>> GetMechanicTrendsAsync(CancellationToken ct = default)
    {
        // Get all HTCM encounter IDs grouped by session
        var sessions = await _db.Encounters
            .Where(e => e.TriggerId == HtcmTriggerId && e.IsCM && e.DurationMs >= MinDurationMs)
            .GroupBy(e => e.EncounterTime.Date)
            .Select(g => new { Date = g.Key, EncounterIds = g.Select(e => e.Id).ToList() })
            .OrderBy(s => s.Date)
            .ToListAsync(ct);

        if (sessions.Count == 0)
            return new List<HtcmMechanicTrend>();

        var allEncounterIds = sessions.SelectMany(s => s.EncounterIds).ToList();

        // Get mechanic events per session (with ICD for grouping)
        var mechanicsBySession = await _db.MechanicEvents
            .InnerJoin(_db.Encounters, (m, e) => m.EncounterId == e.Id, (m, e) => new { m, e })
            .InnerJoin(_db.Players, (x, p) => x.m.PlayerId == p.Id, (x, p) => new { x.m, x.e, p })
            .Where(x => allEncounterIds.Contains(x.m.EncounterId) &&
                        TrackedMechanics.Contains(x.m.MechanicName))
            .OrderBy(x => x.m.EventTimeMs)
            .Select(x => new { x.m.MechanicName, x.p.AccountName, x.m.EventTimeMs, SessionDate = x.e.EncounterTime.Date })
            .ToListAsync(ct);

        // Build trends with ICD grouping
        var trends = TrackedMechanics.Select(mechanic =>
        {
            var sessionCounts = sessions.Select(session =>
            {
                // Group by player within session, then apply ICD grouping
                var sessionEvents = mechanicsBySession
                    .Where(m => m.MechanicName == mechanic && m.SessionDate == session.Date)
                    .ToList();

                var icd = MechanicIcdHelper.GetIcd(mechanic);
                var count = sessionEvents
                    .GroupBy(e => e.AccountName)
                    .Sum(playerGroup =>
                    {
                        var times = playerGroup.OrderBy(e => e.EventTimeMs).Select(e => e.EventTimeMs).ToList();
                        return MechanicIcdHelper.CountWithIcd(times, icd);
                    });

                return new HtcmMechanicSessionCount(session.Date, count);
            }).ToList();

            return new HtcmMechanicTrend(mechanic, sessionCounts);
        }).ToList();

        return trends;
    }
}

// DTOs
public record HtcmSession(
    DateTime Date,
    int PullCount,
    int BestPhaseIndex,
    string BestPhase,
    decimal BestBossHpRemaining,
    bool HasKill
);

public record HtcmSessionDetail(
    DateTime Date,
    int PullCount,
    string BestPhase,
    int BestPhaseIndex,
    decimal BestBossHpRemaining,
    TimeSpan TotalTime,
    double AverageFightDuration,
    int AverageSquadDps,
    int TotalDowns,
    int TotalDeaths,
    bool HasKill,
    List<HtcmPull> Pulls,
    List<HtcmPlayerMechanics> PlayerMechanics
);

public record HtcmPull(
    Guid EncounterId,
    int PullNumber,
    DateTimeOffset Time,
    TimeSpan Duration,
    string FurthestPhase,
    int FurthestPhaseIndex,
    decimal BossHpRemaining,
    int SquadDps,
    int Downs,
    int Deaths,
    bool Success,
    string? LogUrl,
    List<HtcmPhaseStats> PhaseStats,
    string? FirstDeathPlayer,
    TimeSpan? FirstDeathTime
);

public record HtcmPhaseStats(
    int PhaseIndex,
    string PhaseName,
    int SquadDps,
    TimeSpan Duration
);

public record HtcmPlayerMechanics(
    string AccountName,
    Dictionary<string, int> MechanicCounts
);

public record HtcmProgressionData(
    int TotalPulls,
    DateTimeOffset? FirstAttempt,
    string? BestPhase,
    decimal BestBossHpRemaining,
    DateTimeOffset? FirstKill,
    List<HtcmProgressionPoint> PullProgression,
    List<HtcmSessionProgressionPoint> SessionProgression
);

public record HtcmProgressionPoint(
    int PullNumber,
    DateTimeOffset Time,
    decimal BossHpRemaining,
    string FurthestPhase,
    int FurthestPhaseIndex,
    bool Success
);

public record HtcmSessionProgressionPoint(
    int SessionNumber,
    DateTime Date,
    decimal BestBossHpRemaining,
    int BestPhaseIndex,
    string BestPhase,
    int PullCount,
    bool HasKill
);

public record HtcmMechanicTrend(
    string MechanicName,
    List<HtcmMechanicSessionCount> SessionCounts
);

public record HtcmMechanicSessionCount(
    DateTime Date,
    int Count
);

public record HtcmPhaseDpsAverage(
    int PhaseIndex,
    string PhaseName,
    int AverageDps,
    int SampleCount
);

public record HtcmPhaseDpsTrend(
    int PhaseIndex,
    string PhaseName,
    List<HtcmPhaseDpsSessionAverage> SessionAverages
);

public record HtcmPhaseDpsSessionAverage(
    DateTime Date,
    int AverageDps,
    int PullCount
);

public record HtcmMechanicInfo(
    string ShortName,
    string FullName,
    string Description,
    int Count
);
