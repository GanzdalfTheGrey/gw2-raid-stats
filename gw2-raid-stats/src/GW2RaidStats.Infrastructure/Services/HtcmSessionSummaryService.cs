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
    // via MechanicIcdHelper; "Jaws.H" (Primordus Jaws) is a discrete hit. "ShckWv.H"
    // (Mordremoth Shockwave) double-hits and is ICD-grouped to one count per wave.
    private const string OrbPushMechanic = "Orb Push";
    private const string PrimordusJawsMechanic = "Jaws.H";
    private const string ShockwaveMechanic = "ShckWv.H";

    // Red bait — a red target lands on a player. Reds are meant for healers to take, so a
    // non-healer catching one is the mistake (see BuildBadRedsAsync). Counted as raw
    // events (no ICD): each bait is a distinct assignment.
    private const string RedBaitMechanic = "Red.B";

    // Squad average-DPS targets per burst group, shown next to tonight's figure in the
    // header. Dragons has no target (it shows all-time average instead).
    private const int TimecasterSquadTarget = 175_000;
    private const int GiantsSquadTarget = 310_000;
    private const int SaltspraySquadTarget = 170_000;

    // Per-player Giants DPS targets. Profession (elite spec) wins over role, so a Virtuoso
    // running portals or a Vindicator is judged on its own bar rather than the pure-DPS
    // one. Healers (heal_quick/heal_alac) have no Giants DPS target — they're not shamed
    // for low burst — so GiantsTargetFor returns null for them.
    private const int GiantsTargetVirtuoso = 35_000;
    private const int GiantsTargetVindicator = 45_000;
    private const int GiantsTargetBoonDps = 30_000;
    private const int GiantsTargetPureDps = 55_000;

    // A player's Giants performance this far above their target earns a cookie, this far
    // below earns a shame — applied both per pull and to the session average.
    private const int BurstCookieShameBand = 10_000;

    private static int? GiantsTargetFor(string? profession, string? role) => profession switch
    {
        "Virtuoso" => GiantsTargetVirtuoso,
        "Vindicator" => GiantsTargetVindicator,
        _ => role switch
        {
            "heal_quick" or "heal_alac" => null,
            "dps_quick" or "dps_alac" => GiantsTargetBoonDps,
            _ => GiantsTargetPureDps
        }
    };

    // Boon points rank the "🎵 Boons" highlight. They apply only to players whose
    // PlayerEncounter.Role marks them a quickness or alacrity giver, scored on the uptime
    // their *subgroup received*, not their own — a scrapper self-quickening while the group
    // sits at 40% has not done the job. 5 points per burst group (Timecaster, Giants,
    // Saltspray) and 15 across the dragons, so 30 in total. Uptime at or above the
    // full-credit threshold earns the whole allocation; below it the award ramps linearly.
    private const double BoonPointsPerBurstGroup = 5;
    private const double BoonPointsDragons = 15;
    private const decimal BoonFullCreditUptimePct = 95m;

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

        var sessionIds = detail.Pulls.Select(p => p.EncounterId).ToHashSet();

        var burstGroups = new List<HtcmSummaryBurstGroup>
        {
            BuildBurstGroup("Timecaster", HtcmProgService.TimecasterPhases, TimecasterSquadTarget, sessionDate,
                phaseRows, guildPhaseRows, durationByKey, sessionDateByEncounter, detail.SessionBurstAverages?.Timecaster),
            BuildBurstGroup("Giants", HtcmProgService.GiantsPhases, GiantsSquadTarget, sessionDate,
                phaseRows, guildPhaseRows, durationByKey, sessionDateByEncounter, detail.SessionBurstAverages?.Giants),
            BuildBurstGroup("Saltspray", HtcmProgService.SaltsprayPhases, SaltspraySquadTarget, sessionDate,
                phaseRows, guildPhaseRows, durationByKey, sessionDateByEncounter, detail.SessionBurstAverages?.Saltspray),
        };

        var dragons = BuildDragonRows(sessionDate, guildPhaseRows, durationByKey, sessionDateByEncounter);
        var orbPushes = await BuildOrbPushesAsync(sessionDate, allEncounterIds, includedList, sessionDateByEncounter, ct);
        var boonRips = await BuildBoonRipsAsync(sessionDate, allEncounterIds, includedList, sessionDateByEncounter, ct);

        var playerSlices = (detail.PlayerPhaseStats ?? new List<HtcmPlayerPhaseSessionStat>())
            .Where(s => included.Contains(s.AccountName))
            .ToList();

        var firstDeaths = detail.Pulls
            .Where(p => p.FirstDeathPlayer != null && included.Contains(p.FirstDeathPlayer))
            .GroupBy(p => p.FirstDeathPlayer!)
            .ToDictionary(g => g.Key, g => g.Count());

        var chomps = await GetMechanicCountsAsync(detail, included, PrimordusJawsMechanic, ct);
        var shockwaves = await GetMechanicCountsAsync(detail, included, ShockwaveMechanic, ct);

        // Boon points are a session award, so unlike the damage tables this only looks at
        // tonight's pulls — there is no "best ever" comparison to make.
        var boonUptime = await BuildBoonPointsAsync(
            detail.Pulls.Select(p => p.EncounterId).ToList(), phaseNames, included, ct);

        // Participation drives the Field Medic / CC highlights, the Giants targets, and the
        // bad-reds gate — all in one pass over the pulls.
        var participation = await LoadParticipationAsync(sessionIds.ToList(), included, ct);

        var reds = await BuildBadRedsAsync(detail, included, participation.NonHealer, ct);

        // Enrich the Giants group with per-player targets + per-pull cookie/spec/shame rows.
        // Include zero-damage pulls so a dead-whole-phase pull counts (as a shame) and the
        // three counts sum to the pulls the player actually played.
        var giantsPerPull = AggregatePerPull(
            HtcmProgService.GiantsPhases, guildPhaseRows, durationByKey, includeZeroDamage: true);
        var (giantsPlayers, giantsTargets) =
            BuildGiantsTargets(burstGroups[1].Players, giantsPerPull, participation, sessionIds);
        burstGroups[1] = burstGroups[1] with { Players = giantsPlayers, Targets = giantsTargets };

        // Rows are sorted by margin desc, so the first cookie is the biggest and the last
        // shame is the worst — the header standouts.
        static HtcmBurstAward Award(HtcmBurstTargetRow r) => new(r.AccountName, r.AvgDps, r.TargetDps);
        var giantsCookie = giantsTargets
            .Where(r => r.Status == HtcmBurstStatus.Cookie).Select(Award).FirstOrDefault();
        var giantsMiss = giantsTargets
            .Where(r => r.Status == HtcmBurstStatus.Shame).Select(Award).LastOrDefault();

        var shame = BuildShame(playerSlices, firstDeaths, chomps, shockwaves, reds, giantsMiss);
        var highlights = BuildHighlights(
            burstGroups, dragons, boonUptime, orbPushes, boonRips,
            participation.Resurrects, participation.Breakbar, giantsCookie);

        // Combined-dragon squad DPS for the header's squad-totals line: tonight (restricted
        // to this session's pulls) and all-time, same basis as the burst groups.
        var dragonSquadDps = ComputeSquadDps(HtcmProgService.CombinedDragonPhases, phaseRows, durationByKey, sessionIds);
        var dragonSquadDpsAllTime = ComputeSquadDps(HtcmProgService.CombinedDragonPhases, phaseRows, durationByKey, null);

        return new HtcmSessionSummary(
            Date: sessionDate,
            PullCount: detail.PullCount,
            BestPhase: detail.BestPhase,
            BestBossHpRemaining: detail.BestBossHpRemaining,
            DragonSquadDps: dragonSquadDps,
            DragonSquadDpsAllTime: dragonSquadDpsAllTime,
            BurstGroups: burstGroups,
            Dragons: dragons,
            OrbPushes: orbPushes,
            BoonRips: boonRips,
            Highlights: highlights,
            Shame: shame);
    }

    // Session avg/top of per-pull total damage, plus the all-time per-pull best.
    private static HtcmSummaryBurstGroup BuildBurstGroup(
        string name,
        string[] groupPhases,
        int squadTarget,
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
            SquadTarget: squadTarget,
            Players: players,
            Targets: new List<HtcmBurstTargetRow>());
    }

    // Attaches each Giants player's DPS target and builds a per-player target row: their
    // session-average status (cookie / in-spec / shame vs the band) plus how many of
    // tonight's pulls landed in each of those buckets. Sorted by margin, biggest first, so
    // the header can take the top cookie and worst shame and the Cookies & Shames section
    // can list everyone. Healers (no target) are omitted.
    private static (List<HtcmSummaryStatRow> Players, List<HtcmBurstTargetRow> Targets) BuildGiantsTargets(
        List<HtcmSummaryStatRow> giantsPlayers,
        List<PullValue> perPull,
        SessionParticipation participation,
        HashSet<Guid> sessionIds)
    {
        var pullDpsByAccount = perPull
            .Where(p => sessionIds.Contains(p.EncounterId))
            .GroupBy(p => p.AccountName)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Dps).ToList());

        var enriched = new List<HtcmSummaryStatRow>();
        var targets = new List<HtcmBurstTargetRow>();

        foreach (var pl in giantsPlayers)
        {
            var target = GiantsTargetFor(
                participation.Profession.GetValueOrDefault(pl.AccountName),
                participation.Role.GetValueOrDefault(pl.AccountName));

            if (target is not { } t || pl.DpsAvg is not { } avgDps)
            {
                enriched.Add(pl);
                continue;
            }

            enriched.Add(pl with { TargetDps = t });

            var pulls = pullDpsByAccount.GetValueOrDefault(pl.AccountName) ?? new List<double>();
            var cookiePulls = pulls.Count(d => d >= t + BurstCookieShameBand);
            var shamePulls = pulls.Count(d => d <= t - BurstCookieShameBand);

            var margin = avgDps - t;
            var status = margin >= BurstCookieShameBand ? HtcmBurstStatus.Cookie
                : margin <= -BurstCookieShameBand ? HtcmBurstStatus.Shame
                : HtcmBurstStatus.InSpec;

            targets.Add(new HtcmBurstTargetRow(
                pl.AccountName, (int)avgDps, t, status,
                cookiePulls, pulls.Count - cookiePulls - shamePulls, shamePulls));
        }

        targets = targets.OrderByDescending(r => r.AvgDps - r.TargetDps).ToList();
        return (enriched, targets);
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
    //
    // By default zero-damage pulls (the player was dead the whole phase group) are dropped,
    // so they don't drag a DPS average down to zero. Pass includeZeroDamage: true when
    // counting pulls — a dead-whole-phase pull is a real pull (and a shame at 0 dps), and
    // dropping it makes per-player cookie/spec/shame counts fall short of the pulls played.
    private static List<PullValue> AggregatePerPull(
        string[] groupPhases,
        List<PhaseDamageRow> phaseRows,
        Dictionary<(Guid, string), int> durationByKey,
        bool includeZeroDamage = false)
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
            .Where(p => includeZeroDamage || p.Damage > 0)
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

    // Scores each boon giver on the uptime their subgroup *received* in the phases that
    // matter, pull by pull, then averages across the pulls they were in — so a giver who
    // sat out half the night isn't diluted by the pulls they missed.
    //
    // Only the giver's own boon counts: role dps_quick/heal_quick is judged on Quickness,
    // dps_alac/heal_alac on Alacrity. Role is read per encounter rather than per session,
    // so a mid-session build swap is scored on what they actually played.
    private async Task<List<HtcmSummaryBoonRow>> BuildBoonPointsAsync(
        List<Guid> sessionEncounterIds,
        List<string> phaseNames,
        HashSet<string> included,
        CancellationToken ct)
    {
        var phaseRows = await _db.PlayerEncounterPhaseStats
            .InnerJoin(_db.Players, (ps, p) => ps.PlayerId == p.Id, (ps, p) => new { ps, p })
            .Where(x => sessionEncounterIds.Contains(x.ps.EncounterId)
                     && phaseNames.Contains(x.ps.PhaseName))
            .Select(x => new BoonPhaseRow(
                x.ps.EncounterId, x.ps.PlayerId, x.p.AccountName, x.ps.PhaseName,
                x.ps.QuicknessUptimePct, x.ps.AlacrityUptimePct))
            .ToListAsync(ct);
        if (phaseRows.Count == 0) return new List<HtcmSummaryBoonRow>();

        // Subgroup and role are per (encounter, player); both are needed for every player
        // in the pull, not just guild members, because a subgroup average includes pugs.
        var roleRows = await _db.PlayerEncounters
            .Where(pe => sessionEncounterIds.Contains(pe.EncounterId))
            .Select(pe => new { pe.EncounterId, pe.PlayerId, pe.SquadGroup, pe.Role })
            .ToListAsync(ct);
        var rolesByKey = roleRows
            .GroupBy(r => (r.EncounterId, r.PlayerId))
            .ToDictionary(g => g.Key, g => (g.First().SquadGroup, g.First().Role));

        var groups = new[]
        {
            (Phases: HtcmProgService.TimecasterPhases, Allocation: BoonPointsPerBurstGroup),
            (Phases: HtcmProgService.GiantsPhases, Allocation: BoonPointsPerBurstGroup),
            (Phases: HtcmProgService.SaltsprayPhases, Allocation: BoonPointsPerBurstGroup),
            (Phases: HtcmProgService.CombinedDragonPhases, Allocation: BoonPointsDragons),
        };

        // Keyed per phase group so each group's allocation is averaged over the pulls the
        // giver was in before the four are summed — otherwise a giver present for more
        // pulls would accumulate more than the 30-point ceiling.
        var awards = new Dictionary<(string Account, string Boon, int Group), List<(double Credit, double Uptime)>>();

        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var (phases, allocation) = groups[groupIndex];
            var groupRows = phaseRows.Where(r => HtcmProgService.MatchesAny(r.PhaseName, phases)).ToList();

            foreach (var pull in groupRows.GroupBy(r => r.EncounterId))
            {
                // Subgroup average of each boon across every player in it, over all the
                // group's phases. A player with no recorded value for a boon is left out
                // of that boon's average rather than counted as a zero.
                double? SubgroupUptime(int? squadGroup, string boon)
                {
                    var members = pull.Where(r =>
                    {
                        // No subgroup on file (pre-migration rows, odd squads): fall back
                        // to the whole squad rather than scoring the giver against itself.
                        if (squadGroup == null) return true;
                        return rolesByKey.TryGetValue((r.EncounterId, r.PlayerId), out var m)
                            && m.SquadGroup == squadGroup;
                    });

                    var values = members
                        .Select(r => boon == BoonQuickness ? r.Quickness : r.Alacrity)
                        .Where(v => v != null)
                        .Select(v => (double)v!.Value)
                        .ToList();

                    return values.Count > 0 ? values.Average() : null;
                }

                // One award per giver per pull. Groups spanning several phases (Giants,
                // the dragons) have a row per phase, so collapse to the player first.
                var givers = pull
                    .Where(r => included.Contains(r.AccountName))
                    .GroupBy(r => r.PlayerId)
                    .Select(g => g.First());

                foreach (var giver in givers)
                {
                    if (!rolesByKey.TryGetValue((giver.EncounterId, giver.PlayerId), out var meta)) continue;

                    var boon = BoonForRole(meta.Role);
                    if (boon == null) continue;

                    var uptime = SubgroupUptime(meta.SquadGroup, boon);
                    if (uptime == null) continue;

                    // At or above the threshold the giver has done the job; below it the
                    // award ramps linearly, so 47.5% uptime earns half the allocation.
                    var credit = allocation * Math.Min(1.0, uptime.Value / (double)BoonFullCreditUptimePct);

                    var key = (giver.AccountName, boon, groupIndex);
                    if (!awards.TryGetValue(key, out var list))
                    {
                        list = new List<(double, double)>();
                        awards[key] = list;
                    }
                    list.Add((credit, uptime.Value));
                }
            }
        }

        return awards
            .GroupBy(kv => (kv.Key.Account, kv.Key.Boon))
            .Select(g => new HtcmSummaryBoonRow(
                g.Key.Account,
                g.Key.Boon,
                // Average within each phase group, then sum the groups: at most
                // 5 + 5 + 5 + 15 = 30 however many pulls the giver played.
                Points: g.Sum(kv => kv.Value.Average(v => v.Credit)),
                AvgUptimePct: g.SelectMany(kv => kv.Value).Average(v => v.Uptime)))
            .OrderByDescending(r => r.Points)
            .ToList();
    }

    private const string BoonQuickness = "Quickness";
    private const string BoonAlacrity = "Alacrity";

    // PlayerEncounter.Role is assigned by LogImportService.CalculateRole at a 10%
    // generation threshold. pure_dps (and anything unrecognised) gives no boon, and so
    // scores nothing in this category.
    private static string? BoonForRole(string? role) => role switch
    {
        "dps_quick" or "heal_quick" => BoonQuickness,
        "dps_alac" or "heal_alac" => BoonAlacrity,
        _ => null
    };

    // Debilitated-in-Giants counts the pulls where the player carried the debuff into the
    // Giants window at all — pass/fail per pull, not a weighted uptime. Read straight off
    // the slice the prog page renders, so the count and the page's percentage are always
    // derived from the same pulls.
    private static HtcmSummaryShame BuildShame(
        List<HtcmPlayerPhaseSessionStat> playerSlices,
        Dictionary<string, int> firstDeaths,
        Dictionary<string, int> chomps,
        Dictionary<string, int> shockwaves,
        Dictionary<string, int> reds,
        HtcmBurstAward? giantsMiss)
    {
        // Per-category rankings (non-zero, highest first). The header shows the top entry
        // — or "Multiple" when several tie for it — and the expanded view shows them all.
        // Debilitated ranks off the same per-player slice the prog page renders.
        var debilRanking = playerSlices
            .Where(s => s.Giants.DebilPulls > 0)
            .Select(s => new HtcmShameRank(s.AccountName, s.Giants.DebilPulls))
            .OrderByDescending(r => r.Count)
            .ToList();

        return new HtcmSummaryShame(
            GiantsMiss: giantsMiss,
            FirstDeathRanking: Ranking(firstDeaths),
            DebilRanking: debilRanking,
            ChompRanking: Ranking(chomps),
            ShockwaveRanking: Ranking(shockwaves),
            RedsRanking: Ranking(reds));
    }

    // Accounts with a non-zero count, highest first, for the expanded shame view.
    private static List<HtcmShameRank> Ranking(Dictionary<string, int> counts) => counts
        .Where(kv => kv.Value > 0)
        .OrderByDescending(kv => kv.Value)
        .Select(kv => new HtcmShameRank(kv.Key, kv.Value))
        .ToList();

    // The account with the highest count, or null when the dictionary is empty or every
    // count is zero. Used for both shame ("most X") and the Field Medic highlight.
    private static KeyValuePair<string, int>? Highest(Dictionary<string, int> counts)
    {
        if (counts.Count == 0) return null;
        var top = counts.OrderByDescending(kv => kv.Value).First();
        return top.Value > 0 ? top : null;
    }

    // Good-play and mistake callouts for the at-a-glance Highlights board. Each is the
    // single leader in its category; nulls (no data) are dropped by the renderer.
    private static HtcmSummaryHighlights BuildHighlights(
        List<HtcmSummaryBurstGroup> burstGroups,
        List<HtcmSummaryDragonRow> dragons,
        List<HtcmSummaryBoonRow> boonUptime,
        List<HtcmSummaryOrbRow> orbPushes,
        List<HtcmSummaryStatRow> boonRips,
        Dictionary<string, int> resurrects,
        Dictionary<string, double> breakbar,
        HtcmBurstAward? giantsCookie)
    {
        var burstKing = burstGroups
            .SelectMany(g => g.Players)
            .GroupBy(p => p.AccountName)
            .Select(g => new HighlightEntry(g.Key, g.Sum(p => p.Avg)))
            .OrderByDescending(e => e.Value)
            .FirstOrDefault(e => e.Value > 0);

        var dragonDps = dragons
            .OrderByDescending(d => d.DpsAvg)
            .Select(d => new HighlightEntry(d.AccountName, d.DpsAvg))
            .FirstOrDefault(e => e.Value > 0);

        var boonRock = boonUptime
            .OrderByDescending(b => b.Points)
            .Select(b => new HighlightEntry(b.AccountName, b.AvgUptimePct, b.Boon))
            .FirstOrDefault();

        var orbMaster = orbPushes
            .OrderByDescending(o => o.SessionTotal)
            .Select(o => new HighlightEntry(o.AccountName, o.SessionTotal))
            .FirstOrDefault(e => e.Value > 0);

        var medic = Highest(resurrects) is { } m
            ? new HighlightEntry(m.Key, m.Value)
            : null;

        var topRips = boonRips
            .OrderByDescending(r => r.Avg)
            .Select(r => new HighlightEntry(r.AccountName, r.Avg))
            .FirstOrDefault(e => e.Value > 0);

        var mostCc = breakbar
            .OrderByDescending(kv => kv.Value)
            .Where(kv => kv.Value > 0)
            .Select(kv => new HighlightEntry(kv.Key, kv.Value))
            .FirstOrDefault();

        return new HtcmSummaryHighlights(
            burstKing, dragonDps, boonRock, orbMaster, medic, topRips, mostCc, giantsCookie);
    }

    // Red baits caught by non-healers. Role is read per encounter (via NonHealer), so a
    // player who healed some pulls and DPS'd others is only charged for the DPS pulls.
    private async Task<Dictionary<string, int>> BuildBadRedsAsync(
        HtcmSessionDetail detail,
        HashSet<string> included,
        HashSet<(Guid EncounterId, string Account)> nonHealer,
        CancellationToken ct)
    {
        var sessionEncounterIds = detail.Pulls.Select(p => p.EncounterId).ToList();

        var events = await _db.MechanicEvents
            .InnerJoin(_db.Players, (m, p) => m.PlayerId == p.Id, (m, p) => new { m, p })
            .Where(x => sessionEncounterIds.Contains(x.m.EncounterId)
                     && x.m.MechanicName == RedBaitMechanic)
            .Select(x => new { x.p.AccountName, x.m.EncounterId, x.m.EventTimeMs })
            .ToListAsync(ct);

        var icd = MechanicIcdHelper.GetIcd(RedBaitMechanic);
        return events
            .Where(e => included.Contains(e.AccountName)
                     && nonHealer.Contains((e.EncounterId, e.AccountName)))
            .GroupBy(e => e.AccountName)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(e => e.EncounterId)
                      .Sum(pull => MechanicIcdHelper.CountWithIcd(
                          pull.Select(e => e.EventTimeMs).ToList(), icd)));
    }

    // Per-player occurrence counts for one mechanic across the session's pulls,
    // ICD-grouped per pull so multi-hit mechanics count as single occurrences.
    private async Task<Dictionary<string, int>> GetMechanicCountsAsync(
        HtcmSessionDetail detail, HashSet<string> included, string mechanicName, CancellationToken ct)
    {
        var sessionEncounterIds = detail.Pulls.Select(p => p.EncounterId).ToList();

        var events = await _db.MechanicEvents
            .InnerJoin(_db.Players, (m, p) => m.PlayerId == p.Id, (m, p) => new { m, p })
            .Where(x => sessionEncounterIds.Contains(x.m.EncounterId)
                     && x.m.MechanicName == mechanicName)
            .Select(x => new { x.p.AccountName, x.m.EncounterId, x.m.EventTimeMs })
            .ToListAsync(ct);

        var icd = MechanicIcdHelper.GetIcd(mechanicName);
        return events
            .Where(e => included.Contains(e.AccountName))
            .GroupBy(e => e.AccountName)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(e => e.EncounterId)
                      .Sum(pull => MechanicIcdHelper.CountWithIcd(
                          pull.Select(e => e.EventTimeMs).ToList(), icd)));
    }


    // One pass over the session's PlayerEncounter rows: per guild member, total resurrects
    // and CC (breakbar) for the highlights, plus their dominant profession and role for the
    // Giants target. NonHealer is per (encounter, account) — read per pull so a build swap
    // is respected — and covers everyone, not just guild members, so the bad-reds gate
    // matches whoever was there.
    private async Task<SessionParticipation> LoadParticipationAsync(
        List<Guid> sessionEncounterIds, HashSet<string> included, CancellationToken ct)
    {
        var rows = await _db.PlayerEncounters
            .InnerJoin(_db.Players, (pe, p) => pe.PlayerId == p.Id,
                (pe, p) => new { p.AccountName, pe.EncounterId, pe.Resurrects, pe.Role, pe.Profession, pe.BreakbarDamage })
            .Where(x => sessionEncounterIds.Contains(x.EncounterId))
            .ToListAsync(ct);

        var guild = rows.Where(r => included.Contains(r.AccountName)).ToList();

        var resurrects = guild
            .GroupBy(r => r.AccountName)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Resurrects));

        var breakbar = guild
            .GroupBy(r => r.AccountName)
            .ToDictionary(g => g.Key, g => g.Sum(r => (double)(r.BreakbarDamage ?? 0m)));

        // Most-played profession / role across the player's pulls — build swaps are rare,
        // and a single dominant build is what the Giants target should judge.
        static T Dominant<T>(IEnumerable<T> values) =>
            values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;

        var profession = guild
            .GroupBy(r => r.AccountName)
            .ToDictionary(g => g.Key, g => Dominant(g.Select(r => r.Profession)));

        var role = guild
            .GroupBy(r => r.AccountName)
            .ToDictionary(g => g.Key, g => Dominant(g.Select(r => r.Role)));

        var nonHealer = rows
            .Where(r => !IsHealerRole(r.Role))
            .Select(r => (r.EncounterId, r.AccountName))
            .ToHashSet();

        return new SessionParticipation(resurrects, breakbar, profession, role, nonHealer);
    }

    private static bool IsHealerRole(string? role) => role is "heal_quick" or "heal_alac";

    private record SessionParticipation(
        Dictionary<string, int> Resurrects,
        Dictionary<string, double> Breakbar,
        Dictionary<string, string> Profession,
        Dictionary<string, string?> Role,
        HashSet<(Guid EncounterId, string Account)> NonHealer);

    private record PhaseDamageRow(Guid EncounterId, string AccountName, string PhaseName, long Damage);

    private record BoonPhaseRow(
        Guid EncounterId, Guid PlayerId, string AccountName, string PhaseName,
        decimal? Quickness, decimal? Alacrity);

    private record PullValue(Guid EncounterId, string AccountName, double Damage, double Dps);
}

