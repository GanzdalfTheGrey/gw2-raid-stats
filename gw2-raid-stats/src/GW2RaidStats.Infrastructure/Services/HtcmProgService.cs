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
        var sessions = await _db.Encounters
            .Where(e => e.TriggerId == HtcmTriggerId && e.IsCM && e.DurationMs >= MinDurationMs)
            .GroupBy(e => e.EncounterTime.Date)
            .Select(g => new HtcmSession(
                g.Key,
                g.Count(),
                g.Max(e => e.FurthestPhaseIndex) ?? 0,
                g.Max(e => e.FurthestPhase) ?? "Unknown",
                g.Min(e => e.BossHealthPercentRemaining) ?? 100,
                g.Any(e => e.Success)
            ))
            .OrderByDescending(s => s.Date)
            .ToListAsync(ct);

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
        var bestPull = pulls.OrderBy(p => p.BossHpRemaining).First();
        var totalDuration = TimeSpan.FromMilliseconds(encounters.Sum(e => e.DurationMs));

        return new HtcmSessionDetail(
            date,
            pulls.Count,
            bestPull.FurthestPhase,
            bestPull.FurthestPhaseIndex,
            bestPull.BossHpRemaining,
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

        // Build progression points for each pull
        var pullPoints = encounters.Select((e, i) => new HtcmProgressionPoint(
            i + 1,
            e.EncounterTime,
            e.BossHealthPercentRemaining ?? 100,
            e.FurthestPhase ?? "Unknown",
            e.FurthestPhaseIndex ?? 0,
            e.Success
        )).ToList();

        // Build session-level progression
        var sessionPoints = encounters
            .GroupBy(e => e.EncounterTime.Date)
            .OrderBy(g => g.Key)
            .Select((g, i) => new HtcmSessionProgressionPoint(
                i + 1,
                g.Key,
                g.Min(e => e.BossHealthPercentRemaining) ?? 100,
                g.Max(e => e.FurthestPhaseIndex) ?? 0,
                g.Max(e => e.FurthestPhase) ?? "Unknown",
                g.Count(),
                g.Any(e => e.Success)
            ))
            .ToList();

        // Calculate overall stats
        var bestHp = encounters.Min(e => e.BossHealthPercentRemaining) ?? 100;
        var bestPhaseIndex = encounters.Max(e => e.FurthestPhaseIndex) ?? 0;
        var bestPhase = encounters
            .Where(e => e.FurthestPhaseIndex == bestPhaseIndex)
            .Select(e => e.FurthestPhase)
            .FirstOrDefault() ?? "Unknown";
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
