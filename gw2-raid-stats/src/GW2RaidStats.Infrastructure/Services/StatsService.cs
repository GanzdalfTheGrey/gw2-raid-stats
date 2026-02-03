using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Infrastructure.Services;

public class StatsService
{
    private readonly RaidStatsDb _db;
    private readonly IgnoredBossService _ignoredBossService;
    private readonly IncludedPlayerService _includedPlayerService;

    // Match LeaderboardService threshold
    private const decimal BoonSupportThreshold = 10m;

    public StatsService(RaidStatsDb db, IgnoredBossService ignoredBossService, IncludedPlayerService includedPlayerService)
    {
        _db = db;
        _ignoredBossService = ignoredBossService;
        _includedPlayerService = includedPlayerService;
    }

    public async Task<DashboardStats> GetDashboardStatsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var startOfMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var startOfYear = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Get ignored bosses for filtering
        var ignoredKeys = await _ignoredBossService.GetIgnoredKeysAsync(ct);

        // Base query excluding ignored bosses
        var baseEncounters = _db.Encounters.AsQueryable();
        if (ignoredKeys.Count > 0)
        {
            // Filter out ignored bosses (can't use HashSet directly in LINQ to SQL)
            var ignoredList = ignoredKeys.ToList();
            baseEncounters = baseEncounters
                .Where(e => !ignoredList.Any(i => i.TriggerId == e.TriggerId && i.IsCM == e.IsCM));
        }

        // Total encounters (excluding ignored)
        var totalEncounters = await baseEncounters.CountAsync(ct);

        // Total kills (successful encounters, excluding ignored)
        var totalKills = await baseEncounters.CountAsync(e => e.Success, ct);

        // Success rate
        var successRate = totalEncounters > 0
            ? Math.Round((double)totalKills / totalEncounters * 100, 1)
            : 0;

