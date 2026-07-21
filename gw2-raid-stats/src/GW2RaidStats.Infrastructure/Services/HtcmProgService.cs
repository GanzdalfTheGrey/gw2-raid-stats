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
    public const int HtcmTriggerId = 43488;

    // Minimum fight duration to count (30 seconds)
    public const int MinDurationMs = 30000;

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
        { "Void Giant 3", 665 },
        { "Void Giant 3 Breakbar 1", 666 },
        // Modern EI combines all three giants into a single "Giants" damage phase
        // while still emitting per-giant breakbar sub-phases.
        { "Giants", 670 },
        { "Zhaitan", 700 },

        // Phase 4: Soo-Won phases
        { "Purification 3", 750 },
        { "Heart 3 Breakbar 1", 760 },
        { "Heart 3 Breakbar 2", 761 },
        { "Heart 3 Breakbar 3", 762 },
        { "Void Saltspray Dragon", 780 },
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
        ("saltspray", 780),
        ("heart 3",   760),
        ("zhaitan",   700),
        ("giants",    670),
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

    // Burst-comparison phase groups (used by squad/per-player DPS calc and the
    // session burst average). Exact main-damage-phase match only — concurrent
    // breakbar sub-phases would double-count damage and duration if included.
    // "Giants" is the modern combined-phase name; "Void Giant 1/2/3" cover older
    // EI versions that emit each giant individually.
    public static readonly string[] TimecasterPhases = { "Void Time Caster" };
    public static readonly string[] GiantsPhases = { "Giants", "Void Giant 1", "Void Giant 2", "Void Giant 3" };
    public static readonly string[] SaltsprayPhases = { "Void Saltspray Dragon" };

    // Main dragon damage phases, collapsed into a single group for the Discord session
    // summary. Primordus is deliberately excluded: its arena heavily favours 1200-range
    // builds, so including it would rank players by class rather than by performance.
    public static readonly string[] CombinedDragonPhases =
    {
        "Jormag", "Kralkatorrik", "Mordremoth", "Zhaitan", "Soo-Won 1", "Soo-Won 2"
    };

    // Debilitated-aggregate phase groups (used by ComputePlayerSlice for the Phase
    // Insights session column). Widened to include breakbar sub-phases for Giants
    // and Saltspray because EI records buff uptime independently on each phase,
    // with the main phase's "active duration" EXCLUDING sub-phase time. So when
    // the buff is only applied during a breakbar, the main phase reads 0% and the
    // breakbar reads 100% — both correct in their own frames, but neither tells
    // you "% of the burst window the player was debilitated". Combining them via
    // sum(presence × active_duration) / main_window_duration recovers that number.
    // Timecaster's breakbars are sequential (Purification 2 sits between them and
    // the damage phase) so they're left out.
    private static readonly string[] TimecasterDebilPhases = TimecasterPhases;
    private static readonly string[] GiantsDebilPhases =
    {
        "Giants", "Void Giant 1", "Void Giant 2", "Void Giant 3",
        "Void Giant 1 Breakbar 1", "Void Giant 1 Breakbar 2",
        "Void Giant 2 Breakbar 1", "Void Giant 2 Breakbar 2",
        "Void Giant 3 Breakbar 1", "Void Giant 3 Breakbar 2",
    };
    private static readonly string[] SaltsprayDebilPhases =
    {
        "Void Saltspray Dragon",
        "Void Saltspray Dragon Breakbar 1", "Void Saltspray Dragon Breakbar 2",
    };

    // Sub-phase ↔ main-phase mapping for combined-segment uptime computation.
    // Used by ComputePlayerSlice to know which sub-phases sit inside which main
    // phase so it can subtract sub durations from the main's active window
    // (the way EI computes presence for the main phase).
    private static readonly Dictionary<string, string[]> SubPhasesByMain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Giants"] = new[] {
            "Void Giant 1 Breakbar 1", "Void Giant 1 Breakbar 2",
            "Void Giant 2 Breakbar 1", "Void Giant 2 Breakbar 2",
            "Void Giant 3 Breakbar 1", "Void Giant 3 Breakbar 2",
        },
        ["Void Giant 1"] = new[] { "Void Giant 1 Breakbar 1", "Void Giant 1 Breakbar 2" },
        ["Void Giant 2"] = new[] { "Void Giant 2 Breakbar 1", "Void Giant 2 Breakbar 2" },
        ["Void Giant 3"] = new[] { "Void Giant 3 Breakbar 1", "Void Giant 3 Breakbar 2" },
        ["Void Saltspray Dragon"] = new[] {
            "Void Saltspray Dragon Breakbar 1", "Void Saltspray Dragon Breakbar 2",
        },
    };

    private static bool IsSubPhaseOf(string subName, string mainName) =>
        SubPhasesByMain.TryGetValue(mainName, out var subs) &&
        subs.Any(s => string.Equals(s, subName, StringComparison.OrdinalIgnoreCase));

    public static bool MatchesAny(string phaseName, string[] candidates) =>
        candidates.Any(c => string.Equals(c, phaseName, StringComparison.OrdinalIgnoreCase));

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

        // Per-player per-phase stats (migration 032). Empty when no HTCM pull in this
        // session has been imported with the new code yet, in which case the burst /
        // deaths / debilitated slices in the response are simply empty.
        var playerPhaseRaw = await _db.PlayerEncounterPhaseStats
            .Join(_db.Players, ps => ps.PlayerId, p => p.Id, (ps, p) => new { Stats = ps, p.AccountName })
            .Where(x => encounterIds.Contains(x.Stats.EncounterId))
            .ToListAsync(ct);
        var playerPhaseStatsAll = playerPhaseRaw
            .Select(x => new PlayerPhaseRow(x.Stats, x.AccountName))
            .ToList();
        var playerPhaseByEncounter = playerPhaseStatsAll
            .GroupBy(r => r.Stats.EncounterId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // (encounter, phase) → duration lookup used by both the per-pull debilitated
        // chips and the session-level player slice to convert EI's active-basis
        // uptime % into a phase-relative %.
        var phaseDurationByKey = phaseStats.ToDictionary(
            ps => (ps.EncounterId, ps.PhaseName), ps => ps.DurationMs);

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

            // Per-phase debilitated readout for the Phase Breakdown table. One entry per
            // player with phase-relative uptime > 0 in that phase, sorted desc. Phase-
            // relative scales EI's active-basis % down for players who spent part of
            // the phase dead, so the number reflects what the burst actually lost to
            // the debuff rather than overstating it for largely-dead players. AvgStacks
            // pulls EI's per-phase average stack count alongside the % uptime.
            var pullPlayerStatsForDebil = playerPhaseByEncounter.TryGetValue(encounter.Id, out var pps)
                ? pps : new List<PlayerPhaseRow>();
            var playerDebilByPhase = pullPlayerStatsForDebil
                .Select(r =>
                {
                    var rel = phaseDurationByKey.TryGetValue((r.Stats.EncounterId, r.Stats.PhaseName), out var phaseMs)
                        ? ToPhaseRelativeDebilPct(r.Stats, phaseMs)
                        : 0m;
                    return (Row: r, RelPct: rel);
                })
                .Where(x => x.RelPct > 0m)
                .GroupBy(x => x.Row.Stats.PhaseName)
                .ToDictionary(g => g.Key, g => g
                    .OrderByDescending(x => x.RelPct)
                    .Select(x => new HtcmPhaseDebilEntry(x.Row.AccountName, x.RelPct, x.Row.Stats.DebilitatedAvgStacks))
                    .ToList());

            // Get phase stats for this encounter, excluding "Full Fight" (index 0)
            var encounterPhaseStats = phaseStats
                .Where(ps => ps.EncounterId == encounter.Id && ps.PhaseIndex > 0)
                .OrderBy(ps => ps.PhaseIndex)
                .Select(ps => new HtcmPhaseStats(
                    ps.PhaseIndex,
                    ps.PhaseName,
                    ps.SquadDps,
                    TimeSpan.FromMilliseconds(ps.DurationMs),
                    playerDebilByPhase.TryGetValue(ps.PhaseName, out var debil) ? debil : null
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

            // Burst groups (Timecaster / Giants / Saltspray) — null when no player-phase
            // rows exist for this encounter (pre-migration-032 import).
            HtcmRunBurstGroups? burstGroups = null;
            if (playerPhaseByEncounter.TryGetValue(encounter.Id, out var pullPlayerStats))
            {
                var pullPhaseStatEntities = phaseStats.Where(ps => ps.EncounterId == encounter.Id).ToList();
                burstGroups = new HtcmRunBurstGroups(
                    Timecaster: ComputeGroupBurst(TimecasterPhases, pullPlayerStats, pullPhaseStatEntities),
                    Giants: ComputeGroupBurst(GiantsPhases, pullPlayerStats, pullPhaseStatEntities),
                    Saltspray: ComputeGroupBurst(SaltsprayPhases, pullPlayerStats, pullPhaseStatEntities));
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
                firstDeathTime,
                burstGroups
            ));
        }

        // Per-session per-player slice across the three burst phase groups.
        var playerPhaseSessionStats = playerPhaseStatsAll
            .GroupBy(r => r.AccountName)
            .Select(g => new HtcmPlayerPhaseSessionStat(
                g.Key,
                Timecaster: ComputePlayerSlice(g, TimecasterPhases, TimecasterDebilPhases, phaseDurationByKey),
                Giants: ComputePlayerSlice(g, GiantsPhases, GiantsDebilPhases, phaseDurationByKey),
                Saltspray: ComputePlayerSlice(g, SaltsprayPhases, SaltsprayDebilPhases, phaseDurationByKey)))
            .OrderBy(s => s.AccountName)
            .ToList();

        // Session-wide burst averages — same computation as the per-pull bursts but
        // aggregated across every pull in the session. Lets the user spot whether a
        // given pull was a high or low outlier relative to typical session performance.
        HtcmRunBurstGroups? sessionBurstAverages = null;
        if (playerPhaseStatsAll.Count > 0)
        {
            sessionBurstAverages = new HtcmRunBurstGroups(
                Timecaster: ComputeGroupBurst(TimecasterPhases, playerPhaseStatsAll, phaseStats),
                Giants: ComputeGroupBurst(GiantsPhases, playerPhaseStatsAll, phaseStats),
                Saltspray: ComputeGroupBurst(SaltsprayPhases, playerPhaseStatsAll, phaseStats));
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
            playerMechanics,
            playerPhaseSessionStats,
            sessionBurstAverages
        );
    }

    // Combined damage / combined duration across the phase-group's underlying phases
    // (e.g. Giants = Giant 1 + Giant 2). Returns null when no matching phase reached.
    //
    // Per-player DPS uses a per-player denominator (sum of phase durations across only
    // the phases that player has a row in) rather than the squad total. Matters for
    // session averages: a player who skipped some pulls shouldn't get their DPS
    // diluted by the duration of pulls they weren't in. Per-pull values are unchanged
    // because every player has rows for every phase of an encounter they were in.
    private static HtcmPhaseGroupBurst? ComputeGroupBurst(
        string[] phaseNames,
        IReadOnlyList<PlayerPhaseRow> pullPlayerStats,
        IReadOnlyList<Database.Entities.EncounterPhaseStatEntity> pullPhaseStats)
    {
        var matchingPhases = pullPhaseStats.Where(ps => MatchesAny(ps.PhaseName, phaseNames)).ToList();
        if (matchingPhases.Count == 0) return null;

        var totalDurationMs = matchingPhases.Sum(ps => ps.DurationMs);
        if (totalDurationMs <= 0) return null;

        // Distinct encounters reaching the phase group — for per-pull this is always 1,
        // for session-wide it's the number of pulls. Used by the UI to display average
        // duration per pull instead of the meaningless total time across all pulls.
        var pullCount = matchingPhases.Select(p => p.EncounterId).Distinct().Count();

        var groupRows = pullPlayerStats.Where(r => MatchesAny(r.Stats.PhaseName, phaseNames)).ToList();
        var squadDamage = groupRows.Sum(r => r.Stats.Damage);
        var squadDps = (int)(squadDamage * 1000L / totalDurationMs);

        var phaseDurationByKey = matchingPhases.ToDictionary(
            p => (p.EncounterId, p.PhaseName), p => p.DurationMs);

        var playerBursts = groupRows
            .GroupBy(r => r.AccountName)
            .Select(g =>
            {
                long damage = g.Sum(r => r.Stats.Damage);
                long playerDurationMs = g.Sum(r =>
                    phaseDurationByKey.TryGetValue((r.Stats.EncounterId, r.Stats.PhaseName), out var d) ? d : 0L);
                int dps = playerDurationMs > 0 ? (int)(damage * 1000L / playerDurationMs) : 0;
                return new HtcmPlayerBurst(g.Key, dps);
            })
            .OrderByDescending(p => p.Dps)
            .ToList();

        return new HtcmPhaseGroupBurst(squadDps, totalDurationMs, pullCount, playerBursts);
    }

    private static HtcmPlayerPhaseSlice ComputePlayerSlice(
        IEnumerable<PlayerPhaseRow> rows,
        string[] mainPhaseNames,
        string[] debilPhaseNames,
        Dictionary<(Guid EncounterId, string PhaseName), int> phaseDurationByKey)
    {
        var rowList = rows.ToList();
        var mainFiltered = rowList.Where(r => MatchesAny(r.Stats.PhaseName, mainPhaseNames)).ToList();
        // Death stats stay on the strict main-phase list so a death during a Giants
        // breakbar doesn't double-count against both the main and breakbar entries.
        var deaths = mainFiltered.Sum(r => r.Stats.DeadCount);
        var deadAtStart = mainFiltered.Count(r => r.Stats.DeadAtPhaseStart);

        // For each pull, compute the combined segment uptime + avg stacks by
        // summing buff_time across the main phase and its sub-phases. EI's
        // presence on the main phase is computed against the main duration MINUS
        // sub-phase durations, so we mirror that here: main_active_window =
        // main_duration − sum(sub_durations) − dead − down. Each row's contribution
        // is presence × that row's active window, divided in the end by the total
        // main-phase window. Per-pull values are then averaged across pulls where
        // the player picked up the buff at all.
        var pullUptimes = new List<decimal>();
        var pullStacks = new List<decimal>();
        var rowsByEncounter = rowList
            .Where(r => MatchesAny(r.Stats.PhaseName, debilPhaseNames))
            .GroupBy(r => r.Stats.EncounterId);

        foreach (var encGroup in rowsByEncounter)
        {
            var encounterRows = encGroup.ToList();
            var encMainRows = encounterRows.Where(r => MatchesAny(r.Stats.PhaseName, mainPhaseNames)).ToList();
            if (encMainRows.Count == 0) continue;

            long totalBuffMs = 0;
            long mainWindowMs = 0;
            decimal totalStackWeightedMs = 0;
            long totalStackWeight = 0;

            foreach (var main in encMainRows)
            {
                if (!phaseDurationByKey.TryGetValue((main.Stats.EncounterId, main.Stats.PhaseName), out var mainDur)) continue;
                mainWindowMs += mainDur;

                // Sub-phases of this specific main row, in this same encounter.
                var subsOfThisMain = encounterRows
                    .Where(s => IsSubPhaseOf(s.Stats.PhaseName, main.Stats.PhaseName))
                    .ToList();
                var subDurSum = subsOfThisMain.Sum(s =>
                    phaseDurationByKey.TryGetValue((s.Stats.EncounterId, s.Stats.PhaseName), out var d) ? d : 0);

                // Main's active window for buff purposes excludes sub time + dead + down.
                var mainActive = mainDur - subDurSum - main.Stats.DeadDurationMs - main.Stats.DownDurationMs;
                if (mainActive > 0)
                {
                    if (main.Stats.DebilitatedUptimePct is decimal mPct && mPct > 0)
                        totalBuffMs += (long)(mPct * mainActive / 100m);
                    if (main.Stats.DebilitatedAvgStacks is decimal mStacks && mStacks > 0)
                    {
                        totalStackWeightedMs += mStacks * mainActive;
                        totalStackWeight += mainActive;
                    }
                }

                // Sub-phases each contribute presence × their own active window.
                foreach (var sub in subsOfThisMain)
                {
                    if (!phaseDurationByKey.TryGetValue((sub.Stats.EncounterId, sub.Stats.PhaseName), out var subDur)) continue;
                    var subActive = subDur - sub.Stats.DeadDurationMs - sub.Stats.DownDurationMs;
                    if (subActive <= 0) continue;
                    if (sub.Stats.DebilitatedUptimePct is decimal sPct && sPct > 0)
                        totalBuffMs += (long)(sPct * subActive / 100m);
                    if (sub.Stats.DebilitatedAvgStacks is decimal sStacks && sStacks > 0)
                    {
                        totalStackWeightedMs += sStacks * subActive;
                        totalStackWeight += subActive;
                    }
                }
            }

            if (mainWindowMs > 0 && totalBuffMs > 0)
                pullUptimes.Add((decimal)totalBuffMs / mainWindowMs * 100m);
            if (totalStackWeight > 0)
                pullStacks.Add(totalStackWeightedMs / totalStackWeight);
        }

        return new HtcmPlayerPhaseSlice(
            Deaths: deaths,
            DeadAtPhaseStart: deadAtStart,
            DebilUptimeAvgPct: pullUptimes.Count == 0 ? null : pullUptimes.Average(),
            DebilAvgStacks: pullStacks.Count == 0 ? null : pullStacks.Average());
    }

    // DebilitatedUptimePct comes from EI's BuffUptimesActive.Presence field — true
    // uptime % (0-100) of phase ACTIVE time (dead + down excluded). Scale to a
    // phase-relative % so players who spent part of the phase dead are devalued
    // proportionally rather than reading as 100% when they were debilitated for the
    // 5s they were alive in a 30s phase.
    //
    //   phase_relative_% = active_uptime_% × (phase_ms − dead_ms − down_ms) / phase_ms
    //
    // Players alive the full phase get exactly the EI number. Players dead chunks
    // of the phase get proportionally less, reflecting what the squad really lost.
    private static decimal ToPhaseRelativeDebilPct(
        Database.Entities.PlayerEncounterPhaseStatEntity stats, int phaseDurationMs)
    {
        if (stats.DebilitatedUptimePct is not { } activeUptimePct || activeUptimePct <= 0m) return 0m;
        if (phaseDurationMs <= 0) return 0m;
        var activeMs = phaseDurationMs - stats.DeadDurationMs - stats.DownDurationMs;
        if (activeMs <= 0) return 0m;
        return activeUptimePct * activeMs / phaseDurationMs;
    }

    private record PlayerPhaseRow(Database.Entities.PlayerEncounterPhaseStatEntity Stats, string AccountName);

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
    List<HtcmPlayerMechanics> PlayerMechanics,
    List<HtcmPlayerPhaseSessionStat>? PlayerPhaseStats = null,
    HtcmRunBurstGroups? SessionBurstAverages = null
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
    TimeSpan? FirstDeathTime,
    HtcmRunBurstGroups? BurstGroups = null
);

