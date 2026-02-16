using GW2RaidStats.Infrastructure.Services.Achievements.Checkers;

namespace GW2RaidStats.Infrastructure.Services.Achievements.Progress;

/// <summary>
/// Calculator for per-encounter statistics used by achievement checkers.
/// All methods are pure calculations with no side effects.
/// </summary>
public static class EncounterStatsCalculator
{
    // Threshold for considering someone a boon support (generation % to squad)
    public const decimal BoonSupportThreshold = 10m;

    #region DPS Calculations

    /// <summary>
    /// Calculate the total DPS for all players in the encounter
    /// </summary>
    public static int GetTotalSquadDps(IEnumerable<PlayerEncounterData> players)
        => players.Sum(p => p.Dps);

    /// <summary>
    /// Calculate a player's share of the total squad DPS as a percentage
    /// </summary>
    public static decimal GetPlayerDpsShare(int playerDps, int totalSquadDps)
        => totalSquadDps > 0 ? (decimal)playerDps / totalSquadDps * 100 : 0;

    /// <summary>
    /// Check if a player is carrying the squad (dealing 25%+ of total DPS)
    /// </summary>
    public static bool IsCarrying(int playerDps, int totalSquadDps, int minSquadSize = 8)
        => totalSquadDps > 0 && GetPlayerDpsShare(playerDps, totalSquadDps) >= 25;

    /// <summary>
    /// Get the player with the highest DPS in the encounter
    /// </summary>
    public static PlayerEncounterData? GetTopDpsPlayer(IEnumerable<PlayerEncounterData> players)
        => players.OrderByDescending(p => p.Dps).FirstOrDefault();

    #endregion

    #region Death/Down Calculations

    /// <summary>
    /// Get the total deaths for all players in the encounter
    /// </summary>
    public static int GetTotalDeaths(IEnumerable<PlayerEncounterData> players)
        => players.Sum(p => p.Deaths);

    /// <summary>
    /// Get the total downs for all players in the encounter
    /// </summary>
    public static int GetTotalDowns(IEnumerable<PlayerEncounterData> players)
        => players.Sum(p => p.Downs);

    /// <summary>
    /// Count deaths of all players except the specified player
    /// </summary>
    public static int GetSquadDeathsExcluding(IEnumerable<PlayerEncounterData> players, Guid playerId)
        => players.Where(p => p.PlayerId != playerId).Sum(p => p.Deaths);

    /// <summary>
    /// Check if a player survived while 5+ squadmates died (Clutch Player achievement)
    /// </summary>
    public static bool IsClutchPlayer(PlayerEncounterData player, IEnumerable<PlayerEncounterData> allPlayers)
        => player.Deaths == 0 && GetSquadDeathsExcluding(allPlayers, player.PlayerId) >= 5;

    #endregion

    #region Support Calculations

    /// <summary>
    /// Get the maximum resurrects in the encounter
    /// </summary>
    public static int GetMaxResurrects(IEnumerable<PlayerEncounterData> players)
        => players.Any() ? players.Max(p => p.Resurrects) : 0;

    /// <summary>
    /// Check if a player has the most resurrects in the encounter
    /// </summary>
    public static bool HasMostResurrects(PlayerEncounterData player, IEnumerable<PlayerEncounterData> allPlayers)
        => player.Resurrects > 0 && player.Resurrects == GetMaxResurrects(allPlayers);

    /// <summary>
    /// Get the maximum breakbar damage in the encounter
    /// </summary>
    public static int GetMaxBreakbarDamage(IEnumerable<PlayerEncounterData> players)
        => players.Any() ? players.Max(p => p.BreakbarDamage ?? 0) : 0;

    /// <summary>
    /// Check if a player has the most CC damage in the encounter
    /// </summary>
    public static bool HasMostCC(PlayerEncounterData player, IEnumerable<PlayerEncounterData> allPlayers)
        => (player.BreakbarDamage ?? 0) > 0 && player.BreakbarDamage == GetMaxBreakbarDamage(allPlayers);

    /// <summary>
    /// Check if a player is a boon support (quickness or alacrity >= 10%)
    /// </summary>
    public static bool IsBoonSupport(PlayerEncounterData player)
        => (player.QuicknessGeneration ?? 0) >= BoonSupportThreshold ||
           (player.AlacracityGeneration ?? 0) >= BoonSupportThreshold;

    /// <summary>
    /// Get all boon support players in the encounter
    /// </summary>
    public static IEnumerable<PlayerEncounterData> GetBoonSupportPlayers(IEnumerable<PlayerEncounterData> players)
        => players.Where(IsBoonSupport);

    /// <summary>
    /// Get the highest DPS among boon support players
    /// </summary>
    public static int GetMaxBoonDps(IEnumerable<PlayerEncounterData> players)
    {
        var boonPlayers = GetBoonSupportPlayers(players).ToList();
        return boonPlayers.Any() ? boonPlayers.Max(p => p.Dps) : 0;
    }

    /// <summary>
    /// Check if a player has the highest boon DPS in the encounter
    /// </summary>
    public static bool HasHighestBoonDps(PlayerEncounterData player, IEnumerable<PlayerEncounterData> allPlayers)
        => IsBoonSupport(player) && player.Dps == GetMaxBoonDps(allPlayers);

    #endregion

    #region Composition Calculations

    /// <summary>
    /// Get the distinct professions/elite specs used in the encounter
    /// </summary>
    public static List<string> GetProfessions(IEnumerable<PlayerEncounterData> players)
        => players.Select(p => p.Profession).ToList();