        // Active raiders this month (distinct players with encounters this month)
        var activeRaidersQuery = _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.e.EncounterTime >= startOfMonth);

        if (ignoredKeys.Count > 0)
        {
            var ignoredList = ignoredKeys.ToList();
            activeRaidersQuery = activeRaidersQuery
                .Where(x => !ignoredList.Any(i => i.TriggerId == x.e.TriggerId && i.IsCM == x.e.IsCM));
        }

        var activeRaidersThisMonth = await activeRaidersQuery
            .Select(x => x.pe.PlayerId)
            .Distinct()
            .CountAsync(ct);

        // Total raid hours this year (excluding ignored)
        var hoursQuery = baseEncounters.Where(e => e.EncounterTime >= startOfYear);
        var totalDurationMsThisYear = await hoursQuery.SumAsync(e => (long)e.DurationMs, ct);
        var raidHoursThisYear = Math.Round(totalDurationMsThisYear / 1000.0 / 60.0 / 60.0, 1);

        // Total players
        var totalPlayers = await _db.Players.CountAsync(ct);

        return new DashboardStats(
            TotalEncounters: totalEncounters,
            TotalKills: totalKills,
            SuccessRate: successRate,
            ActiveRaidersThisMonth: activeRaidersThisMonth,
            RaidHoursThisYear: raidHoursThisYear,
            TotalPlayers: totalPlayers
        );
    }

    public async Task<List<RecentEncounter>> GetRecentEncountersAsync(int count = 10, CancellationToken ct = default)
    {
        var encounters = await _db.Encounters
            .OrderByDescending(e => e.EncounterTime)
            .Take(count)
            .Select(e => new RecentEncounter(
                e.Id,
                e.BossName,
                e.Success,
                e.IsCM,
                e.EncounterTime,
                e.DurationMs,
                e.LogUrl
            ))
            .ToListAsync(ct);

        return encounters;
    }

    public async Task<WeeklyHighlights> GetWeeklyHighlightsAsync(CancellationToken ct = default)
    {
        var oneWeekAgo = DateTimeOffset.UtcNow.AddDays(-7);

        // Get ignored bosses for filtering
        var ignoredKeys = await _ignoredBossService.GetIgnoredKeysAsync(ct);
        var ignoredList = ignoredKeys.ToList();

        // Get encounters from the past week (excluding ignored)
        var weeklyQuery = _db.Encounters.Where(e => e.EncounterTime >= oneWeekAgo);
        if (ignoredKeys.Count > 0)
        {
            weeklyQuery = weeklyQuery
                .Where(e => !ignoredList.Any(i => i.TriggerId == e.TriggerId && i.IsCM == e.IsCM));
        }

        var weeklyEncounters = await weeklyQuery.CountAsync(ct);
        var weeklyKills = await weeklyQuery.CountAsync(e => e.Success, ct);

        // Top DPS this week (excluding ignored)
        var topDpsQuery = _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Where(x => x.e.EncounterTime >= oneWeekAgo && x.e.Success);

        if (ignoredKeys.Count > 0)
        {
            topDpsQuery = topDpsQuery
                .Where(x => !ignoredList.Any(i => i.TriggerId == x.e.TriggerId && i.IsCM == x.e.IsCM));
        }

        var topDps = await topDpsQuery
            .OrderByDescending(x => x.pe.Dps)
            .Take(1)
            .Select(x => new TopPerformer(
                x.p.AccountName,
                x.pe.CharacterName,
                x.pe.Profession,
                x.pe.Dps,
                x.e.BossName
            ))
            .FirstOrDefaultAsync(ct);

        return new WeeklyHighlights(
            Encounters: weeklyEncounters,
            Kills: weeklyKills,
            TopDps: topDps
        );
    }

    public async Task<PreviousSession?> GetPreviousSessionAsync(CancellationToken ct = default)
    {
        // Find the most recent encounter by encounter time (actual raid date, not upload date)
        var latestEncounter = await _db.Encounters
            .OrderByDescending(e => e.EncounterTime)
            .FirstOrDefaultAsync(ct);

        if (latestEncounter == null) return null;

        // Get all encounters from that same calendar date (preserving timezone)
        // Explicitly calculate local time using UTC + offset to avoid server timezone issues
        var encounterOffset = latestEncounter.EncounterTime.Offset;
        var localDateTime = latestEncounter.EncounterTime.UtcDateTime + encounterOffset;
        // SpecifyKind is needed because UtcDateTime returns Kind=Utc, which can't be used with non-zero offsets
        var sessionDate = DateTime.SpecifyKind(localDateTime.Date, DateTimeKind.Unspecified);
        var sessionStart = new DateTimeOffset(sessionDate, encounterOffset);
        var sessionEnd = sessionStart.AddDays(1);

        var sessionEncounters = await _db.Encounters
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime < sessionEnd)
            .OrderBy(e => e.EncounterTime)
            .ToListAsync(ct);

        if (sessionEncounters.Count == 0) return null;

        var encounters = sessionEncounters.Select(e => new SessionEncounter(
            e.Id,
            e.BossName,
            e.Success,
            e.IsCM,
            e.EncounterTime,
            e.DurationMs,
            e.LogUrl
        )).ToList();

        var totalAttempts = sessionEncounters.Count;
        var totalKills = sessionEncounters.Count(e => e.Success);
        var totalTimeMs = sessionEncounters.Sum(e => e.DurationMs);

        // Calculate downtime: elapsed time from first to last encounter minus time spent on bosses
        var firstEncounter = sessionEncounters.First();
        var lastEncounter = sessionEncounters.Last();
        var totalElapsedMs = (lastEncounter.EncounterTime - firstEncounter.EncounterTime).TotalMilliseconds + lastEncounter.DurationMs;
        var downtimeMs = totalElapsedMs - totalTimeMs;

        // Send the first encounter's time so client can display in viewer's local timezone
        return new PreviousSession(
            SessionTime: firstEncounter.EncounterTime,
            Encounters: encounters,
            TotalAttempts: totalAttempts,
            TotalKills: totalKills,
            TotalTimeSeconds: totalTimeMs / 1000.0,
            DowntimeSeconds: downtimeMs / 1000.0
        );
    }

    public async Task<SessionHighlights> GetSessionHighlightsAsync(CancellationToken ct = default)
    {
        var records = new List<RecordBroken>();
        var milestones = new List<Milestone>();

        // Find the most recent session date
        var latestEncounter = await _db.Encounters
            .OrderByDescending(e => e.EncounterTime)
            .FirstOrDefaultAsync(ct);

        if (latestEncounter == null)
        {
            return new SessionHighlights(records, milestones);
        }

        // Get all encounters from that same calendar date (preserving timezone)
        // Explicitly calculate local time using UTC + offset to avoid server timezone issues
        var encounterOffset = latestEncounter.EncounterTime.Offset;
        var localDateTime = latestEncounter.EncounterTime.UtcDateTime + encounterOffset;
        // SpecifyKind is needed because UtcDateTime returns Kind=Utc, which can't be used with non-zero offsets
        var sessionDate = DateTime.SpecifyKind(localDateTime.Date, DateTimeKind.Unspecified);
        var sessionStart = new DateTimeOffset(sessionDate, encounterOffset);
        var sessionEnd = sessionStart.AddDays(1);

        // Get included players (guild members) - to match leaderboard logic
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = includedAccounts.ToList();

        // Get successful encounters from this session
        var sessionKills = await _db.Encounters
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime < sessionEnd && e.Success)
            .ToListAsync(ct);

        // Check for kill time records
        foreach (var kill in sessionKills)
        {
            // Get the previous best kill time for this boss (before this session)
            var previousBest = await _db.Encounters
                .Where(e => e.TriggerId == kill.TriggerId
                         && e.IsCM == kill.IsCM
                         && e.Success
                         && e.EncounterTime < sessionStart)
                .OrderBy(e => e.DurationMs)
                .FirstOrDefaultAsync(ct);

            if (previousBest == null || kill.DurationMs < previousBest.DurationMs)
            {
                records.Add(new RecordBroken(
                    RecordType: "Kill Time",
                    BossName: kill.BossName,
                    IsCM: kill.IsCM,
                    PlayerName: null,
                    NewValue: kill.DurationMs / 1000.0,
                    PreviousValue: previousBest?.DurationMs / 1000.0,
                    Profession: null
                ));
            }
        }

        // Check for DPS records from this session
        var sessionEncounterIds = sessionKills.Select(e => e.Id).ToList();
        if (sessionEncounterIds.Count > 0)
        {
            // Build query for session player encounters, filtering to included players only
            var sessionQuery = _db.PlayerEncounters
                .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
                .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
                .Where(x => sessionEncounterIds.Contains(x.e.Id));

            if (includedList.Count > 0)
            {
                sessionQuery = sessionQuery.Where(x => includedList.Contains(x.p.AccountName));
            }

            var sessionPlayerEncounters = await sessionQuery.ToListAsync(ct);

            // Group by boss and check for DPS records
            var bossDpsRecords = sessionPlayerEncounters
                .GroupBy(x => new { x.e.TriggerId, x.e.IsCM, x.e.BossName })
                .ToList();

            foreach (var bossGroup in bossDpsRecords)
            {
                var topSessionDps = bossGroup.OrderByDescending(x => x.pe.Dps).FirstOrDefault();
                if (topSessionDps == null) continue;

                // Get previous best DPS for this boss (included players only)
                var previousQuery = _db.PlayerEncounters
                    .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
                    .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
                    .Where(x => x.e.TriggerId == bossGroup.Key.TriggerId
                             && x.e.IsCM == bossGroup.Key.IsCM
                             && x.e.Success
                             && x.e.EncounterTime < sessionStart);

                if (includedList.Count > 0)
                {
                    previousQuery = previousQuery.Where(x => includedList.Contains(x.p.AccountName));
                }

                var previousBestDps = await previousQuery
                    .OrderByDescending(x => x.pe.Dps)
                    .FirstOrDefaultAsync(ct);

                if (previousBestDps == null || topSessionDps.pe.Dps > previousBestDps.pe.Dps)
                {
                    records.Add(new RecordBroken(
                        RecordType: "DPS",
                        BossName: bossGroup.Key.BossName,
                        IsCM: bossGroup.Key.IsCM,
                        PlayerName: topSessionDps.p.AccountName,
                        NewValue: topSessionDps.pe.Dps,
                        PreviousValue: previousBestDps?.pe.Dps,
                        Profession: topSessionDps.pe.Profession
                    ));
                }

                // Check for Boon DPS records (quickness or alacrity providers >= 10% threshold)
                var boonDpsPlayers = bossGroup
                    .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                                (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
                    .OrderByDescending(x => x.pe.Dps)
                    .FirstOrDefault();

                if (boonDpsPlayers != null)
                {
                    var previousBoonQuery = _db.PlayerEncounters
                        .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
                        .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
                        .Where(x => x.e.TriggerId == bossGroup.Key.TriggerId
                                 && x.e.IsCM == bossGroup.Key.IsCM
                                 && x.e.Success
                                 && x.e.EncounterTime < sessionStart
                                 && ((x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                                     (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold));

                    if (includedList.Count > 0)
                    {
                        previousBoonQuery = previousBoonQuery.Where(x => includedList.Contains(x.p.AccountName));
                    }

                    var previousBestBoonDps = await previousBoonQuery
                        .OrderByDescending(x => x.pe.Dps)
                        .FirstOrDefaultAsync(ct);

                    if (previousBestBoonDps == null || boonDpsPlayers.pe.Dps > previousBestBoonDps.pe.Dps)
                    {
                        records.Add(new RecordBroken(
                            RecordType: "Boon DPS",
                            BossName: bossGroup.Key.BossName,
                            IsCM: bossGroup.Key.IsCM,
                            PlayerName: boonDpsPlayers.p.AccountName,
                            NewValue: boonDpsPlayers.pe.Dps,
                            PreviousValue: previousBestBoonDps?.pe.Dps,
                            Profession: boonDpsPlayers.pe.Profession
                        ));
                    }
                }
            }
        }

        // Check for kill milestones (every 50 kills)
        var totalKillsNow = await _db.Encounters.CountAsync(e => e.Success, ct);
        var totalKillsBefore = await _db.Encounters
            .CountAsync(e => e.Success && e.EncounterTime < sessionStart, ct);

        // Check which 50-kill milestones were crossed
        var milestoneBefore = (totalKillsBefore / 50) * 50;
        var milestoneNow = (totalKillsNow / 50) * 50;

        for (var m = milestoneBefore + 50; m <= milestoneNow; m += 50)
        {
            milestones.Add(new Milestone(
                Type: "Total Kills",
                Value: m,
                Description: $"Reached {m} total boss kills!"
            ));
        }

        // Deduplicate records (keep only new records, not first-time records for clarity)
        var newRecords = records
            .Where(r => r.PreviousValue.HasValue)
            .OrderByDescending(r => r.RecordType == "Kill Time" ? (r.PreviousValue!.Value - r.NewValue) / r.PreviousValue!.Value : (r.NewValue - r.PreviousValue!.Value) / r.PreviousValue!.Value)
            .Take(5)
            .ToList();

        return new SessionHighlights(newRecords, milestones);
    }

    /// <summary>
    /// Get "wall of shame" stats for the most recent session.
    /// Deaths are filtered to exclude:
    /// - Deaths within 5 seconds of encounter end (likely squad wipe or /ff)
    /// - Deaths without a preceding downed event (instant death via /ff command)
    /// </summary>
    public async Task<SessionShameStats?> GetSessionShameStatsAsync(CancellationToken ct = default)
    {
        // Find the most recent session date
        var latestEncounter = await _db.Encounters
            .OrderByDescending(e => e.EncounterTime)
            .FirstOrDefaultAsync(ct);

        if (latestEncounter == null) return null;

        // Get session date range
        var encounterOffset = latestEncounter.EncounterTime.Offset;
        var localDateTime = latestEncounter.EncounterTime.UtcDateTime + encounterOffset;
        var sessionDate = DateTime.SpecifyKind(localDateTime.Date, DateTimeKind.Unspecified);
        var sessionStart = new DateTimeOffset(sessionDate, encounterOffset);
        var sessionEnd = sessionStart.AddDays(1);

        // Get encounters for this session with their durations
        var sessionEncounters = await _db.Encounters
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime < sessionEnd)
            .Select(e => new { e.Id, e.DurationMs })
            .ToListAsync(ct);

        if (sessionEncounters.Count == 0) return null;

        var sessionEncounterIds = sessionEncounters.Select(e => e.Id).ToList();
        var encounterDurations = sessionEncounters.ToDictionary(e => e.Id, e => e.DurationMs);

        // Get included players (guild members)
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = includedAccounts.ToList();

        // Get all "Dead" and "Downed" mechanic events for the session
        var mechanicEvents = await _db.MechanicEvents
            .InnerJoin(_db.Players, (m, p) => m.PlayerId == p.Id, (m, p) => new { m, p })
            .Where(x => sessionEncounterIds.Contains(x.m.EncounterId))
            .Where(x => includedList.Contains(x.p.AccountName))
            .Where(x => x.m.MechanicName == "Dead" || x.m.MechanicName == "Downed")
            .Select(x => new
            {
                x.m.EncounterId,
                x.m.PlayerId,
                x.p.AccountName,
                x.m.MechanicName,
                x.m.EventTimeMs
            })
            .ToListAsync(ct);

        // Filter deaths to only count "legitimate" ones
        var deathEvents = mechanicEvents.Where(m => m.MechanicName == "Dead").ToList();
        var downedEvents = mechanicEvents.Where(m => m.MechanicName == "Downed").ToList();

        const int endOfFightThresholdMs = 5000; // Exclude deaths within 5 seconds of encounter end
        const int downedLookbackMs = 15000; // Look for downed event within 15 seconds before death

        var legitimateDeaths = deathEvents.Where(death =>
        {
            // Exclude deaths within 5 seconds of encounter end
            if (encounterDurations.TryGetValue(death.EncounterId, out var durationMs))
            {
                if (death.EventTimeMs > durationMs - endOfFightThresholdMs)
                    return false;
            }

            // Exclude deaths without a preceding downed event (/ff detection)
            var hasDownedBefore = downedEvents.Any(downed =>
                downed.EncounterId == death.EncounterId &&
                downed.PlayerId == death.PlayerId &&
                downed.EventTimeMs < death.EventTimeMs &&
                downed.EventTimeMs >= death.EventTimeMs - downedLookbackMs);

            return hasDownedBefore;
        }).ToList();

        // Count legitimate deaths per player
        var deathsByPlayer = legitimateDeaths
            .GroupBy(d => d.AccountName)
            .Select(g => new { AccountName = g.Key, TotalDeaths = g.Count() })
            .OrderByDescending(p => p.TotalDeaths)
            .ToList();

        // Get downs from PlayerEncounters (still use aggregate for downs)
        var playerDowns = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => sessionEncounterIds.Contains(x.pe.EncounterId))
            .Where(x => includedList.Contains(x.p.AccountName))
            .GroupBy(x => x.p.AccountName)
            .Select(g => new
            {
                AccountName = g.Key,
                TotalDowns = g.Sum(x => x.pe.Downs)
            })
            .ToListAsync(ct);

        if (deathsByPlayer.Count == 0 && playerDowns.Count == 0) return null;

        // Find the player with most legitimate deaths
        var mostDeaths = deathsByPlayer.FirstOrDefault();
        var mostDowns = playerDowns.OrderByDescending(p => p.TotalDowns).FirstOrDefault();

        return new SessionShameStats(
            MostDeathsPlayer: mostDeaths?.AccountName ?? "",
            MostDeathsCount: mostDeaths?.TotalDeaths ?? 0,
            MostDownsPlayer: mostDowns?.AccountName ?? "",
            MostDownsCount: mostDowns?.TotalDowns ?? 0
        );
    }

    public async Task<SessionMvpStats?> GetSessionMvpStatsAsync(CancellationToken ct = default)
    {
        // Find the most recent session date
        var latestEncounter = await _db.Encounters
            .OrderByDescending(e => e.EncounterTime)
            .FirstOrDefaultAsync(ct);

        if (latestEncounter == null) return null;

        // Get session date range
        var encounterOffset = latestEncounter.EncounterTime.Offset;
        var localDateTime = latestEncounter.EncounterTime.UtcDateTime + encounterOffset;
        var sessionDate = DateTime.SpecifyKind(localDateTime.Date, DateTimeKind.Unspecified);
        var sessionStart = new DateTimeOffset(sessionDate, encounterOffset);
        var sessionEnd = sessionStart.AddDays(1);

        // Get encounter IDs for this session (only successful kills)
        var sessionEncounterIds = await _db.Encounters
            .Where(e => e.EncounterTime >= sessionStart && e.EncounterTime < sessionEnd && e.Success)
            .Select(e => e.Id)
            .ToListAsync(ct);

        if (sessionEncounterIds.Count == 0) return null;

        // Get included players (guild members)
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = includedAccounts.ToList();

        // Get player stats aggregated for the session
        var playerStats = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => sessionEncounterIds.Contains(x.pe.EncounterId))
            .Where(x => includedList.Contains(x.p.AccountName))
            .GroupBy(x => x.p.AccountName)
            .Select(g => new
            {
                AccountName = g.Key,
                AvgDps = g.Average(x => (double)x.pe.Dps),
                TotalDeaths = g.Sum(x => x.pe.Deaths),
                EncounterCount = g.Count(),
                AvgQuickness = g.Average(x => (double)(x.pe.QuicknessGeneration ?? 0)),
                AvgAlacrity = g.Average(x => (double)(x.pe.AlacracityGeneration ?? 0))
            })
            .ToListAsync(ct);

        if (playerStats.Count == 0) return null;

        // Top DPS (non-support players - less than 10% boon generation)
        var dpsPlayers = playerStats.Where(p => p.AvgQuickness < 10 && p.AvgAlacrity < 10).ToList();
        var topDps = dpsPlayers.OrderByDescending(p => p.AvgDps).FirstOrDefault();

        // Top Support DPS (players with significant boon generation)
        var supportPlayers = playerStats.Where(p => p.AvgQuickness >= 10 || p.AvgAlacrity >= 10).ToList();
        var topSupport = supportPlayers.OrderByDescending(p => p.AvgDps).FirstOrDefault();

        // Survivor (fewest deaths, min 3 encounters to qualify)
        var survivor = playerStats
            .Where(p => p.EncounterCount >= 3)
            .OrderBy(p => p.TotalDeaths)
            .ThenByDescending(p => p.EncounterCount)
            .FirstOrDefault();

        return new SessionMvpStats(
            TopDpsPlayer: topDps?.AccountName,
            TopDpsValue: topDps != null ? (int)topDps.AvgDps : null,
            TopSupportPlayer: topSupport?.AccountName,
            TopSupportDps: topSupport != null ? (int)topSupport.AvgDps : null,
            SurvivorPlayer: survivor?.AccountName,
            SurvivorDeaths: survivor?.TotalDeaths
        );
    }
}