// DTOs

public record HtcmSessionSummary(
    DateTime Date,
    int PullCount,
    string BestPhase,
    decimal BestBossHpRemaining,
    int DragonSquadDps,
    int DragonSquadDpsAllTime,
    List<HtcmSummaryBurstGroup> BurstGroups,
    List<HtcmSummaryDragonRow> Dragons,
    List<HtcmSummaryOrbRow> OrbPushes,
    List<HtcmSummaryStatRow> BoonRips,
    HtcmSummaryHighlights Highlights,
    HtcmSummaryShame Shame);

/// <summary>
/// At-a-glance good-play leaders for the Highlights board — one player per category, or
/// null when the category has no data. Value carries the raw number; the renderer formats
/// it per callout. Label holds extra context (the boon name for BoonRock).
/// </summary>
public record HtcmSummaryHighlights(
    HighlightEntry? BurstKing,
    HighlightEntry? DragonDps,
    HighlightEntry? BoonRock,
    HighlightEntry? OrbMaster,
    HighlightEntry? FieldMedic,
    HighlightEntry? BoonRips,
    HighlightEntry? MostCc,
    HtcmBurstAward? GiantsCookie);

public record HighlightEntry(string AccountName, double Value, string? Label = null);

/// <summary>
/// A player's Giants performance vs their target — the biggest over-target (cookie) or
/// under-target (miss) of the night. The renderer shows AvgDps and the AvgDps−TargetDps
/// margin.
/// </summary>
public record HtcmBurstAward(string AccountName, int AvgDps, int TargetDps);

