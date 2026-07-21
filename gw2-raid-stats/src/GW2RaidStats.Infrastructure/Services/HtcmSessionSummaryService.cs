using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Infrastructure.Services;

/// <summary>
/// Builds the per-session HTCM progression summary posted to Discord on prog nights
/// (HTCM attempts with no kill). Every "Max" figure is best-ever across all sessions,
/// so the whole thing is computed from an all-time query set and then sliced by date.
/// </summary>
public class HtcmSessionSummaryService
{
    private readonly RaidStatsDb _db;
    private readonly HtcmProgService _htcmProgService;
    private readonly IncludedPlayerService _includedPlayerService;

    // EI mechanic short names. "Orb Push" fires per channel tick and is ICD-grouped
    // via MechanicIcdHelper; "Jaws.H" (Primordus Jaws) is a discrete hit.
    private const string OrbPushMechanic = "Orb Push";
    private const string PrimordusJawsMechanic = "Jaws.H";

    // MVDPS category weights, summing to 100. Each player's value is normalised against
    // the session leader in that category (leader = full weight), so the scale
    // self-calibrates instead of relying on fixed damage-per-rip conversion constants.
    // Constraints: burst == dps, orbs == rips, burst == 3 × orbs. That gives
    // 2b + 2o = 100 with b = 3o, so o = 12.5 and b = 37.5.
    private const double BurstWeight = 37.5;
    private const double DpsWeight = 37.5;
    private const double OrbWeight = 12.5;
    private const double RipWeight = 12.5;

    public HtcmSessionSummaryService(
        RaidStatsDb db,
        HtcmProgService htcmProgService,
        IncludedPlayerService includedPlayerService)
    {
        _db = db;
        _htcmProgService = htcmProgService;
        _includedPlayerService = includedPlayerService;
    }

    /// <summary>
    /// Returns the summary for the given session date, or null when that date has no
    /// HTCM attempts. Phase-derived sections are empty for sessions imported before
    /// migration 032 until a rescan backfills player_encounter_phase_stats.
    /// </summary>
    public async Task<HtcmSessionSummary?> GetSummaryAsync(DateTime sessionDate, CancellationToken ct = default)
    {
        var detail = await _htcmProgService.GetSessionDetailAsync(sessionDate, ct);
        if (detail == null) return null;

        var included = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includedList = included.ToList();

        // All-time HTCM encounters, so "best ever" columns don't need a second pass.
        var allEncounters = await _db.Encounters
            .Where(e => e.TriggerId == HtcmProgService.HtcmTriggerId
                     && e.IsCM
                     && e.DurationMs >= HtcmProgService.MinDurationMs)
            .Select(e => new { e.Id, e.EncounterTime })
            .ToListAsync(ct);

        var sessionDateByEncounter = allEncounters.ToDictionary(e => e.Id, e => e.EncounterTime.Date);
        var allEncounterIds = allEncounters.Select(e => e.Id).ToList();
        if (allEncounterIds.Count == 0) return null;

        var phaseNames = HtcmProgService.TimecasterPhases
            .Concat(HtcmProgService.GiantsPhases)
            .Concat(HtcmProgService.SaltsprayPhases)
            .Concat(HtcmProgService.CombinedDragonPhases)
            .Distinct()
            .ToList();

        // Per-player damage on every phase that feeds a burst group, all-time. Deliberately
        // NOT filtered to guild members: squad DPS means the whole squad, pugs included, so
        // it lines up with the figure on the HTCM prog page. The per-player tables filter to
        // included accounts further down.
        var phaseRows = await _db.PlayerEncounterPhaseStats
            .InnerJoin(_db.Players, (ps, p) => ps.PlayerId == p.Id, (ps, p) => new { ps, p })
            .Where(x => allEncounterIds.Contains(x.ps.EncounterId)
                     && phaseNames.Contains(x.ps.PhaseName))
            .Select(x => new PhaseDamageRow(x.ps.EncounterId, x.p.AccountName, x.ps.PhaseName, x.ps.Damage))
            .ToListAsync(ct);
        var guildPhaseRows = phaseRows.Where(r => included.Contains(r.AccountName)).ToList();

        // Phase durations, used as the DPS denominator.
        var phaseDurations = await _db.EncounterPhaseStats
            .Where(ps => allEncounterIds.Contains(ps.EncounterId) && phaseNames.Contains(ps.PhaseName))
            .Select(ps => new { ps.EncounterId, ps.PhaseName, ps.DurationMs })
            .ToListAsync(ct);
        var durationByKey = phaseDurations
            .GroupBy(d => (d.EncounterId, d.PhaseName))
            .ToDictionary(g => g.Key, g => g.First().DurationMs);

        var burstGroups = new List<HtcmSummaryBurstGroup>
        {
            BuildBurstGroup("Timecaster", HtcmProgService.TimecasterPhases, sessionDate,
                phaseRows, guildPhaseRows, durationByKey, sessionDateByEncounter, detail.SessionBurstAverages?.Timecaster),
            BuildBurstGroup("Giants", HtcmProgService.GiantsPhases, sessionDate,
                phaseRows, guildPhaseRows, durationByKey, sessionDateByEncounter, detail.SessionBurstAverages?.Giants),
            BuildBurstGroup("Saltspray", HtcmProgService.SaltsprayPhases, sessionDate,
                phaseRows, guildPhaseRows, durationByKey, sessionDateByEncounter, detail.SessionBurstAverages?.Saltspray),
        };

        var dragons = BuildDragonRows(sessionDate, guildPhaseRows, durationByKey, sessionDateByEncounter);
        var orbPushes = await BuildOrbPushesAsync(sessionDate, allEncounterIds, includedList, sessionDateByEncounter, ct);
        var boonRips = await BuildBoonRipsAsync(sessionDate, allEncounterIds, includedList, sessionDateByEncounter, ct);
        var shame = await BuildShameAsync(detail, included, ct);

        var mvdps = ComputeMvdps(burstGroups, dragons, orbPushes, boonRips);

        return new HtcmSessionSummary(
            Date: sessionDate,
            PullCount: detail.PullCount,
            BestPhase: detail.BestPhase,
            BestBossHpRemaining: detail.BestBossHpRemaining,
            BurstGroups: burstGroups,
            Dragons: dragons,
            OrbPushes: orbPushes,
            BoonRips: boonRips,
            Mvdps: mvdps,
            Shame: shame);
    }