public record SessionMvpStats(
    string? TopDpsPlayer,
    int? TopDpsValue,
    string? TopSupportPlayer,
    int? TopSupportDps,
    string? SurvivorPlayer,
    int? SurvivorDeaths
);

public record SessionShameStats(
    string MostDeathsPlayer,
    int MostDeathsCount,
    string MostDownsPlayer,
    int MostDownsCount
);

public record DashboardStats(
    int TotalEncounters,
    int TotalKills,
    double SuccessRate,
    int ActiveRaidersThisMonth,
    double RaidHoursThisYear,
    int TotalPlayers
);

public record RecentEncounter(
    Guid Id,
    string BossName,
    bool Success,
    bool IsCM,
    DateTimeOffset EncounterTime,
    int DurationMs,
    string? LogUrl
);

public record WeeklyHighlights(
    int Encounters,
    int Kills,
    TopPerformer? TopDps
);

public record TopPerformer(
    string AccountName,
    string CharacterName,
    string Profession,
    int Value,
    string BossName
);

public record PreviousSession(
    DateTimeOffset SessionTime,
    List<SessionEncounter> Encounters,
    int TotalAttempts,
    int TotalKills,
    double TotalTimeSeconds,
    double DowntimeSeconds
);

public record SessionEncounter(
    Guid Id,
    string BossName,
    bool Success,
    bool IsCM,
    DateTimeOffset EncounterTime,
    int DurationMs,
    string? LogUrl
);

public record SessionHighlights(
    List<RecordBroken> Records,
    List<Milestone> Milestones
);

public record RecordBroken(
    string RecordType,
    string BossName,
    bool IsCM,
    string? PlayerName,
    double NewValue,
    double? PreviousValue,
    string? Profession
);

public record Milestone(
    string Type,
    int Value,
    string Description
);