/// <summary>
/// SquadDps is tonight's average across the group's phases; SquadDpsAllTime is the same
/// figure across every session, for comparison. Both cover the whole squad, pugs included.
/// </summary>
/// <summary>
/// Targets is the group's per-player target breakdown (session-average status + per-pull
/// counts), sorted by margin, biggest first — empty for groups without per-player targets
/// (currently all but Giants).
/// </summary>
public record HtcmSummaryBurstGroup(
    string Name,
    int PullsReached,
    int AverageDurationMs,
    int SquadDps,
    int SquadDpsAllTime,
    int SquadTarget,
    List<HtcmSummaryStatRow> Players,
    List<HtcmBurstTargetRow> Targets);

public enum HtcmBurstStatus { Cookie, InSpec, Shame }

/// <summary>
/// A Giants player's session-average status vs their DPS target, plus how many of tonight's
/// pulls beat it by the band (Cookie), landed within the band (Spec), or fell short (Shame).
/// </summary>
public record HtcmBurstTargetRow(
    string AccountName, int AvgDps, int TargetDps, HtcmBurstStatus Status,
    int CookiePulls, int SpecPulls, int ShamePulls);

/// <summary>
/// Avg/Top are tonight; Max is best-ever. IsNewBest means tonight set the max. The Dps*
/// figures accompany each damage column where the metric is damage-based, otherwise null.
/// TargetDps is populated only for the Giants group (the player's DPS target).
/// </summary>
public record HtcmSummaryStatRow(
    string AccountName, double Avg, double Top, double Max, bool IsNewBest,
    double? DpsAvg = null, double? DpsTop = null, double? DpsMax = null,
    int? TargetDps = null);

