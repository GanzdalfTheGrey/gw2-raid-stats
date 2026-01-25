using LinqToDB;
using LinqToDB.Async;
using GW2RaidStats.Infrastructure.Database;

namespace GW2RaidStats.Infrastructure.Services;

public class PlayerRecapService
{
    private readonly RaidStatsDb _db;
    private readonly IgnoredBossService _ignoredBossService;
    private readonly SettingsService _settingsService;

    // Mapping from elite specializations to base professions
    private static readonly Dictionary<string, string> SpecToBaseClass = new(StringComparer.OrdinalIgnoreCase)
    {
        // Mesmer
        { "Chronomancer", "Mesmer" },
        { "Mirage", "Mesmer" },
        { "Virtuoso", "Mesmer" },
        { "Troubadour", "Mesmer" },
        // Guardian
        { "Dragonhunter", "Guardian" },
        { "Firebrand", "Guardian" },
        { "Willbender", "Guardian" },
        { "Luminary", "Guardian" },
        // Warrior
        { "Berserker", "Warrior" },
        { "Spellbreaker", "Warrior" },
        { "Bladesworn", "Warrior" },
        { "Paragon", "Warrior" },
        // Revenant
        { "Herald", "Revenant" },
        { "Renegade", "Revenant" },
        { "Vindicator", "Revenant" },
        { "Conduit", "Revenant" },
        // Ranger
        { "Druid", "Ranger" },
        { "Soulbeast", "Ranger" },
        { "Untamed", "Ranger" },
        { "Galeshot", "Ranger" },
        // Thief
        { "Daredevil", "Thief" },
        { "Deadeye", "Thief" },
        { "Specter", "Thief" },
        { "Antiquary", "Thief" },
        // Engineer
        { "Scrapper", "Engineer" },
        { "Holosmith", "Engineer" },
        { "Mechanist", "Engineer" },
        { "Amalgam", "Engineer" },
        // Necromancer
        { "Reaper", "Necromancer" },
        { "Scourge", "Necromancer" },
        { "Harbinger", "Necromancer" },
        { "Ritualist", "Necromancer" },
        // Elementalist
        { "Tempest", "Elementalist" },
        { "Weaver", "Elementalist" },
        { "Catalyst", "Elementalist" },
        { "Evoker", "Elementalist" },
    };