// Per-run burst per phase-group (Timecaster / Giants / Saltspray). Squad totals plus
// each player's contribution within the group. Null inside a group means the phase
// wasn't reached in this pull.
public record HtcmRunBurstGroups(
    HtcmPhaseGroupBurst? Timecaster,
    HtcmPhaseGroupBurst? Giants,
    HtcmPhaseGroupBurst? Saltspray);

// DurationMs is the SUM of phase durations across the encounters covered by this
// burst record. PullCount is the number of distinct encounters reaching the phase
// group, so per-pull average duration = DurationMs / PullCount. For per-pull bursts
// PullCount is always 1, so the displayed "average" equals the single pull's duration.
public record HtcmPhaseGroupBurst(int SquadDps, int DurationMs, int PullCount, List<HtcmPlayerBurst> Players);

public record HtcmPlayerBurst(string AccountName, int Dps);

// Per-session per-player slice across the three burst phase-groups. Deaths counts
// EI's DeadCount events that occurred during the group; DeadAtPhaseStart counts
// how many pulls in the group's phases the player walked in already dead. Debil
// uptime is the simple average across pulls where the phase was reached.
public record HtcmPlayerPhaseSessionStat(
    string AccountName,
    HtcmPlayerPhaseSlice Timecaster,
    HtcmPlayerPhaseSlice Giants,
    HtcmPlayerPhaseSlice Saltspray);

public record HtcmPlayerPhaseSlice(int Deaths, int DeadAtPhaseStart, decimal? DebilUptimeAvgPct, decimal? DebilAvgStacks);

public record HtcmPhaseStats(
    int PhaseIndex,
    string PhaseName,
    int SquadDps,
    TimeSpan Duration,
    List<HtcmPhaseDebilEntry>? PlayerDebil = null
);

// Per-phase debilitated readout used in the Phase Breakdown table to explain low
// burst — only players with uptime > 0 in that phase are emitted, sorted by
// uptime descending so the heaviest hitters surface first. AvgStacks is the
// EI BuffUptimesActive.Uptime field (average stack count over the phase active
// time, 0-5 for Debilitated).
public record HtcmPhaseDebilEntry(string AccountName, decimal UptimePct, decimal? AvgStacks);

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
