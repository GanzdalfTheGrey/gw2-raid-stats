using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Infrastructure.Services;

public class RecapService
{
    private readonly RaidStatsDb _db;
    private readonly IncludedPlayerService _includedPlayerService;
    private readonly IgnoredBossService _ignoredBossService;
    private readonly SettingsService _settingsService;
    private readonly RecapFunStatsService _funStatsService;

    // Mapping from elite specializations to base professions
    private static readonly Dictionary<string, string> SpecToBaseClass = new(StringComparer.OrdinalIgnoreCase)
    {
        // Mesmer
        { "Chronomancer", "Mesmer" },
        { "Mirage", "Mesmer" },
        { "Virtuoso", "Mesmer" },
        // Guardian
        { "Dragonhunter", "Guardian" },
        { "Firebrand", "Guardian" },
        { "Willbender", "Guardian" },
        // Warrior
        { "Berserker", "Warrior" },
        { "Spellbreaker", "Warrior" },
        { "Bladesworn", "Warrior" },
        // Revenant
        { "Herald", "Revenant" },
        { "Renegade", "Revenant" },
        { "Vindicator", "Revenant" },
        // Ranger
        { "Druid", "Ranger" },
        { "Soulbeast", "Ranger" },
        { "Untamed", "Ranger" },
        // Thief
        { "Daredevil", "Thief" },
        { "Deadeye", "Thief" },
        { "Specter", "Thief" },
        // Engineer
        { "Scrapper", "Engineer" },
        { "Holosmith", "Engineer" },
        { "Mechanist", "Engineer" },
        // Necromancer
        { "Reaper", "Necromancer" },
        { "Scourge", "Necromancer" },
        { "Harbinger", "Necromancer" },
        // Elementalist
        { "Tempest", "Elementalist" },
        { "Weaver", "Elementalist" },
        { "Catalyst", "Elementalist" },
    };