public record HtcmSummaryDragonRow(
    string AccountName,
    double DamageAvg, double DamageTop, double DamageMax,
    double DpsAvg, double DpsTop, double DpsMax,
    bool IsNewBest);

public record HtcmSummaryOrbRow(string AccountName, int SessionTotal, int BestSessionTotal, bool IsNewBest);

/// <summary>
/// Points a boon giver earned for the uptime their subgroup received. Boon is
/// "Quickness" or "Alacrity"; AvgUptimePct is across every scored phase, for display.
/// </summary>
public record HtcmSummaryBoonRow(string AccountName, string Boon, double Points, double AvgUptimePct);

/// <summary>
/// Shame awards. The scalar fields are the single worst per category, shown in the
/// collapsed header. The *Ranking lists are the full per-category rankings (non-zero,
/// highest first) shown in the expanded ephemeral breakdown.
/// </summary>
public record HtcmSummaryShame(
    HtcmBurstAward? GiantsMiss,
    List<HtcmShameRank> FirstDeathRanking,
    List<HtcmShameRank> DebilRanking,
    List<HtcmShameRank> ChompRanking,
    List<HtcmShameRank> ShockwaveRanking,
    List<HtcmShameRank> RedsRanking);

public record HtcmShameRank(string AccountName, int Count);