    /// <summary>
    /// Get the distinct base professions used in the encounter
    /// </summary>
    public static List<string> GetBaseProfessions(IEnumerable<PlayerEncounterData> players)
        => players.Select(p => AchievementDefinitions.GetBaseProfession(p.Profession)).ToList();

    /// <summary>
    /// Get the distinct subgroups in the encounter
    /// </summary>
    public static List<int?> GetSubgroups(IEnumerable<PlayerEncounterData> players)
        => players.Select(p => p.SquadGroup).Distinct().ToList();

    /// <summary>
    /// Check if all players are in the same subgroup
    /// </summary>
    public static bool AllInSameSubgroup(IEnumerable<PlayerEncounterData> players)
    {
        var subgroups = GetSubgroups(players);
        return subgroups.Count == 1 && subgroups[0] != null;
    }

    /// <summary>
    /// Check if all players are on the same base profession
    /// </summary>
    public static bool AllSameProfession(IEnumerable<PlayerEncounterData> players)
    {
        var baseProfessions = GetBaseProfessions(players);
        return baseProfessions.Distinct().Count() == 1;
    }

    /// <summary>
    /// Check if all players are on core professions (no elite specs)
    /// </summary>
    public static bool AllCoreProfessions(IEnumerable<PlayerEncounterData> players)
        => GetProfessions(players).All(AchievementDefinitions.IsCoreProfession);

    /// <summary>
    /// Check if all players are using only Heart of Thorns elite specs
    /// </summary>
    public static bool AllHotSpecs(IEnumerable<PlayerEncounterData> players)
        => GetProfessions(players).All(AchievementDefinitions.IsHotSpec);

    /// <summary>
    /// Check if all players are using only Path of Fire elite specs
    /// </summary>
    public static bool AllPofSpecs(IEnumerable<PlayerEncounterData> players)
        => GetProfessions(players).All(AchievementDefinitions.IsPofSpec);

    /// <summary>
    /// Check if all players are using only heavy armor classes
    /// </summary>
    public static bool AllHeavyArmor(IEnumerable<PlayerEncounterData> players)
    {
        var heavySpecs = AchievementDefinitions.ArmorClasses["Heavy"];
        return GetProfessions(players).All(p => heavySpecs.Contains(p));
    }

    /// <summary>
    /// Check if all players are using only light armor classes
    /// </summary>
    public static bool AllLightArmor(IEnumerable<PlayerEncounterData> players)
    {
        var lightSpecs = AchievementDefinitions.ArmorClasses["Light"];
        return GetProfessions(players).All(p => lightSpecs.Contains(p));
    }

    /// <summary>
    /// Check if all players are using only medium armor classes
    /// </summary>
    public static bool AllMediumArmor(IEnumerable<PlayerEncounterData> players)
    {
        var mediumSpecs = AchievementDefinitions.ArmorClasses["Medium"];
        return GetProfessions(players).All(p => mediumSpecs.Contains(p));
    }

    /// <summary>
    /// Get the count of unique elite specs in the encounter
    /// </summary>
    public static int GetUniqueEliteSpecCount(IEnumerable<PlayerEncounterData> players)
        => GetProfessions(players)
            .Where(p => AchievementDefinitions.AllEliteSpecs.Contains(p))
            .Distinct()
            .Count();

    /// <summary>
    /// Check if the squad has 10 different elite specs (No Duplicates achievement)
    /// </summary>
    public static bool HasNoDuplicateEliteSpecs(IEnumerable<PlayerEncounterData> players)
        => GetUniqueEliteSpecCount(players) >= 10;

    /// <summary>
    /// Get the count of unique base professions represented
    /// </summary>
    public static int GetUniqueProfessionCount(IEnumerable<PlayerEncounterData> players)
    {
        var allBaseProfessions = AchievementDefinitions.EliteSpecsByProfession.Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return GetBaseProfessions(players)
            .Where(p => allBaseProfessions.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    /// <summary>
    /// Check if all 9 professions are represented (Rainbow Squad achievement)
    /// </summary>
    public static bool HasRainbowSquad(IEnumerable<PlayerEncounterData> players)
        => GetUniqueProfessionCount(players) >= 9;

    #endregion

    #region Guild Member Filtering

    /// <summary>
    /// Filter players to only include guild members
    /// </summary>
    public static IEnumerable<PlayerEncounterData> GetGuildMembers(
        IEnumerable<PlayerEncounterData> players,
        HashSet<string> includedAccounts)
        => players.Where(p => includedAccounts.Contains(p.AccountName));

    /// <summary>
    /// Check if all players are guild members (no pugs)
    /// </summary>
    public static bool AllGuildMembers(
        IEnumerable<PlayerEncounterData> players,
        HashSet<string> includedAccounts)
        => players.All(p => includedAccounts.Contains(p.AccountName));

    /// <summary>
    /// Get the count of guild members in the encounter
    /// </summary>
    public static int GetGuildMemberCount(
        IEnumerable<PlayerEncounterData> players,
        HashSet<string> includedAccounts)
        => GetGuildMembers(players, includedAccounts).Count();

    #endregion

    #region Record Tracking

    /// <summary>
    /// Check if a player broke the DPS record for this boss
    /// </summary>
    /// <param name="playerDps">The player's DPS in this encounter</param>
    /// <param name="previousRecord">The previous record DPS for this boss</param>
    /// <returns>True if the player broke the record</returns>
    public static bool BrokeDpsRecord(int playerDps, int previousRecord)
        => playerDps > previousRecord;

    #endregion
}