    // Base professions (for when someone plays core)
    private static readonly HashSet<string> BaseProfessions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mesmer", "Guardian", "Warrior", "Revenant", "Ranger", "Thief", "Engineer", "Necromancer", "Elementalist"
    };

    public RecapService(
        RaidStatsDb db,
        IncludedPlayerService includedPlayerService,
        IgnoredBossService ignoredBossService,
        SettingsService settingsService,
        RecapFunStatsService funStatsService)
    {
        _db = db;
        _includedPlayerService = includedPlayerService;
        _ignoredBossService = ignoredBossService;
        _settingsService = settingsService;
        _funStatsService = funStatsService;
    }

    private static string GetBaseClass(string profession)
    {
        if (SpecToBaseClass.TryGetValue(profession, out var baseClass))
        {
            return baseClass;
        }
        // If it's already a base class or unknown, return as-is
        return BaseProfessions.Contains(profession) ? profession : profession;
    }

    public async Task<YearlyRecap> GetYearlyRecapAsync(int year, CancellationToken ct = default)
    {
        var yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var yearEnd = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Get included players and check boss filter setting
        var includedAccounts = await _includedPlayerService.GetIncludedAccountNamesAsync(ct);
        var includeAllBosses = await _settingsService.GetRecapIncludeAllBossesAsync(ct);
        var ignoredBosses = includeAllBosses
            ? new HashSet<(int, bool)>()
            : await _ignoredBossService.GetIgnoredKeysAsync(ct);

        // Get all encounters for the year
        var allEncounters = await _db.Encounters
            .Where(e => e.EncounterTime >= yearStart && e.EncounterTime < yearEnd)
            .ToListAsync(ct);

        // Filter out ignored bosses (unless includeAllBosses is true)
        var encounters = allEncounters
            .Where(e => !ignoredBosses.Contains((e.TriggerId, e.IsCM)))
            .ToList();

        if (encounters.Count == 0)
        {
            return new YearlyRecap(year, IsEmpty: true);
        }

        // Get player encounters for included players only
        var playerIds = await _db.Players
            .Where(p => includedAccounts.Contains(p.AccountName))
            .Select(p => p.Id)
            .ToListAsync(ct);

        var encounterIds = encounters.Select(e => e.Id).ToList();

        var playerEncounters = await _db.PlayerEncounters
            .Where(pe => encounterIds.Contains(pe.EncounterId) && playerIds.Contains(pe.PlayerId))
            .InnerJoin(_db.Encounters, (pe, e) => pe.EncounterId == e.Id, (pe, e) => new { pe, e })
            .InnerJoin(_db.Players, (x, p) => x.pe.PlayerId == p.Id, (x, p) => new { x.pe, x.e, p })
            .Select(x => new
            {
                x.pe.Id,
                x.pe.PlayerId,
                x.pe.EncounterId,
                x.pe.Profession,
                x.pe.Dps,
                x.pe.Deaths,
                x.pe.Damage,
                x.pe.BreakbarDamage,
                x.pe.Resurrects,
                x.e.BossName,
                x.e.TriggerId,
                x.e.IsCM,
                x.e.Success,
                x.e.DurationMs,
                x.e.EncounterTime,
                x.p.AccountName
            })
            .ToListAsync(ct);

        // Total stats
        var totalEncounters = encounters.Count;
        var totalKills = encounters.Count(e => e.Success);
        var totalWipes = totalEncounters - totalKills;
        var successRate = totalEncounters > 0 ? (decimal)totalKills / totalEncounters * 100 : 0;

        // Time raided (sum of encounter durations in hours)
        var totalDurationMs = encounters.Sum(e => (long)e.DurationMs);
        var hoursRaided = totalDurationMs / 3600000.0;

        // Most killed boss
        var bossKills = encounters
            .Where(e => e.Success)
            .GroupBy(e => new { e.BossName, e.IsCM })
            .Select(g => new BossKillStat(g.Key.BossName, g.Key.IsCM, g.Count()))
            .OrderByDescending(b => b.Kills)
            .ToList();
        var mostKilledBoss = bossKills.FirstOrDefault();

        // Most wiped boss
        var bossWipes = encounters
            .Where(e => !e.Success)
            .GroupBy(e => new { e.BossName, e.IsCM })
            .Select(g => new BossWipeStat(g.Key.BossName, g.Key.IsCM, g.Count()))
            .OrderByDescending(b => b.Wipes)
            .ToList();
        var mostWipedBoss = bossWipes.FirstOrDefault();

        // Most attempted bosses (total attempts with clear count)
        var mostAttemptedBosses = encounters
            .GroupBy(e => new { e.BossName, e.IsCM })
            .Select(g => new BossAttemptStat(
                g.Key.BossName,
                g.Key.IsCM,
                g.Count(),
                g.Count(e => e.Success)))
            .OrderByDescending(b => b.Attempts)
            .Take(5)
            .ToList();

        // Most played subclass (elite spec) across all included players
        var subclassPlays = playerEncounters
            .GroupBy(pe => pe.Profession)
            .Select(g => new ClassPlayStat(g.Key, g.Count()))
            .OrderByDescending(c => c.Count)
            .ToList();
        var favoriteSubclass = subclassPlays.FirstOrDefault();

        // Most played base class (Mesmer, Guardian, etc)
        var baseClassPlays = playerEncounters
            .GroupBy(pe => GetBaseClass(pe.Profession))
            .Select(g => new ClassPlayStat(g.Key, g.Count()))
            .OrderByDescending(c => c.Count)
            .ToList();
        var favoriteBaseClass = baseClassPlays.FirstOrDefault();

        // Top DPS performance
        var topDpsPerformance = playerEncounters
            .Where(pe => pe.Success)
            .OrderByDescending(pe => pe.Dps)
            .Take(1)
            .Select(pe => new TopDpsPerformance(
                pe.AccountName,
                pe.BossName,
                pe.IsCM,
                pe.Dps,
                pe.Profession,
                pe.EncounterTime
            ))
            .FirstOrDefault();

        // Total deaths
        var totalDeaths = playerEncounters.Sum(pe => pe.Deaths);

        // Player with most deaths
        var deathsByPlayer = playerEncounters
            .GroupBy(pe => pe.AccountName)
            .Select(g => new PlayerDeathStat(g.Key, g.Sum(pe => pe.Deaths)))
            .OrderByDescending(p => p.Deaths)
            .ToList();
        var mostDeathsPlayer = deathsByPlayer.FirstOrDefault();

        // Total damage done
        var totalDamage = playerEncounters.Sum(pe => pe.Damage);

        // Top 5 players by damage
        var topDamagePlayers = playerEncounters
            .GroupBy(pe => pe.AccountName)
            .Select(g => new PlayerDamageStat(g.Key, g.Sum(pe => pe.Damage)))
            .OrderByDescending(p => p.Damage)
            .Take(5)
            .ToList();

        // Total breakbar damage
        var totalBreakbarDamage = playerEncounters.Sum(pe => pe.BreakbarDamage ?? 0);

        // Top 5 players by breakbar damage
        var topBreakbarPlayers = playerEncounters
            .GroupBy(pe => pe.AccountName)
            .Select(g => new PlayerBreakbarStat(g.Key, g.Sum(pe => pe.BreakbarDamage ?? 0)))
            .OrderByDescending(p => p.BreakbarDamage)
            .Take(5)
            .ToList();

        // Clutch saves (most resurrects)
        var totalResurrects = playerEncounters.Sum(pe => pe.Resurrects);
        var clutchSavesPlayer = playerEncounters
            .GroupBy(pe => pe.AccountName)
            .Select(g => new PlayerResurrectStat(g.Key, g.Sum(pe => pe.Resurrects)))
            .OrderByDescending(p => p.Resurrects)
            .FirstOrDefault();

        // Most diverse player (played most different specs)
        var mostDiversePlayer = playerEncounters
            .GroupBy(pe => pe.AccountName)
            .Select(g => new PlayerDiversityStat(
                g.Key,
                g.Select(pe => pe.Profession).Distinct().Count(),
                g.Select(pe => pe.Profession).Distinct().ToList()))
            .OrderByDescending(p => p.UniqueSpecs)
            .FirstOrDefault();

        // Unique players that participated
        var uniquePlayers = playerEncounters.Select(pe => pe.AccountName).Distinct().Count();

        // First encounter of the year
        var firstEncounter = encounters.OrderBy(e => e.EncounterTime).FirstOrDefault();
        var lastEncounter = encounters.OrderByDescending(e => e.EncounterTime).FirstOrDefault();

        // Longest fight
        var longestFight = encounters.OrderByDescending(e => e.DurationMs).FirstOrDefault();

        // Fun facts
        var funFacts = GenerateFunFacts(
            hoursRaided,
            totalDeaths,
            totalKills,
            totalWipes,
            uniquePlayers
        );

        // Get fun stats (mechanic-based achievements)
        var funStatsAchievements = await GetFunStatsAchievementsAsync(year, includedAccounts, ct);

        return new YearlyRecap(
            Year: year,
            IsEmpty: false,
            TotalEncounters: totalEncounters,
            TotalKills: totalKills,
            TotalWipes: totalWipes,
            SuccessRate: successRate,
            HoursRaided: hoursRaided,
            UniqueRaiders: uniquePlayers,
            TotalDeaths: totalDeaths,
            TotalDamage: totalDamage,
            TotalBreakbarDamage: totalBreakbarDamage,
            MostKilledBoss: mostKilledBoss,
            MostWipedBoss: mostWipedBoss,
            MostAttemptedBosses: mostAttemptedBosses,
            FavoriteBaseClass: favoriteBaseClass,
            FavoriteSubclass: favoriteSubclass,
            TopDpsPerformance: topDpsPerformance,
            MostDeathsPlayer: mostDeathsPlayer,
            TopDamagePlayers: topDamagePlayers,
            TopBreakbarPlayers: topBreakbarPlayers,
            TotalResurrects: totalResurrects,
            ClutchSavesPlayer: clutchSavesPlayer,
            MostDiversePlayer: mostDiversePlayer,
            FirstEncounter: firstEncounter != null ? new EncounterSnapshot(firstEncounter.BossName, firstEncounter.IsCM, firstEncounter.Success, firstEncounter.EncounterTime) : null,
            LastEncounter: lastEncounter != null ? new EncounterSnapshot(lastEncounter.BossName, lastEncounter.IsCM, lastEncounter.Success, lastEncounter.EncounterTime) : null,
            LongestFight: longestFight != null ? new LongestFightStat(longestFight.BossName, longestFight.IsCM, longestFight.DurationMs / 1000.0) : null,
            TopBaseClasses: baseClassPlays.Take(5).ToList(),
            TopSubclasses: subclassPlays.Take(5).ToList(),
            FunFacts: funFacts,
            FunStatsAchievements: funStatsAchievements
        );
    }

    private async Task<List<FunStatAchievement>> GetFunStatsAchievementsAsync(
        int year,
        HashSet<string> includedAccounts,
        CancellationToken ct)
    {
        var achievements = new List<FunStatAchievement>();

        // Get enabled fun stats
        var enabledStats = await _funStatsService.GetEnabledAsync(ct);

        foreach (var stat in enabledStats)
        {
            // Get top player for this mechanic
            var leaderboard = await _funStatsService.GetMechanicLeaderboardAsync(
                stat.MechanicName,
                year,
                includedAccounts,
                limit: 1,
                ct);

            var winner = leaderboard.FirstOrDefault();
            if (winner != null && winner.Count > 0)
            {
                achievements.Add(new FunStatAchievement(
                    stat.DisplayTitle,
                    stat.Description,
                    stat.MechanicName,
                    stat.IsPositive,
                    winner.AccountName,
                    winner.Count
                ));
            }
        }

        return achievements;
    }

    public async Task<List<int>> GetAvailableYearsAsync(CancellationToken ct = default)
    {
        var currentYear = DateTime.UtcNow.Year;

        var years = await _db.Encounters
            .Select(e => e.EncounterTime.Year)
            .Distinct()
            .OrderByDescending(y => y)
            .ToListAsync(ct);

        // Exclude the current year since it's still ongoing
        return years.Where(y => y < currentYear).ToList();
    }

    private static List<string> GenerateFunFacts(
        double hoursRaided,
        int totalDeaths,
        int totalKills,
        int totalWipes,
        int uniquePlayers)
    {
        var facts = new List<string>();

        // Time comparisons
        if (hoursRaided > 100)
        {
            var days = hoursRaided / 24;
            facts.Add($"You spent {days:F1} full days raiding. That's dedication!");
        }
        else if (hoursRaided > 50)
        {
            facts.Add($"You could have watched {(int)(hoursRaided / 2)} movies in that raid time!");
        }
        else if (hoursRaided > 10)
        {
            var marathons = hoursRaided / 4.5;
            facts.Add($"That's enough time to run {marathons:F1} marathons!");
        }

        // Death facts
        if (totalDeaths > 500)
        {
            facts.Add($"With {totalDeaths} deaths, you've funded Dhuum's retirement plan!");
        }
        else if (totalDeaths > 200)
        {
            facts.Add($"{totalDeaths} deaths? The waypoint system thanks you for the business!");
        }
        else if (totalDeaths > 50)
        {
            facts.Add($"Only {totalDeaths} deaths? Your healers deserve a raise!");
        }

        // Kill/wipe ratios
        if (totalKills > totalWipes * 3)
        {
            facts.Add("3:1 kill ratio or better - you're basically speedrunners!");
        }
        else if (totalWipes > totalKills)
        {
            facts.Add("More wipes than kills? We call that 'learning opportunities'!");
        }

        // Team size
        if (uniquePlayers >= 20)
        {
            facts.Add($"With {uniquePlayers} different raiders, you're basically running a small army!");
        }
        else if (uniquePlayers >= 10)
        {
            facts.Add($"{uniquePlayers} raiders strong - a perfect raiding guild!");
        }

        return facts;
    }
}