    // Session avg/top of per-pull total damage, plus the all-time per-pull best.
    private static HtcmSummaryBurstGroup BuildBurstGroup(
        string name,
        string[] groupPhases,
        DateTime sessionDate,
        List<PhaseDamageRow> allPhaseRows,
        List<PhaseDamageRow> guildPhaseRows,
        Dictionary<(Guid, string), int> durationByKey,
        Dictionary<Guid, DateTime> sessionDateByEncounter,
        HtcmPhaseGroupBurst? squadBurst)
    {
        var perPull = AggregatePerPull(groupPhases, guildPhaseRows, durationByKey);
        var players = BuildStatRows(perPull, sessionDate, sessionDateByEncounter, p => p.Damage, includeDps: true);

        return new HtcmSummaryBurstGroup(
            name,
            PullsReached: squadBurst?.PullCount ?? 0,
            AverageDurationMs: squadBurst is { PullCount: > 0 } b ? b.DurationMs / b.PullCount : 0,
            SquadDps: squadBurst?.SquadDps ?? 0,
            SquadDpsAllTime: ComputeSquadDps(groupPhases, allPhaseRows, durationByKey, encounterFilter: null),
            Players: players);
    }

    // Squad DPS = total squad damage across the group's phases / the summed phase
    // durations. Same basis as HtcmProgService.ComputeGroupBurst, so the "tonight"
    // figure from the prog page and the all-time figure here are comparable.
    private static int ComputeSquadDps(
        string[] groupPhases,
        List<PhaseDamageRow> phaseRows,
        Dictionary<(Guid, string), int> durationByKey,
        HashSet<Guid>? encounterFilter)
    {
        var rows = phaseRows
            .Where(r => HtcmProgService.MatchesAny(r.PhaseName, groupPhases))
            .Where(r => encounterFilter == null || encounterFilter.Contains(r.EncounterId))
            .ToList();
        if (rows.Count == 0) return 0;

        var damage = rows.Sum(r => r.Damage);
        // Durations are per (encounter, phase), so de-duplicate across the players
        // that share each phase before summing.
        var durationMs = rows
            .Select(r => (r.EncounterId, r.PhaseName))
            .Distinct()
            .Sum(key => durationByKey.TryGetValue(key, out var d) ? (long)d : 0L);

        return durationMs > 0 ? (int)(damage * 1000L / durationMs) : 0;
    }