    private static readonly HashSet<string> BaseProfessions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mesmer", "Guardian", "Warrior", "Revenant", "Ranger", "Thief", "Engineer", "Necromancer", "Elementalist"
    };

    // Boon support threshold - 20% works for both old squadBuffs data (~30-50%) and new groupBuffs data (~80-100%)
    private const decimal BoonSupportThreshold = 20m;

    public PlayerRecapService(
        RaidStatsDb db,
        IgnoredBossService ignoredBossService,
        SettingsService settingsService)
    {
        _db = db;
        _ignoredBossService = ignoredBossService;
        _settingsService = settingsService;
    }

    private static string GetBaseClass(string profession)
    {
        if (SpecToBaseClass.TryGetValue(profession, out var baseClass))
        {
            return baseClass;
        }
        return BaseProfessions.Contains(profession) ? profession : profession;
    }

    public async Task<PlayerYearlyRecap?> GetPlayerYearlyRecapAsync(
        Guid playerId,
        int year,
        bool includeIgnoredBosses = false,
        CancellationToken ct = default)
    {
        var yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var yearEnd = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Check if player exists
        var player = await _db.Players
            .Where(p => p.Id == playerId)
            .FirstOrDefaultAsync(ct);

        if (player == null)
            return null;

        // Get boss filter settings
        var ignoredBosses = includeIgnoredBosses
            ? new HashSet<(int, bool)>()
            : await _ignoredBossService.GetIgnoredKeysAsync(ct);

        // Get all player encounters for the year with encounter details
        var playerEncounters = await (
            from pe in _db.PlayerEncounters
            join e in _db.Encounters on pe.EncounterId equals e.Id
            where pe.PlayerId == playerId
                && e.EncounterTime >= yearStart
                && e.EncounterTime < yearEnd
            select new PlayerEncounterData
            {
                PlayerId = pe.PlayerId,
                EncounterId = pe.EncounterId,
                CharacterName = pe.CharacterName,
                Profession = pe.Profession,
                Dps = pe.Dps,
                Damage = pe.Damage,
                Deaths = pe.Deaths,
                Downs = pe.Downs,
                Resurrects = pe.Resurrects,
                CondiCleanse = pe.CondiCleanse,
                BoonStrips = pe.BoonStrips,
                Healing = pe.Healing,
                QuicknessGeneration = pe.QuicknessGeneration,
                AlacrityGeneration = pe.AlacracityGeneration,
                TriggerId = e.TriggerId,
                BossName = e.BossName,
                Wing = e.Wing,
                IsCM = e.IsCM,
                Success = e.Success,
                DurationMs = e.DurationMs,
                EncounterTime = e.EncounterTime
            }
        ).ToListAsync(ct);

        // Filter out ignored bosses
        var filteredEncounters = playerEncounters
            .Where(pe => !ignoredBosses.Contains((pe.TriggerId, pe.IsCM)))
            .ToList();

        if (filteredEncounters.Count == 0)
            return new PlayerYearlyRecap(
                playerId,
                player.AccountName,
                year,
                new PerformanceStats(0, 0, 0, TimeSpan.Zero, null, 0, 0, 0),
                new ProfessionStats("", 0, "", 0, []),
                new BossStats(null, null, null, []),
                new SupportStats(0, 0, 0, null)
            );

        // Calculate Performance Stats
        var performanceStats = await CalculatePerformanceStatsAsync(playerId, filteredEncounters, yearStart, yearEnd, ct);

        // Calculate Profession Stats
        var professionStats = CalculateProfessionStats(filteredEncounters);

        // Calculate Boss Stats
        var bossStats = CalculateBossStats(filteredEncounters);

        // Calculate Support Stats
        var supportStats = CalculateSupportStats(filteredEncounters);

        return new PlayerYearlyRecap(
            playerId,
            player.AccountName,
            year,
            performanceStats,
            professionStats,
            bossStats,
            supportStats
        );
    }

    private async Task<PerformanceStats> CalculatePerformanceStatsAsync(
        Guid playerId,
        List<PlayerEncounterData> encounters,
        DateTimeOffset yearStart,
        DateTimeOffset yearEnd,
        CancellationToken ct)
    {
        var totalEncounters = encounters.Count;
        var kills = encounters.Count(e => e.Success);
        var wipes = totalEncounters - kills;
        var totalTimeMs = encounters.Sum(e => e.DurationMs);
        var totalTime = TimeSpan.FromMilliseconds(totalTimeMs);

        // Personal best DPS (only from kills, non-support roles)
        var dpsEncounters = encounters
            .Where(e => e.Success)
            .Where(e => (e.QuicknessGeneration ?? 0) < BoonSupportThreshold
                     && (e.AlacrityGeneration ?? 0) < BoonSupportThreshold)
            .ToList();

        PersonalBestDps? personalBest = null;
        if (dpsEncounters.Count > 0)
        {
            var best = dpsEncounters.OrderByDescending(e => e.Dps).First();
            personalBest = new PersonalBestDps(
                best.Dps,
                best.BossName,
                best.IsCM,
                best.Profession,
                best.EncounterTime
            );
        }

        // Average DPS (only from kills, non-support roles)
        var avgDps = dpsEncounters.Count > 0
            ? (int)dpsEncounters.Average(e => e.Dps)
            : 0;

        // Times ranked #1 DPS in a run
        var timesTopDps = 0;
        var encounterIds = encounters.Select(e => e.EncounterId).Distinct().ToList();

        foreach (var encounterId in encounterIds)
        {
            var topPlayer = await _db.PlayerEncounters
                .Where(pe => pe.EncounterId == encounterId)
                .OrderByDescending(pe => pe.Dps)
                .FirstOrDefaultAsync(ct);

            if (topPlayer?.PlayerId == playerId)
            {
                timesTopDps++;
            }
        }

        // DPS improvement (first month avg vs last month avg)
        var firstMonthEnd = yearStart.AddMonths(1);
        var lastMonthStart = yearEnd.AddMonths(-1);

        var firstMonthDps = dpsEncounters
            .Where(e => e.EncounterTime < firstMonthEnd)
            .ToList();
        var lastMonthDps = dpsEncounters
            .Where(e => e.EncounterTime >= lastMonthStart)
            .ToList();

        int dpsImprovement = 0;
        if (firstMonthDps.Count > 0 && lastMonthDps.Count > 0)
        {
            var firstAvg = (int)firstMonthDps.Average(e => e.Dps);
            var lastAvg = (int)lastMonthDps.Average(e => e.Dps);
            dpsImprovement = lastAvg - firstAvg;
        }

        return new PerformanceStats(
            totalEncounters,
            kills,
            wipes,
            totalTime,
            personalBest,
            avgDps,
            timesTopDps,
            dpsImprovement
        );
    }

    private ProfessionStats CalculateProfessionStats(List<PlayerEncounterData> encounters)
    {
        // Most played profession (elite spec)
        var professionCounts = encounters
            .GroupBy(e => e.Profession)
            .Select(g => new { Profession = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        var mostPlayedProfession = professionCounts.FirstOrDefault()?.Profession ?? "";
        var mostPlayedCount = professionCounts.FirstOrDefault()?.Count ?? 0;

        // Most played character
        var characterCounts = encounters
            .GroupBy(e => e.CharacterName)
            .Select(g => new { Character = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        var mostPlayedCharacter = characterCounts.FirstOrDefault()?.Character ?? "";
        var characterCount = characterCounts.FirstOrDefault()?.Count ?? 0;

        // Profession breakdown (by base class)
        var baseClassCounts = encounters
            .GroupBy(e => GetBaseClass(e.Profession))
            .Select(g => new ProfessionBreakdown(g.Key, g.Count(), (double)g.Count() / encounters.Count * 100))
            .OrderByDescending(x => x.Count)
            .ToList();

        return new ProfessionStats(
            mostPlayedProfession,
            mostPlayedCount,
            mostPlayedCharacter,
            characterCount,
            baseClassCounts
        );
    }

    private BossStats CalculateBossStats(List<PlayerEncounterData> encounters)
    {
        var kills = encounters.Where(e => e.Success).ToList();

        // Most killed boss
        var bossKillCounts = kills
            .GroupBy(e => new { e.BossName, e.IsCM })
            .Select(g => new BossKillCount(g.Key.BossName, g.Key.IsCM, g.Count()))
            .OrderByDescending(x => x.Kills)
            .ToList();

        var mostKilledBoss = bossKillCounts.FirstOrDefault();

        // Favorite wing
        var wingCounts = kills
            .Where(e => e.Wing != null)
            .GroupBy(e => e.Wing!.Value)
            .Select(g => new { Wing = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();

        int? favoriteWing = wingCounts?.Wing;

        // Boss with most wipes
        var wipes = encounters.Where(e => !e.Success).ToList();
        var bossWipeCounts = wipes
            .GroupBy(e => new { e.BossName, e.IsCM })
            .Select(g => new BossKillCount(g.Key.BossName, g.Key.IsCM, g.Count()))
            .OrderByDescending(x => x.Kills)
            .FirstOrDefault();

        return new BossStats(
            mostKilledBoss,
            favoriteWing,
            bossWipeCounts,
            bossKillCounts.Take(10).ToList()
        );
    }

    private SupportStats CalculateSupportStats(List<PlayerEncounterData> encounters)
    {
        var totalResurrects = encounters.Sum(e => e.Resurrects ?? 0);
        var totalCondiCleanse = encounters.Sum(e => e.CondiCleanse ?? 0);
        var totalBoonStrips = encounters.Sum(e => e.BoonStrips ?? 0);

        // Total healing (if available)
        var totalHealing = encounters.Sum(e => (long)e.Healing);

        return new SupportStats(
            totalResurrects,
            totalCondiCleanse,
            totalBoonStrips,
            totalHealing > 0 ? totalHealing : null
        );
    }

    // Internal class to hold joined data
    private class PlayerEncounterData
    {
        public Guid PlayerId { get; set; }
        public Guid EncounterId { get; set; }
        public string CharacterName { get; set; } = "";
        public string Profession { get; set; } = "";
        public int Dps { get; set; }
        public long Damage { get; set; }
        public int Deaths { get; set; }
        public int Downs { get; set; }
        public int? Resurrects { get; set; }
        public int? CondiCleanse { get; set; }
        public int? BoonStrips { get; set; }
        public int Healing { get; set; }
        public decimal? QuicknessGeneration { get; set; }
        public decimal? AlacrityGeneration { get; set; }
        public int TriggerId { get; set; }
        public string BossName { get; set; } = "";
        public int? Wing { get; set; }
        public bool IsCM { get; set; }
        public bool Success { get; set; }
        public int DurationMs { get; set; }
        public DateTimeOffset EncounterTime { get; set; }
    }

    public async Task<List<int>> GetAvailableYearsAsync(Guid playerId, CancellationToken ct = default)
    {
        var years = await (
            from pe in _db.PlayerEncounters
            join e in _db.Encounters on pe.EncounterId equals e.Id
            where pe.PlayerId == playerId
            select e.EncounterTime.Year
        ).Distinct().OrderByDescending(y => y).ToListAsync(ct);

        return years;
    }
}

// DTOs
public record PlayerYearlyRecap(
    Guid PlayerId,
    string AccountName,
    int Year,
    PerformanceStats Performance,
    ProfessionStats Professions,
    BossStats Bosses,
    SupportStats Support
);

public record PerformanceStats(
    int TotalEncounters,
    int Kills,
    int Wipes,
    TimeSpan TotalRaidTime,
    PersonalBestDps? PersonalBest,
    int AverageDps,
    int TimesTopDps,
    int DpsImprovement
);

public record PersonalBestDps(
    int Dps,
    string BossName,
    bool IsCM,
    string Profession,
    DateTimeOffset EncounterTime
);

public record ProfessionStats(
    string MostPlayedProfession,
    int MostPlayedCount,
    string MostPlayedCharacter,
    int CharacterEncounterCount,
    List<ProfessionBreakdown> Breakdown
);

public record ProfessionBreakdown(
    string Profession,
    int Count,
    double Percentage
);

public record BossStats(
    BossKillCount? MostKilledBoss,
    int? FavoriteWing,
    BossKillCount? MostWipedBoss,
    List<BossKillCount> TopBosses
);

public record BossKillCount(
    string BossName,
    bool IsCM,
    int Kills
);

public record SupportStats(
    int TotalResurrects,
    int TotalCondiCleanse,
    int TotalBoonStrips,
    long? TotalHealing = null
);