public record YearlyRecap(
    int Year,
    bool IsEmpty,
    int TotalEncounters = 0,
    int TotalKills = 0,
    int TotalWipes = 0,
    decimal SuccessRate = 0,
    double HoursRaided = 0,
    int UniqueRaiders = 0,
    int TotalDeaths = 0,
    long TotalDamage = 0,
    decimal TotalBreakbarDamage = 0,
    BossKillStat? MostKilledBoss = null,
    BossWipeStat? MostWipedBoss = null,
    List<BossAttemptStat>? MostAttemptedBosses = null,
    ClassPlayStat? FavoriteBaseClass = null,
    ClassPlayStat? FavoriteSubclass = null,
    TopDpsPerformance? TopDpsPerformance = null,
    PlayerDeathStat? MostDeathsPlayer = null,
    List<PlayerDamageStat>? TopDamagePlayers = null,
    List<PlayerBreakbarStat>? TopBreakbarPlayers = null,
    int TotalResurrects = 0,
    PlayerResurrectStat? ClutchSavesPlayer = null,
    PlayerDiversityStat? MostDiversePlayer = null,
    EncounterSnapshot? FirstEncounter = null,
    EncounterSnapshot? LastEncounter = null,
    LongestFightStat? LongestFight = null,
    List<ClassPlayStat>? TopBaseClasses = null,
    List<ClassPlayStat>? TopSubclasses = null,
    List<string>? FunFacts = null,
    List<FunStatAchievement>? FunStatsAchievements = null
);

public record BossKillStat(string BossName, bool IsCM, int Kills);
public record BossWipeStat(string BossName, bool IsCM, int Wipes);
public record ClassPlayStat(string Profession, int Count);
public record TopDpsPerformance(string AccountName, string BossName, bool IsCM, int Dps, string Profession, DateTimeOffset EncounterTime);
public record PlayerDeathStat(string AccountName, int Deaths);
public record EncounterSnapshot(string BossName, bool IsCM, bool Success, DateTimeOffset EncounterTime);
public record LongestFightStat(string BossName, bool IsCM, double DurationSeconds);
public record FunStatAchievement(
    string Title,
    string? Description,
    string MechanicName,
    bool IsPositive,
    string WinnerAccountName,
    int Count
);
public record PlayerDamageStat(string AccountName, long Damage);
public record PlayerBreakbarStat(string AccountName, decimal BreakbarDamage);
public record BossAttemptStat(string BossName, bool IsCM, int Attempts, int Clears);
public record PlayerResurrectStat(string AccountName, int Resurrects);
public record PlayerDiversityStat(string AccountName, int UniqueSpecs, List<string> Specs);