    // Dragons report damage and DPS as independent avg/top/max series — "best-ever DPS"
    // is its own figure, not the DPS of whichever pull happened to do the most damage.
    private static List<HtcmSummaryDragonRow> BuildDragonRows(
        DateTime sessionDate,
        List<PhaseDamageRow> phaseRows,
        Dictionary<(Guid, string), int> durationByKey,
        Dictionary<Guid, DateTime> sessionDateByEncounter)
    {
        var perPull = AggregatePerPull(HtcmProgService.CombinedDragonPhases, phaseRows, durationByKey);
        var damage = BuildStatRows(perPull, sessionDate, sessionDateByEncounter, p => p.Damage);
        var dps = BuildStatRows(perPull, sessionDate, sessionDateByEncounter, p => p.Dps)
            .ToDictionary(r => r.AccountName);

        return damage
            .Select(d =>
            {
                var p = dps[d.AccountName];
                return new HtcmSummaryDragonRow(
                    d.AccountName,
                    DamageAvg: d.Avg, DamageTop: d.Top, DamageMax: d.Max,
                    DpsAvg: p.Avg, DpsTop: p.Top, DpsMax: p.Max,
                    IsNewBest: d.IsNewBest || p.IsNewBest);
            })
            .OrderByDescending(r => r.DamageAvg)
            .ToList();
    }

    // Collapses phase rows into one (encounter, player) entry per pull: total damage
    // across the group's phases, and DPS over only the phases that player has rows in
    // (a player absent from a pull must not have their DPS diluted by its duration).
    private static List<PullValue> AggregatePerPull(
        string[] groupPhases,
        List<PhaseDamageRow> phaseRows,
        Dictionary<(Guid, string), int> durationByKey)
    {
        return phaseRows
            .Where(r => HtcmProgService.MatchesAny(r.PhaseName, groupPhases))
            .GroupBy(r => (r.EncounterId, r.AccountName))
            .Select(g =>
            {
                var damage = g.Sum(r => r.Damage);
                var durationMs = g.Sum(r =>
                    durationByKey.TryGetValue((r.EncounterId, r.PhaseName), out var d) ? (long)d : 0L);
                var dps = durationMs > 0 ? damage * 1000d / durationMs : 0d;
                return new PullValue(g.Key.EncounterId, g.Key.AccountName, damage, dps);
            })
            .Where(p => p.Damage > 0)
            .ToList();
    }

    /// <param name="includeDps">
    /// Populates the DPS figures alongside the damage ones so the burst tables can show
    /// each as a parenthetical, giving a read on how long that burst window ran. DpsTop
    /// and DpsMax are the DPS of the pull that produced the top / best-ever damage, not
    /// separate DPS maxima.
    /// </param>
    private static List<HtcmSummaryStatRow> BuildStatRows(
        List<PullValue> perPull,
        DateTime sessionDate,
        Dictionary<Guid, DateTime> sessionDateByEncounter,
        Func<PullValue, double> selector,
        bool includeDps = false)
    {
        return perPull
            .GroupBy(p => p.AccountName)
            .Select(g =>
            {
                var tonightPulls = g
                    .Where(p => sessionDateByEncounter.TryGetValue(p.EncounterId, out var d) && d == sessionDate)
                    .ToList();
                if (tonightPulls.Count == 0) return null;

                var tonight = tonightPulls.Select(selector).ToList();
                var topPull = tonightPulls.OrderByDescending(selector).First();
                var maxPull = g.OrderByDescending(selector).First();
                var top = selector(topPull);
                var max = selector(maxPull);
                return new HtcmSummaryStatRow(
                    g.Key,
                    Avg: tonight.Average(),
                    Top: top,
                    Max: max,
                    // Tonight's pulls are part of the all-time set, so matching the
                    // all-time max means tonight set it.
                    IsNewBest: top >= max,
                    DpsAvg: includeDps ? tonightPulls.Average(p => p.Dps) : null,
                    DpsTop: includeDps ? topPull.Dps : null,
                    DpsMax: includeDps ? maxPull.Dps : null);
            })
            .Where(r => r != null)
            .Select(r => r!)
            .OrderByDescending(r => r.Avg)
            .ToList();
    }

