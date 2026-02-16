using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Infrastructure.Services.Achievements.Progress;

/// <summary>
/// Calculates cumulative/career statistics for players.
/// Used by achievement checkers to evaluate milestone and aggregate achievements.
/// </summary>
public class PlayerHistoryCalculator
{
    private readonly RaidStatsDb _db;

    // Threshold for considering someone a boon support (generation % to squad)
    private const decimal BoonSupportThreshold = 10m;

    // Baseline date for certain achievements (Speed Demon, Keeping Up)
    public static readonly DateTimeOffset AchievementBaselineDate = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public PlayerHistoryCalculator(RaidStatsDb db)
    {
        _db = db;
    }

    #region Deathless Streaks

    /// <summary>
    /// Get current deathless kill streak and when the threshold was first reached
    /// </summary>
    public async Task<(int currentStreak, DateTimeOffset? thresholdReachedAt)> GetDeathlessStreakAsync(
        Guid playerId, int threshold, CancellationToken ct)
    {
        var kills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.pe.Deaths, x.e.EncounterTime })
            .ToListAsync(ct);

        int streak = 0;
        int maxStreak = 0;
        DateTimeOffset? thresholdReachedAt = null;

        foreach (var kill in kills)
        {
            if (kill.Deaths == 0)
            {
                streak++;
                if (streak > maxStreak) maxStreak = streak;
                if (streak == threshold && thresholdReachedAt == null)
                {
                    thresholdReachedAt = kill.EncounterTime;
                }
            }
            else
            {
                streak = 0;
            }
        }

        return (maxStreak, thresholdReachedAt);
    }

    /// <summary>
    /// Get current (most recent) deathless streak
    /// </summary>
    public async Task<int> GetCurrentDeathlessStreakAsync(Guid playerId, CancellationToken ct)
    {
        var encounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderByDescending(x => x.e.EncounterTime)
            .Select(x => x.pe.Deaths)
            .ToListAsync(ct);

        var streak = 0;
        foreach (var deaths in encounters)
        {
            if (deaths == 0)
                streak++;
            else
                break;
        }

        return streak;
    }

    /// <summary>
    /// Get deathless streak at a specific point in time (for incremental checking).
    /// Returns the streak count ending at or before the specified time.
    /// </summary>
    public async Task<int> GetDeathlessStreakAtTimeAsync(
        Guid playerId,
        DateTimeOffset upToTime,
        CancellationToken ct)
    {
        var kills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => x.e.EncounterTime <= upToTime)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => x.pe.Deaths)
            .ToListAsync(ct);

        int streak = 0;
        foreach (var deaths in kills)
        {
            if (deaths == 0)
                streak++;
            else
                streak = 0;
        }

        return streak;
    }

    #endregion

    #region Session & Dedication

    /// <summary>
    /// Get total number of raid sessions (unique dates)
    /// </summary>
    public async Task<int> GetSessionCountAsync(Guid playerId, CancellationToken ct)
    {
        return await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId)
            .Select(x => x.e.EncounterTime.Date)
            .Distinct()
            .CountAsync(ct);
    }

    /// <summary>
    /// Get session dates in order, and when thresholds were reached
    /// </summary>
    public async Task<(int count, DateTimeOffset? threshold25, DateTimeOffset? threshold50)> GetSessionProgressAsync(
        Guid playerId, CancellationToken ct)
    {
        var sessionDates = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => x.e.EncounterTime.Date)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);

        var count = sessionDates.Count;
        var threshold25 = count >= 25 ? (DateTimeOffset?)sessionDates[24] : null;
        var threshold50 = count >= 50 ? (DateTimeOffset?)sessionDates[49] : null;

        return (count, threshold25, threshold50);
    }

    /// <summary>
    /// Get ordered list of session dates
    /// </summary>
    public async Task<List<DateTime>> GetSessionDatesAsync(Guid playerId, CancellationToken ct)
    {
        return await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => x.e.EncounterTime.Date)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
    }

    #endregion

    #region Personal Bests

    /// <summary>
    /// Count times player beat their personal DPS best on any boss (only 2025+)
    /// </summary>
    public async Task<(int count, DateTimeOffset? threshold5)> GetPersonalBestCountAsync(
        Guid playerId, CancellationToken ct)
    {
        var encounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => x.e.EncounterTime >= AchievementBaselineDate)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.e.TriggerId, x.e.IsCM, x.pe.Dps, x.e.EncounterTime })
            .ToListAsync(ct);

        var personalBests = new Dictionary<(int, bool), int>();
        var pbCount = 0;
        DateTimeOffset? threshold5 = null;

        foreach (var enc in encounters)
        {
            var key = (enc.TriggerId, enc.IsCM);
            if (personalBests.TryGetValue(key, out var currentBest))
            {
                if (enc.Dps > currentBest)
                {
                    personalBests[key] = enc.Dps;
                    pbCount++;
                    if (pbCount == 5 && threshold5 == null)
                    {
                        threshold5 = enc.EncounterTime;
                    }
                }
            }
            else
            {
                personalBests[key] = enc.Dps;
            }
        }

        return (pbCount, threshold5);
    }

    /// <summary>
    /// Get the date when a specific personal best threshold was reached
    /// </summary>
    public async Task<DateTimeOffset?> GetPersonalBestAchievedAtAsync(
        Guid playerId,
        int threshold,
        CancellationToken ct)
    {
        var result = await GetPersonalBestCountAsync(playerId, ct);
        if (result.count >= threshold && threshold == 5)
        {
            return result.threshold5;
        }
        return null;
    }

    #endregion

    #region Support Stats

    /// <summary>
    /// Count encounters where player had most resurrects
    /// </summary>
    public async Task<int> GetMostResurrectsCountAsync(Guid playerId, CancellationToken ct)
    {
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success && x.pe.Resurrects > 0)
            .Select(x => new { x.pe.EncounterId, x.pe.Resurrects })
            .ToListAsync(ct);

        var count = 0;
        foreach (var pe in playerEncounters)
        {
            var maxInEncounter = await _db.PlayerEncounters
                .Where(x => x.EncounterId == pe.EncounterId)
                .MaxAsync(x => x.Resurrects, ct);

            if (pe.Resurrects == maxInEncounter)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Count encounters where player had most breakbar damage
    /// </summary>
    public async Task<int> GetMostCCCountAsync(Guid playerId, CancellationToken ct)
    {
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success && x.pe.BreakbarDamage > 0)
            .Select(x => new { x.pe.EncounterId, x.pe.BreakbarDamage })
            .ToListAsync(ct);

        var count = 0;
        foreach (var pe in playerEncounters)
        {
            var maxInEncounter = await _db.PlayerEncounters
                .Where(x => x.EncounterId == pe.EncounterId)
                .MaxAsync(x => x.BreakbarDamage ?? 0, ct);

            if (pe.BreakbarDamage == maxInEncounter)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Count encounters where player had highest boon DPS (as a support)
    /// </summary>
    public async Task<int> GetHighestBoonDpsCountAsync(Guid playerId, CancellationToken ct)
    {
        var playerEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => (x.pe.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                        (x.pe.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
            .Select(x => new { x.pe.EncounterId, x.pe.Dps })
            .ToListAsync(ct);

        var count = 0;
        foreach (var pe in playerEncounters)
        {
            var boonPlayersInEncounter = await _db.PlayerEncounters
                .Where(x => x.EncounterId == pe.EncounterId)
                .Where(x => (x.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
                            (x.AlacracityGeneration ?? 0) >= BoonSupportThreshold)
                .ToListAsync(ct);

            var maxBoonDps = boonPlayersInEncounter.Count > 0 ? boonPlayersInEncounter.Max(x => x.Dps) : 0;
            if (pe.Dps == maxBoonDps)
                count++;
        }

        return count;
    }

    #endregion

    #region Social / Partner Kills

    /// <summary>
    /// Get max kills with any single partner (squad-wide)
    /// </summary>
    public async Task<int> GetMaxPartnerKillsAsync(Guid playerId, CancellationToken ct)
    {
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => x.e.Id)
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return 0;

        var partnerCounts = await _db.PlayerEncounters
            .Where(pe => myEncounters.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .GroupBy(pe => pe.PlayerId)
            .Select(g => g.Count())
            .ToListAsync(ct);

        return partnerCounts.Count > 0 ? partnerCounts.Max() : 0;
    }

    /// <summary>
    /// Find when threshold kills with a partner was reached
    /// </summary>
    public async Task<(DateTimeOffset achievedAt, Guid partnerId)?> GetPartnerKillsWithDateAsync(
        Guid playerId, int threshold, CancellationToken ct)
    {
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.e.Id, x.e.EncounterTime })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return null;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();

        var partnersByEncounter = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId })
            .ToListAsync(ct);

        var partnerLookup = partnersByEncounter
            .GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToHashSet());

        var partnerCounts = new Dictionary<Guid, int>();
        foreach (var enc in myEncounters)
        {
            if (!partnerLookup.TryGetValue(enc.Id, out var partners)) continue;
            foreach (var partnerId in partners)
            {
                partnerCounts.TryGetValue(partnerId, out var count);
                partnerCounts[partnerId] = count + 1;

                if (partnerCounts[partnerId] == threshold)
                {
                    return (enc.EncounterTime, partnerId);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Get max kills with any pair of partners (trio)
    /// </summary>
    public async Task<int> GetMaxTrioKillsAsync(Guid playerId, CancellationToken ct)
    {
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => x.e.Id)
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return 0;

        var coPlayers = await _db.PlayerEncounters
            .Where(pe => myEncounters.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId })
            .ToListAsync(ct);

        var encounterPlayers = coPlayers.GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToHashSet());

        var pairCounts = new Dictionary<(Guid, Guid), int>();
        foreach (var (_, players) in encounterPlayers)
        {
            var playerList = players.ToList();
            for (var i = 0; i < playerList.Count; i++)
            {
                for (var j = i + 1; j < playerList.Count; j++)
                {
                    var pair = playerList[i].CompareTo(playerList[j]) < 0
                        ? (playerList[i], playerList[j])
                        : (playerList[j], playerList[i]);

                    pairCounts.TryGetValue(pair, out var count);
                    pairCounts[pair] = count + 1;
                }
            }
        }

        return pairCounts.Count > 0 ? pairCounts.Values.Max() : 0;
    }

    /// <summary>
    /// Find when threshold kills with a trio was reached
    /// </summary>
    public async Task<(DateTimeOffset achievedAt, Guid partner1, Guid partner2)?> GetTrioKillsWithDateAsync(
        Guid playerId, int threshold, CancellationToken ct)
    {
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.e.Id, x.e.EncounterTime })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return null;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();

        var partnersByEncounter = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId })
            .ToListAsync(ct);

        var partnerLookup = partnersByEncounter
            .GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToList());

        var pairCounts = new Dictionary<(Guid, Guid), int>();
        foreach (var enc in myEncounters)
        {
            if (!partnerLookup.TryGetValue(enc.Id, out var players)) continue;

            for (var i = 0; i < players.Count; i++)
            {
                for (var j = i + 1; j < players.Count; j++)
                {
                    var pair = players[i].CompareTo(players[j]) < 0
                        ? (players[i], players[j])
                        : (players[j], players[i]);

                    pairCounts.TryGetValue(pair, out var count);
                    pairCounts[pair] = count + 1;

                    if (pairCounts[pair] == threshold)
                    {
                        return (enc.EncounterTime, pair.Item1, pair.Item2);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Get max kills with a party member (same subgroup)
    /// </summary>
    public async Task<int> GetMaxPartyPartnerKillsAsync(Guid playerId, CancellationToken ct)
    {
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => new { x.e.Id, x.pe.SquadGroup })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return 0;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();
        var myGroupByEncounter = myEncounters.ToDictionary(e => e.Id, e => e.SquadGroup);

        var partyMembers = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId, pe.SquadGroup })
            .ToListAsync(ct);

        var partnerCounts = new Dictionary<Guid, int>();
        foreach (var member in partyMembers)
        {
            if (!myGroupByEncounter.TryGetValue(member.EncounterId, out var myGroup)) continue;
            if (myGroup != null && member.SquadGroup == myGroup)
            {
                partnerCounts.TryGetValue(member.PlayerId, out var count);
                partnerCounts[member.PlayerId] = count + 1;
            }
        }

        return partnerCounts.Count > 0 ? partnerCounts.Values.Max() : 0;
    }

    /// <summary>
    /// Find when threshold kills with a party partner was reached
    /// </summary>
    public async Task<(DateTimeOffset achievedAt, Guid partnerId)?> GetPartyPartnerKillsWithDateAsync(
        Guid playerId, int threshold, CancellationToken ct)
    {
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.e.Id, x.e.EncounterTime, x.pe.SquadGroup })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return null;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();
        var myGroupByEncounter = myEncounters.ToDictionary(e => e.Id, e => e.SquadGroup);

        var partyMembers = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId, pe.SquadGroup })
            .ToListAsync(ct);

        var partnerLookup = partyMembers
            .Where(m => myGroupByEncounter.TryGetValue(m.EncounterId, out var myGroup) && myGroup != null && m.SquadGroup == myGroup)
            .GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToHashSet());

        var partnerCounts = new Dictionary<Guid, int>();
        foreach (var enc in myEncounters)
        {
            if (!partnerLookup.TryGetValue(enc.Id, out var partners)) continue;
            foreach (var partnerId in partners)
            {
                partnerCounts.TryGetValue(partnerId, out var count);
                partnerCounts[partnerId] = count + 1;

                if (partnerCounts[partnerId] == threshold)
                {
                    return (enc.EncounterTime, partnerId);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Get max kills with a party trio (same subgroup)
    /// </summary>
    public async Task<int> GetMaxPartyTrioKillsAsync(Guid playerId, CancellationToken ct)
    {
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Select(x => new { x.e.Id, x.pe.SquadGroup })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return 0;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();
        var myGroupByEncounter = myEncounters.ToDictionary(e => e.Id, e => e.SquadGroup);

        var partyMembers = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId, pe.SquadGroup })
            .ToListAsync(ct);

        var partnerLookup = partyMembers
            .Where(m => myGroupByEncounter.TryGetValue(m.EncounterId, out var myGroup) && myGroup != null && m.SquadGroup == myGroup)
            .GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToList());

        var pairCounts = new Dictionary<(Guid, Guid), int>();
        foreach (var encounterId in encounterIds)
        {
            if (!partnerLookup.TryGetValue(encounterId, out var players)) continue;

            for (var i = 0; i < players.Count; i++)
            {
                for (var j = i + 1; j < players.Count; j++)
                {
                    var pair = players[i].CompareTo(players[j]) < 0
                        ? (players[i], players[j])
                        : (players[j], players[i]);

                    pairCounts.TryGetValue(pair, out var count);
                    pairCounts[pair] = count + 1;
                }
            }
        }

        return pairCounts.Count > 0 ? pairCounts.Values.Max() : 0;
    }

    /// <summary>
    /// Find when threshold kills with a party trio was reached
    /// </summary>
    public async Task<(DateTimeOffset achievedAt, Guid partner1, Guid partner2)?> GetPartyTrioKillsWithDateAsync(
        Guid playerId, int threshold, CancellationToken ct)
    {
        var myEncounters = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .OrderBy(x => x.e.EncounterTime)
            .Select(x => new { x.e.Id, x.e.EncounterTime, x.pe.SquadGroup })
            .ToListAsync(ct);

        if (myEncounters.Count == 0) return null;

        var encounterIds = myEncounters.Select(e => e.Id).ToHashSet();
        var myGroupByEncounter = myEncounters.ToDictionary(e => e.Id, e => e.SquadGroup);

        var partyMembers = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && pe.PlayerId != playerId)
            .Select(pe => new { pe.EncounterId, pe.PlayerId, pe.SquadGroup })
            .ToListAsync(ct);

        var partnerLookup = partyMembers
            .Where(m => myGroupByEncounter.TryGetValue(m.EncounterId, out var myGroup) && myGroup != null && m.SquadGroup == myGroup)
            .GroupBy(x => x.EncounterId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PlayerId).ToList());

        var pairCounts = new Dictionary<(Guid, Guid), int>();
        foreach (var enc in myEncounters)
        {
            if (!partnerLookup.TryGetValue(enc.Id, out var players)) continue;

            for (var i = 0; i < players.Count; i++)
            {
                for (var j = i + 1; j < players.Count; j++)
                {
                    var pair = players[i].CompareTo(players[j]) < 0
                        ? (players[i], players[j])
                        : (players[j], players[i]);

                    pairCounts.TryGetValue(pair, out var count);
                    pairCounts[pair] = count + 1;

                    if (pairCounts[pair] == threshold)
                    {
                        return (enc.EncounterTime, pair.Item1, pair.Item2);
                    }
                }
            }
        }

        return null;
    }

    #endregion

    #region Spec Diversity

    /// <summary>
    /// Get count of unique elite specs used by player
    /// </summary>
    public async Task<int> GetUniqueEliteSpecCountAsync(Guid playerId, CancellationToken ct)
    {
        return await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .Select(x => x.pe.Profession)
            .Distinct()
            .CountAsync(ct);
    }

    /// <summary>
    /// Get the boss with the most spec diversity for this player
    /// </summary>
    public async Task<(string bossName, int specCount)?> GetTopBossBySpecDiversityAsync(Guid playerId, CancellationToken ct)
    {
        var killsByBoss = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .Select(x => new { x.e.TriggerId, x.e.BossName, x.pe.Profession })
            .ToListAsync(ct);

        if (killsByBoss.Count == 0) return null;

        var bossDiversity = killsByBoss
            .GroupBy(x => new { x.TriggerId, x.BossName })
            .Select(g => new { g.Key.BossName, SpecCount = g.Select(x => x.Profession).Distinct().Count() })
            .OrderByDescending(x => x.SpecCount)
            .FirstOrDefault();

        return bossDiversity != null ? (bossDiversity.BossName, bossDiversity.SpecCount) : null;
    }

    /// <summary>
    /// Get max kills on any single elite spec
    /// </summary>
    public async Task<int> GetMaxKillsOnSingleSpecAsync(Guid playerId, CancellationToken ct)
    {
        var specKills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .GroupBy(x => x.pe.Profession)
            .Select(g => g.Count())
            .ToListAsync(ct);

        return specKills.Count > 0 ? specKills.Max() : 0;
    }

    /// <summary>
    /// Get spec kill data for Master of One achievement
    /// </summary>
    public async Task<(string spec, int kills, DateTimeOffset hundredthKillDate)?> GetTopSpecKillsAsync(
        Guid playerId, CancellationToken ct)
    {
        var specKills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .Select(x => new { x.pe.Profession, x.e.EncounterTime })
            .ToListAsync(ct);

        var topSpecData = specKills
            .GroupBy(x => x.Profession)
            .Where(g => g.Count() >= 100)
            .Select(g => new
            {
                Spec = g.Key,
                Count = g.Count(),
                HundredthKillDate = g.OrderBy(x => x.EncounterTime).Skip(99).First().EncounterTime
            })
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();

        return topSpecData != null
            ? (topSpecData.Spec, topSpecData.Count, topSpecData.HundredthKillDate)
            : null;
    }

    /// <summary>
    /// Check if player has all 4 elite specs for a profession on any single boss
    /// </summary>
    public async Task<(string profession, string bossName, List<string> specs)?> GetClassCompletionistProgressAsync(
        Guid playerId, CancellationToken ct)
    {
        var specKills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => AchievementDefinitions.AllEliteSpecs.Contains(x.pe.Profession))
            .Select(x => new { x.pe.Profession, x.e.BossName })
            .Distinct()
            .ToListAsync(ct);

        var killsByBoss = specKills.GroupBy(x => x.BossName).ToList();

        foreach (var (profession, eliteSpecs) in AchievementDefinitions.EliteSpecsByProfession)
        {
            foreach (var bossGroup in killsByBoss)
            {
                var specsOnThisBoss = bossGroup.Select(x => x.Profession).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (eliteSpecs.All(spec => specsOnThisBoss.Contains(spec)))
                {
                    return (profession, bossGroup.Key, eliteSpecs.ToList());
                }
            }
        }

        return null;
    }

    #endregion

    #region Wing Master & Completion

    /// <summary>
    /// Get wing master progress for a specific wing
    /// </summary>
    public async Task<(int completed, int total, bool isComplete)> GetWingMasterProgressAsync(
        Guid playerId, int wingNum, CancellationToken ct)
    {
        var bosses = AchievementDefinitions.WingMasterBosses[wingNum];
        var totalRequired = bosses.Length * AchievementDefinitions.RequiredRoles.Length;
        var completed = 0;

        foreach (var bossId in bosses)
        {
            var matchingTriggerIds = bossId == 16137
                ? new[] { 16137, 16115 }
                : new[] { bossId };

            foreach (var role in AchievementDefinitions.RequiredRoles)
            {
                var hasRole = await _db.PlayerEncounters
                    .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
                    .AnyAsync(x =>
                        x.pe.PlayerId == playerId &&
                        x.e.Success &&
                        matchingTriggerIds.Contains(x.e.TriggerId) &&
                        x.pe.Role == role, ct);

                if (hasRole) completed++;
            }
        }

        return (completed, totalRequired, completed == totalRequired);
    }

    /// <summary>
    /// Check completion achievement (all bosses in wings 1-7)
    /// </summary>
    public async Task<(bool complete, DateTimeOffset? achievedAt)> CheckCompletionAsync(
        Guid playerId, CancellationToken ct)
    {
        var w1to7Bosses = AchievementDefinitions.WingMasterBosses
            .Where(kvp => kvp.Key <= 7)
            .SelectMany(kvp => kvp.Value)
            .Select(AchievementDefinitions.NormalizeTriggerId)
            .Distinct()
            .ToHashSet();

        var rawKills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => w1to7Bosses.Contains(x.e.TriggerId) || x.e.TriggerId == 16115)
            .Select(x => new { x.e.TriggerId, x.e.EncounterTime })
            .ToListAsync(ct);

        var firstKillsByBoss = rawKills
            .GroupBy(x => AchievementDefinitions.NormalizeTriggerId(x.TriggerId))
            .Select(g => new { BossId = g.Key, FirstKill = g.Min(x => x.EncounterTime) })
            .ToList();

        if (w1to7Bosses.All(b => firstKillsByBoss.Any(fk => fk.BossId == b)))
        {
            var achievedAt = firstKillsByBoss.Max(fk => fk.FirstKill);
            return (true, achievedAt);
        }

        return (false, null);
    }

    /// <summary>
    /// Check legendary raider achievement (all CMs in wings 3-7)
    /// </summary>
    public async Task<(bool complete, DateTimeOffset? achievedAt)> CheckLegendaryRaiderAsync(
        Guid playerId, CancellationToken ct)
    {
        var w1to7CmBosses = AchievementDefinitions.Wings1To7CMBosses;

        var rawCmKills = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success && x.e.IsCM)
            .Where(x => w1to7CmBosses.Contains(x.e.TriggerId))
            .Select(x => new { x.e.TriggerId, x.e.EncounterTime })
            .ToListAsync(ct);

        var firstCmKillsByBoss = rawCmKills
            .GroupBy(x => x.TriggerId)
            .Select(g => new { BossId = g.Key, FirstKill = g.Min(x => x.EncounterTime) })
            .ToList();

        if (w1to7CmBosses.All(b => firstCmKillsByBoss.Any(fk => fk.BossId == b)))
        {
            var achievedAt = firstCmKillsByBoss.Max(fk => fk.FirstKill);
            return (true, achievedAt);
        }

        return (false, null);
    }

    /// <summary>
    /// Check Wing 8 clear achievement
    /// </summary>
    public async Task<(bool complete, DateTimeOffset? achievedAt)> CheckWing8ClearAsync(
        Guid playerId, CancellationToken ct)
    {
        var w8Bosses = AchievementDefinitions.WingMasterBosses[8];
        var firstW8KillsByBoss = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success && !x.e.IsCM)
            .Where(x => w8Bosses.Contains(x.e.TriggerId))
            .GroupBy(x => x.e.TriggerId)
            .Select(g => new { BossId = g.Key, FirstKill = g.Min(x => x.e.EncounterTime) })
            .ToListAsync(ct);

        if (w8Bosses.All(b => firstW8KillsByBoss.Any(fk => fk.BossId == b)))
        {
            var achievedAt = firstW8KillsByBoss.Max(fk => fk.FirstKill);
            return (true, achievedAt);
        }

        return (false, null);
    }

    /// <summary>
    /// Check Wing 8 CM clear achievement
    /// </summary>
    public async Task<(bool complete, DateTimeOffset? achievedAt)> CheckWing8CmClearAsync(
        Guid playerId, CancellationToken ct)
    {
        var greerCmKill = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => x.e.TriggerId == 26725 && x.e.IsCM)
            .Select(x => (DateTimeOffset?)x.e.EncounterTime)
            .MinAsync(ct);

        var decimaCmKill = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => x.e.TriggerId == 26867)
            .Select(x => (DateTimeOffset?)x.e.EncounterTime)
            .MinAsync(ct);

        var uraCmKill = await _db.PlayerEncounters
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .Where(x => x.pe.PlayerId == playerId && x.e.Success)
            .Where(x => x.e.TriggerId == 26712 && x.e.IsCM)
            .Select(x => (DateTimeOffset?)x.e.EncounterTime)
            .MinAsync(ct);

        if (greerCmKill.HasValue && decimaCmKill.HasValue && uraCmKill.HasValue)
        {
            var achievedAt = new[] { greerCmKill.Value, decimaCmKill.Value, uraCmKill.Value }.Max();
            return (true, achievedAt);
        }

        return (false, null);
    }

    #endregion
}
