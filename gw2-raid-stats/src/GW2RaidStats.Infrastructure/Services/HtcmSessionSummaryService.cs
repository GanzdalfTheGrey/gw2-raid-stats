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

    // Penalties are absolute points off the weighted score, not shares — a first death
    // costs the same 2 points whoever you are. Debilitated is charged per stack carried
    // into Giants, summed across the pulls it happened on.
    private const double PenaltyPerFirstDeath = 2;
    private const double PenaltyPerDebilStack = 1;
    private const double PenaltyPerChomp = 1;

    // How many players the MVDPS podium lists.
    private const int MvdpsPodiumSize = 3;

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

        // Shared between the shame awards and the MVDPS penalties, so a player can't be
        // named for a mistake the scoring didn't charge them for.
        var playerSlices = (detail.PlayerPhaseStats ?? new List<HtcmPlayerPhaseSessionStat>())
            .Where(s => included.Contains(s.AccountName))
            .ToList();

        var firstDeaths = detail.Pulls
            .Where(p => p.FirstDeathPlayer != null && included.Contains(p.FirstDeathPlayer))
            .GroupBy(p => p.FirstDeathPlayer!)
            .ToDictionary(g => g.Key, g => g.Count());

        var chomps = await GetChompCountsAsync(detail, included, ct);

        // Stacks carried into Giants across the whole session: average stacks on the pulls
        // it happened, times the number of those pulls.
        var debilStacks = playerSlices.ToDictionary(
            s => s.AccountName,
            s => (double)(s.Giants.DebilAvgStacks ?? 0m) * s.Giants.DebilPulls);

        var shame = BuildShame(playerSlices, firstDeaths, chomps);
        var mvdps = ComputeMvdps(burstGroups, dragons, orbPushes, boonRips, firstDeaths, debilStacks, chomps);

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
    private static HtcmSummaryShame BuildShame(
        List<HtcmPlayerPhaseSessionStat> playerSlices,
        Dictionary<string, int> firstDeaths,
        Dictionary<string, int> chomps)
    {
        var worstDebil = playerSlices
            .Where(s => s.Giants.DebilPulls > 0)
            .Select(s => new { Account = s.AccountName, Pulls = s.Giants.DebilPulls })
            .OrderByDescending(x => x.Pulls)
            .FirstOrDefault();

        var worstFirstDeath = Worst(firstDeaths);
        var worstChomp = Worst(chomps);

        return new HtcmSummaryShame(
            FirstDeathPlayer: worstFirstDeath?.Key,
            FirstDeathCount: worstFirstDeath?.Value ?? 0,
            DebilitatedPlayer: worstDebil?.Account,
            DebilitatedPulls: worstDebil?.Pulls ?? 0,
            ChompedPlayer: worstChomp?.Key,
            ChompCount: worstChomp?.Value ?? 0);
    }

    private static KeyValuePair<string, int>? Worst(Dictionary<string, int> counts)
    {
        if (counts.Count == 0) return null;
        var worst = counts.OrderByDescending(kv => kv.Value).First();
        return worst.Value > 0 ? worst : null;
    }

    private async Task<Dictionary<string, int>> GetChompCountsAsync(
        HtcmSessionDetail detail, HashSet<string> included, CancellationToken ct)
    {
        var sessionEncounterIds = detail.Pulls.Select(p => p.EncounterId).ToList();

        var chomps = await _db.MechanicEvents
            .InnerJoin(_db.Players, (m, p) => m.PlayerId == p.Id, (m, p) => new { m, p })
            .Where(x => sessionEncounterIds.Contains(x.m.EncounterId)
                     && x.m.MechanicName == PrimordusJawsMechanic)
            .Select(x => x.p.AccountName)
            .ToListAsync(ct);

        return chomps
            .Where(included.Contains)
            .GroupBy(a => a)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    // Weighted sum of each player's share of the session leader in four categories, minus
    // flat penalties for the night's mistakes. A category with no data simply contributes
    // nothing to anyone's score. Returns the podium, best first.
    private static List<HtcmSummaryMvdps> ComputeMvdps(
        List<HtcmSummaryBurstGroup> burstGroups,
        List<HtcmSummaryDragonRow> dragons,
        List<HtcmSummaryOrbRow> orbPushes,
        List<HtcmSummaryStatRow> boonRips,
        Dictionary<string, int> firstDeaths,
        Dictionary<string, double> debilStacks,
        Dictionary<string, int> chomps)
    {
        // Burst = combined session-average total damage across all three burst groups.
        var burst = burstGroups
            .SelectMany(g => g.Players)
            .GroupBy(p => p.AccountName)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Avg));
        var dps = dragons.ToDictionary(d => d.AccountName, d => d.DpsAvg);
        var orbs = orbPushes.ToDictionary(o => o.AccountName, o => (double)o.SessionTotal);
        var rips = boonRips.ToDictionary(r => r.AccountName, r => r.Avg);

        // Penalised players are included even with no scoring data, so a night spent
        // mostly dead still shows up rather than quietly vanishing from the podium.
        var accounts = burst.Keys
            .Concat(dps.Keys).Concat(orbs.Keys).Concat(rips.Keys)
            .Concat(firstDeaths.Keys).Concat(debilStacks.Keys).Concat(chomps.Keys)
            .Distinct()
            .ToList();
        if (accounts.Count == 0) return new List<HtcmSummaryMvdps>();

        static double Share(Dictionary<string, double> values, string account, double weight)
        {
            if (values.Count == 0) return 0;
            var leader = values.Values.Max();
            if (leader <= 0) return 0;
            return values.TryGetValue(account, out var v) ? v / leader * weight : 0;
        }

        return accounts
            .Select(a =>
            {
                var deaths = firstDeaths.GetValueOrDefault(a);
                var stacks = debilStacks.GetValueOrDefault(a);
                var chomped = chomps.GetValueOrDefault(a);
                return new HtcmSummaryMvdps(
                    a,
                    BurstPoints: Share(burst, a, BurstWeight),
                    DpsPoints: Share(dps, a, DpsWeight),
                    OrbPoints: Share(orbs, a, OrbWeight),
                    RipPoints: Share(rips, a, RipWeight),
                    FirstDeaths: deaths,
                    DebilStacks: stacks,
                    Chomps: chomped,
                    FirstDeathPenalty: deaths * PenaltyPerFirstDeath,
                    DebilPenalty: stacks * PenaltyPerDebilStack,
                    ChompPenalty: chomped * PenaltyPerChomp);
            })
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.BurstPoints)
            .Take(MvdpsPodiumSize)
            .ToList();
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
    List<HtcmSummaryMvdps> Mvdps,
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

/// <summary>
/// One podium entry. The *Points are shares of the session leader (100 across all four);
/// the *Penalty values are flat deductions, with the raw counts alongside them so the
/// embed can show what earned the deduction.
/// </summary>
public record HtcmSummaryMvdps(
    string AccountName,
    double BurstPoints,
    double DpsPoints,
    double OrbPoints,
    double RipPoints,
    int FirstDeaths,
    double DebilStacks,
    int Chomps,
    double FirstDeathPenalty,
    double DebilPenalty,
    double ChompPenalty)
{
    public double EarnedPoints => BurstPoints + DpsPoints + OrbPoints + RipPoints;

    public double Penalty => FirstDeathPenalty + DebilPenalty + ChompPenalty;

    public double Score => EarnedPoints - Penalty;
}

public record HtcmSummaryShame(
    string? FirstDeathPlayer,
    int FirstDeathCount,
    string? DebilitatedPlayer,
    int DebilitatedPulls,
    string? ChompedPlayer,
    int ChompCount);