    // Cumulative distinct pushes tonight vs the best-ever single session. ICD grouping
    // is applied per encounter because EI event times are encounter-relative.
    private async Task<List<HtcmSummaryOrbRow>> BuildOrbPushesAsync(
        DateTime sessionDate,
        List<Guid> allEncounterIds,
        List<string> includedList,
        Dictionary<Guid, DateTime> sessionDateByEncounter,
        CancellationToken ct)
    {
        var events = await _db.MechanicEvents
            .InnerJoin(_db.Players, (m, p) => m.PlayerId == p.Id, (m, p) => new { m, p })
            .Where(x => allEncounterIds.Contains(x.m.EncounterId)
                     && x.m.MechanicName == OrbPushMechanic
                     && includedList.Contains(x.p.AccountName))
            .Select(x => new { x.m.EncounterId, x.p.AccountName, x.m.EventTimeMs })
            .ToListAsync(ct);

        var icd = MechanicIcdHelper.GetIcd(OrbPushMechanic);

        var perEncounter = events
            .GroupBy(e => (e.EncounterId, e.AccountName))
            .Select(g => new
            {
                g.Key.AccountName,
                Date = sessionDateByEncounter.TryGetValue(g.Key.EncounterId, out var d) ? d : default,
                Count = MechanicIcdHelper.CountWithIcd(g.Select(e => e.EventTimeMs).ToList(), icd)
            })
            .ToList();

        return perEncounter
            .GroupBy(x => x.AccountName)
            .Select(g =>
            {
                var bySession = g.GroupBy(x => x.Date).ToDictionary(s => s.Key, s => s.Sum(x => x.Count));
                if (!bySession.TryGetValue(sessionDate, out var tonight)) return null;
                var best = bySession.Values.Max();
                return new HtcmSummaryOrbRow(g.Key, tonight, best, IsNewBest: tonight >= best);
            })
            .Where(r => r != null)
            .Select(r => r!)
            .OrderByDescending(r => r.SessionTotal)
            .ToList();
    }

    // Boon strips are recorded per pull on the full-fight basis, so Max is the
    // best-ever single pull — same basis as Avg and Top.
    private async Task<List<HtcmSummaryStatRow>> BuildBoonRipsAsync(
        DateTime sessionDate,
        List<Guid> allEncounterIds,
        List<string> includedList,
        Dictionary<Guid, DateTime> sessionDateByEncounter,
        CancellationToken ct)
    {
        var rows = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id, (pe, p) => new { pe, p })
            .Where(x => allEncounterIds.Contains(x.pe.EncounterId)
                     && includedList.Contains(x.p.AccountName))
            .Select(x => new { x.pe.EncounterId, x.p.AccountName, x.pe.BoonStrips })
            .ToListAsync(ct);

        var perPull = rows
            .Select(r => new PullValue(r.EncounterId, r.AccountName, r.BoonStrips, r.BoonStrips))
            .ToList();

