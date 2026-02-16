using GW2RaidStats.Infrastructure.Services.Achievements.Progress;

namespace GW2RaidStats.Infrastructure.Services.Achievements.Checkers;

/// <summary>
/// Checks per-encounter guild challenge achievements:
/// - Composition challenges (One Trick Guild, Heavy Metal, Cloth Squad, etc.)
/// - Core Memory, Core 2 Duo
/// - Chaos Strat, Chaos Dunk
/// - No Duplicates, Rainbow Squad
/// - Bench Warmers
/// - Untouchable
/// </summary>
public class GuildChallengeChecker : IAchievementChecker
{
    public Task<List<AchievementUnlock>> CheckAsync(AchievementCheckContext context, CancellationToken ct)
    {
        var unlocks = new List<AchievementUnlock>();

        // Only check for successful kills
        if (!context.Encounter.Success) return Task.FromResult(unlocks);

        // Require at least 5 guild members for guild achievements
        if (context.GuildMemberCount < 5) return Task.FromResult(unlocks);

        var encounter = context.Encounter;
        var players = context.Players;
        var professions = EncounterStatsCalculator.GetProfessions(players);
        var baseProfessions = EncounterStatsCalculator.GetBaseProfessions(players);

        // One Trick Guild - all 10 on same profession
        if (players.Count >= 10 && EncounterStatsCalculator.AllSameProfession(players))
        {
            var profession = baseProfessions.First();
            unlocks.Add(new AchievementUnlock(
                "one_trick_guild",
                null, // Guild achievement
                new { encounter_id = encounter.Id, boss = encounter.BossName, profession },
                encounter.EncounterTime
            ));

            // Also award profession-specific achievement
            var professionCode = GetProfessionAchievementCode(profession);
            if (professionCode != null)
            {
                unlocks.Add(new AchievementUnlock(
                    professionCode,
                    null,
                    new { encounter_id = encounter.Id, boss = encounter.BossName, profession },
                    encounter.EncounterTime
                ));
            }
        }

        // Heavy Metal - only heavy armor
        if (EncounterStatsCalculator.AllHeavyArmor(players))
        {
            unlocks.Add(new AchievementUnlock(
                "heavy_metal",
                null,
                new { encounter_id = encounter.Id, boss = encounter.BossName },
                encounter.EncounterTime
            ));
        }

        // Cloth Squad - only light armor
        if (EncounterStatsCalculator.AllLightArmor(players))
        {
            unlocks.Add(new AchievementUnlock(
                "cloth_squad",
                null,
                new { encounter_id = encounter.Id, boss = encounter.BossName },
                encounter.EncounterTime
            ));
        }

        // Leather Lovers - only medium armor
        if (EncounterStatsCalculator.AllMediumArmor(players))
        {
            unlocks.Add(new AchievementUnlock(
                "leather_lovers",
                null,
                new { encounter_id = encounter.Id, boss = encounter.BossName },
                encounter.EncounterTime
            ));
        }

        // No Duplicates - 10 different elite specs
        if (EncounterStatsCalculator.HasNoDuplicateEliteSpecs(players))
        {
            unlocks.Add(new AchievementUnlock(
                "no_duplicates",
                null,
                new { encounter_id = encounter.Id, boss = encounter.BossName, specs = EncounterStatsCalculator.GetUniqueEliteSpecCount(players) },
                encounter.EncounterTime
            ));
        }

        // Rainbow Squad - all 9 professions represented
        if (EncounterStatsCalculator.HasRainbowSquad(players))
        {
            unlocks.Add(new AchievementUnlock(
                "rainbow_squad",
                null,
                new { encounter_id = encounter.Id, boss = encounter.BossName },
                encounter.EncounterTime
            ));
        }

        // Core Memory - everyone on core classes
        if (EncounterStatsCalculator.AllCoreProfessions(players))
        {
            unlocks.Add(new AchievementUnlock(
                "core_memory",
                null,
                new { encounter_id = encounter.Id, boss = encounter.BossName },
                encounter.EncounterTime
            ));
        }

        // Chaos Strat - everyone in same subgroup (raids only, 7+ players)
        if (encounter.Wing >= 1 && encounter.Wing <= 8 &&
            players.Count >= 7 &&
            EncounterStatsCalculator.AllInSameSubgroup(players))
        {
            var subgroup = players.First().SquadGroup;
            unlocks.Add(new AchievementUnlock(
                "chaos_strat",
                null,
                new { encounter_id = encounter.Id, boss = encounter.BossName, subgroup },
                encounter.EncounterTime
            ));
        }

        // Bench Warmers - kill with 7 or fewer players
        if (players.Count <= 7)
        {
            unlocks.Add(new AchievementUnlock(
                "bench_warmers",
                null,
                new { encounter_id = encounter.Id, boss = encounter.BossName, player_count = players.Count },
                encounter.EncounterTime
            ));
        }

        // Untouchable - 0 downs across entire squad
        if (EncounterStatsCalculator.GetTotalDowns(players) == 0)
        {
            unlocks.Add(new AchievementUnlock(
                "untouchable",
                null,
                new { encounter_id = encounter.Id, boss = encounter.BossName },
                encounter.EncounterTime
            ));
        }

        return Task.FromResult(unlocks);
    }

    private static string? GetProfessionAchievementCode(string? profession)
    {
        return profession?.ToLowerInvariant() switch
        {
            "elementalist" => "all_elementalist",
            "necromancer" => "all_necromancer",
            "mesmer" => "all_mesmer",
            "guardian" => "all_guardian",
            "warrior" => "all_warrior",
            "revenant" => "all_revenant",
            "engineer" => "all_engineer",
            "ranger" => "all_ranger",
            "thief" => "all_thief",
            _ => null
        };
    }
}