        return BuildStatRows(perPull, sessionDate, sessionDateByEncounter, p => p.Damage);
    }

    // Debilitated-in-Giants counts the pulls where the player carried the debuff into the
    // Giants window at all — pass/fail per pull, not a weighted uptime. Read straight off
    // the slice the prog page renders, so the count and the page's percentage are always
    // derived from the same pulls.
    private async Task<HtcmSummaryShame> BuildShameAsync(
        HtcmSessionDetail detail,
        HashSet<string> included,
        CancellationToken ct)
    {
        var sessionEncounterIds = detail.Pulls.Select(p => p.EncounterId).ToList();

        var worstDebil = (detail.PlayerPhaseStats ?? new List<HtcmPlayerPhaseSessionStat>())
            .Where(s => included.Contains(s.AccountName))
            .Where(s => s.Giants.DebilPulls > 0)
            .Select(s => new { Account = s.AccountName, Pulls = s.Giants.DebilPulls })
            .OrderByDescending(x => x.Pulls)
            .FirstOrDefault();
        var chomps = await _db.MechanicEvents
            .InnerJoin(_db.Players, (m, p) => m.PlayerId == p.Id, (m, p) => new { m, p })
            .Where(x => sessionEncounterIds.Contains(x.m.EncounterId)
                     && x.m.MechanicName == PrimordusJawsMechanic)
            .Select(x => x.p.AccountName)
            .ToListAsync(ct);

        var worstChomp = chomps
            .Where(included.Contains)
            .GroupBy(a => a)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        return new HtcmSummaryShame(
            DebilitatedPlayer: worstDebil?.Account,
            DebilitatedPulls: worstDebil?.Pulls ?? 0,
            ChompedPlayer: worstChomp?.Key,
            ChompCount: worstChomp?.Count() ?? 0);
    }

    // Weighted sum of each player's share of the session leader in four categories.
    // A category with no data simply contributes nothing to anyone's score.
    private static HtcmSummaryMvdps? ComputeMvdps(
        List<HtcmSummaryBurstGroup> burstGroups,
        List<HtcmSummaryDragonRow> dragons,
        List<HtcmSummaryOrbRow> orbPushes,
        List<HtcmSummaryStatRow> boonRips)
    {
        // Burst = combined session-average total damage across all three burst groups.
        var burst = burstGroups
            .SelectMany(g => g.Players)
            .GroupBy(p => p.AccountName)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Avg));
        var dps = dragons.ToDictionary(d => d.AccountName, d => d.DpsAvg);
        var orbs = orbPushes.ToDictionary(o => o.AccountName, o => (double)o.SessionTotal);
        var rips = boonRips.ToDictionary(r => r.AccountName, r => r.Avg);

        var accounts = burst.Keys
            .Concat(dps.Keys).Concat(orbs.Keys).Concat(rips.Keys)
            .Distinct()
            .ToList();
        if (accounts.Count == 0) return null;

        static double Share(Dictionary<string, double> values, string account, double weight)
        {
            if (values.Count == 0) return 0;
            var leader = values.Values.Max();
            if (leader <= 0) return 0;
            return values.TryGetValue(account, out var v) ? v / leader * weight : 0;
        }

        var scored = accounts
            .Select(a => new HtcmSummaryMvdps(
                a,
                BurstPoints: Share(burst, a, BurstWeight),
                DpsPoints: Share(dps, a, DpsWeight),
                OrbPoints: Share(orbs, a, OrbWeight),
                RipPoints: Share(rips, a, RipWeight),
                RunnerUp: null,
                RunnerUpScore: null))
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.BurstPoints)
            .ToList();

        var winner = scored[0];
        var runnerUp = scored.Count > 1 ? scored[1] : null;
        return winner with { RunnerUp = runnerUp?.AccountName, RunnerUpScore = runnerUp?.Score };
    }

    private record PhaseDamageRow(Guid EncounterId, string AccountName, string PhaseName, long Damage);

    private record PullValue(Guid EncounterId, string AccountName, double Damage, double Dps);
}

// DTOs

public record HtcmSessionSummary(
    DateTime Date,
    int PullCount,
    string BestPhase,
    decimal BestBossHpRemaining,
    List<HtcmSummaryBurstGroup> BurstGroups,
    List<HtcmSummaryDragonRow> Dragons,
    List<HtcmSummaryOrbRow> OrbPushes,
    List<HtcmSummaryStatRow> BoonRips,
    HtcmSummaryMvdps? Mvdps,
    HtcmSummaryShame Shame);

/// <summary>
/// SquadDps is tonight's average across the group's phases; SquadDpsAllTime is the same
/// figure across every session, for comparison. Both cover the whole squad, pugs included.
/// </summary>
public record HtcmSummaryBurstGroup(
    string Name,
    int PullsReached,
    int AverageDurationMs,
    int SquadDps,
    int SquadDpsAllTime,
    List<HtcmSummaryStatRow> Players);

/// <summary>
/// Avg/Top are tonight; Max is best-ever. IsNewBest means tonight set the max. The Dps*
/// figures accompany each damage column where the metric is damage-based, otherwise null.
/// </summary>
public record HtcmSummaryStatRow(
    string AccountName, double Avg, double Top, double Max, bool IsNewBest,
    double? DpsAvg = null, double? DpsTop = null, double? DpsMax = null);

public record HtcmSummaryDragonRow(
    string AccountName,
    double DamageAvg, double DamageTop, double DamageMax,
    double DpsAvg, double DpsTop, double DpsMax,
    bool IsNewBest);

public record HtcmSummaryOrbRow(string AccountName, int SessionTotal, int BestSessionTotal, bool IsNewBest);

public record HtcmSummaryMvdps(
    string AccountName,
    double BurstPoints,
    double DpsPoints,
    double OrbPoints,
    double RipPoints,
    string? RunnerUp,
    double? RunnerUpScore)
{
    public double Score => BurstPoints + DpsPoints + OrbPoints + RipPoints;
}

public record HtcmSummaryShame(
    string? DebilitatedPlayer,
    int DebilitatedPulls,
    string? ChompedPlayer,
    int ChompCount);
