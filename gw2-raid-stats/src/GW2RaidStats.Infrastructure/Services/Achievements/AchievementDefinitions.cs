namespace GW2RaidStats.Infrastructure.Services.Achievements;

/// <summary>
/// Static definitions for all achievements in the system.
/// Achievements are grouped by category for display purposes.
/// </summary>
public static class AchievementDefinitions
{
    /// <summary>
    /// All personal achievements
    /// </summary>
    public static readonly IReadOnlyList<AchievementDefinition> Personal = new List<AchievementDefinition>
    {
        // Wing Master (8) - All 5 roles on each boss in a wing
        new("wing_1_master", "Wing 1 Master", "Complete all roles on Vale Guardian, Gorseval, and Sabetha", AchievementCategory.WingMaster),
        new("wing_2_master", "Wing 2 Master", "Complete all roles on Slothasor and Matthias", AchievementCategory.WingMaster),
        new("wing_3_master", "Wing 3 Master", "Complete all roles on Keep Construct and Xera", AchievementCategory.WingMaster),
        new("wing_4_master", "Wing 4 Master", "Complete all roles on Cairn, Mursaat Overseer, Samarog, and Deimos", AchievementCategory.WingMaster),
        new("wing_5_master", "Wing 5 Master", "Complete all roles on Soulless Horror and Dhuum", AchievementCategory.WingMaster),
        new("wing_6_master", "Wing 6 Master", "Complete all roles on Conjured Amalgamate, Twin Largos, and Qadim", AchievementCategory.WingMaster),
        new("wing_7_master", "Wing 7 Master", "Complete all roles on Cardinal Adina, Cardinal Sabir, and Qadim the Peerless", AchievementCategory.WingMaster),
        new("wing_8_master", "Wing 8 Master", "Complete all roles on Greer, Decima, and Ura", AchievementCategory.WingMaster),

        // Completion (6)
        new("completion", "Completion", "Kill every boss in Wings 1-7", AchievementCategory.Completion),
        new("legendary_raider", "Legendary Raider", "Kill every CM boss in Wings 3-7", AchievementCategory.Completion),
        new("wing_8_clear", "Wing 8 Clear", "Complete all Wing 8 bosses", AchievementCategory.Completion),
        new("wing_8_cm_clear", "Wing 8 CM Clear", "Complete all Wing 8 CMs", AchievementCategory.Completion),
        new("guardians_glade_clear", "The Crabs and The Bees", "Complete Guardian's Glade strike mission", AchievementCategory.Completion),
        new("guardians_glade_flawless", "Cajun Seafood Boil", "Complete Guardian's Glade without being hit by Scalding Wave", AchievementCategory.Completion),

        // Performance (5)
        new("the_carry", "The Carry", "Deal 25%+ of your squad's total DPS in a successful kill", AchievementCategory.Performance),
        new("immortal", "Immortal", "Complete 10 consecutive kills without dying", AchievementCategory.Performance),
        new("clutch_player", "Clutch Player", "Survive a successful kill where 5+ squadmates died", AchievementCategory.Performance),
        new("speed_demon", "Speed Demon", "Participate in a guild record kill time", AchievementCategory.Performance),
        new("witness_me", "Witness Me", "Be the only one alive when the boss dies", AchievementCategory.Performance),

        // Records (1)
        new("former_champion", "Former Champion", "Held a guild DPS record at any point in time", AchievementCategory.Records),

        // Spec Diversity (4)
        new("versatile", "Versatile", "Complete a boss on 10 different elite specs", AchievementCategory.SpecDiversity),
        new("jack_of_all_trades", "Jack of All Trades", "Complete a boss on 20 different elite specs", AchievementCategory.SpecDiversity),
        new("class_completionist", "Class Completionist", "Complete a boss on every elite spec for a single profession", AchievementCategory.SpecDiversity),
        new("master_of_one", "Master of One", "Complete 100 kills on the same elite spec", AchievementCategory.SpecDiversity),

        // Support Recognition (4)
        new("guardian_angel", "Guardian Angel", "Have the most resurrects in a successful kill (5+ times)", AchievementCategory.Support),
        new("cc_champion", "CC Champion", "Deal the most breakbar damage in a successful kill (10+ times)", AchievementCategory.Support),
        new("the_enabler", "The Enabler", "Have the highest boon DPS in a successful kill (25+ times)", AchievementCategory.Support),
        new("ambulance", "Ambulance", "Resurrect 5+ teammates in a single encounter", AchievementCategory.Support),

        // Dedication (2)
        new("the_regular", "The Regular", "Participate in 25 raid sessions", AchievementCategory.Dedication),
        new("dedicated", "Dedicated", "Participate in 50 raid sessions", AchievementCategory.Dedication),

        // Growth (1)
        new("keeping_up", "Keeping Up", "Beat your personal DPS best on a single boss 5+ times", AchievementCategory.Growth),

        // Social (3)
        new("dynamic_duo", "Dynamic Duo", "Complete 50 bosses with the same party member", AchievementCategory.Social),
        new("trio", "Trio", "Complete 25 bosses with the same two party members", AchievementCategory.Social),
        new("guild_pride", "Guild Pride", "Be part of a successful kill with only guild members (no pugs)", AchievementCategory.Social),

        // Shame (9)
        new("serial_downer", "Serial Downer", "Go downstate 5+ times in a single encounter without fully dying", AchievementCategory.Shame),
        new("backpack", "Backpack", "Die within the first minute of a boss and still clear", AchievementCategory.Shame),
        new("greedy", "Greedy", "Die to a boss under 10% HP", AchievementCategory.Shame),
        new("pacifist", "Pacifist", "Do the least damage on a boss kill", AchievementCategory.Shame),
        new("oil_change", "Fast Service Oil Change", "Be the first to step in oil on Deimos on a failed run", AchievementCategory.Shame),
        new("breakfast_special", "Breakfast Special", "Get egged by Gorseval and die to Sabetha's flame wall in the same session", AchievementCategory.Shame),
        new("glass_cannon", "Glass Cannon (Without the Cannon)", "Go down 3+ times while doing less DPS than a boon DPS", AchievementCategory.Shame),
        new("just_gg", "Just GG Already", "Be the last one alive on a wipe for 5+ seconds", AchievementCategory.Shame),
        new("the_sacrifice", "The Sacrifice", "Die during Matthias sacrifice mechanic", AchievementCategory.Shame)
    };

    /// <summary>
    /// All guild achievements
    /// </summary>
    public static readonly IReadOnlyList<GuildAchievementDefinition> Guild = new List<GuildAchievementDefinition>
    {
        // Class/Composition Challenges (7)
        new("one_trick_guild", "One Trick Guild", "Complete a boss with all 10 players on the same profession", GuildAchievementCategory.Composition),
        new("class_wing_clear", "Class Wing Clear", "Complete an entire wing with all players on the same profession", GuildAchievementCategory.Composition),
        new("heavy_metal", "Heavy Metal", "Complete an entire wing with only heavy armor professions, with at least one Guardian, one Warrior, and one Revenant (any elite spec)", GuildAchievementCategory.Composition),
        new("cloth_squad", "Cloth Squad", "Complete an entire wing with only light armor professions, with at least one Elementalist, one Mesmer, and one Necromancer (any elite spec)", GuildAchievementCategory.Composition),
        new("leather_lovers", "Leather Lovers", "Complete an entire wing with only medium armor professions, with at least one Engineer, one Ranger, and one Thief (any elite spec)", GuildAchievementCategory.Composition),
        new("heavy_metal_master", "Metal Bikini Brigade", "Complete Heavy Metal on all 8 wings", GuildAchievementCategory.Composition),
        new("cloth_squad_master", "Glass Cannons United", "Complete Cloth Squad on all 8 wings", GuildAchievementCategory.Composition),
        new("leather_lovers_master", "Cowhide Crusaders", "Complete Leather Lovers on all 8 wings", GuildAchievementCategory.Composition),
        new("triple_threat", "Triple Threat", "Complete Heavy Metal, Cloth Squad, and Leather Lovers on the same wing", GuildAchievementCategory.Composition),
        new("no_duplicates", "No Duplicates", "Complete a boss with 10 different elite specs (no repeats)", GuildAchievementCategory.Composition),
        new("rainbow_squad", "Rainbow Squad", "Complete a boss with at least one of each profession (9 classes)", GuildAchievementCategory.Composition),

        // Core/Expansion Composition Challenges
        new("core_memory", "Core Memory", "Complete an encounter with everyone on core classes (no elite specs)", GuildAchievementCategory.Composition),
        new("core_2_duo", "Core 2 Duo", "Complete an entire wing with everyone on core classes only", GuildAchievementCategory.Composition),
        new("chaos_strat", "Chaos Strat", "Complete an encounter with everyone in the same subgroup", GuildAchievementCategory.Composition),
        new("chaos_dunk", "Chaos Dunk", "Complete an entire wing with everyone in the same subgroup", GuildAchievementCategory.Composition),
        new("thorn_in_my_side", "Thorn in My Side", "Complete Wings 1-4 on only Heart of Thorns specializations", GuildAchievementCategory.Composition),
        new("ring_of_fire", "Ring of Fire", "Complete Wings 5-7 on only Path of Fire specializations", GuildAchievementCategory.Composition),

        // Profession-Specific Composition (9)
        new("all_elementalist", "Oops All Downstate", "Complete a wing with all players on Elementalist", GuildAchievementCategory.Composition),
        new("all_necromancer", "Shroud Squad", "Complete a wing with all players on Necromancer", GuildAchievementCategory.Composition),
        new("all_mesmer", "The Clone Wars", "Complete a wing with all players on Mesmer", GuildAchievementCategory.Composition),
        new("all_guardian", "Blue Man Group", "Complete a wing with all players on Guardian", GuildAchievementCategory.Composition),
        new("all_warrior", "Box of Crayons", "Complete a wing with all players on Warrior", GuildAchievementCategory.Composition),
        new("all_revenant", "Channel Surfers", "Complete a wing with all players on Revenant", GuildAchievementCategory.Composition),
        new("all_engineer", "Over-Engineered", "Complete a wing with all players on Engineer", GuildAchievementCategory.Composition),
        new("all_ranger", "Nature Documentary", "Complete a wing with all players on Ranger", GuildAchievementCategory.Composition),
        new("all_thief", "Pickpocket Convention", "Complete a wing with all players on Thief", GuildAchievementCategory.Composition),

        // Performance Challenges
        new("flawless_wing_1", "Flawless Wing 1", "Complete Wing 1 with 0 squad deaths in a single session", GuildAchievementCategory.Performance),
        new("flawless_wing_2", "Flawless Wing 2", "Complete Wing 2 with 0 squad deaths in a single session", GuildAchievementCategory.Performance),
        new("flawless_wing_3", "Flawless Wing 3", "Complete Wing 3 with 0 squad deaths in a single session", GuildAchievementCategory.Performance),
        new("flawless_wing_4", "Flawless Wing 4", "Complete Wing 4 with 0 squad deaths in a single session", GuildAchievementCategory.Performance),
        new("flawless_wing_5", "Flawless Wing 5", "Complete Wing 5 with 0 squad deaths in a single session", GuildAchievementCategory.Performance),
        new("flawless_wing_6", "Flawless Wing 6", "Complete Wing 6 with 0 squad deaths in a single session", GuildAchievementCategory.Performance),
        new("flawless_wing_7", "Flawless Wing 7", "Complete Wing 7 with 0 squad deaths in a single session", GuildAchievementCategory.Performance),
        new("flawless_wing_8", "Flawless Wing 8", "Complete Wing 8 with 0 squad deaths in a single session", GuildAchievementCategory.Performance),
        new("untouchable", "Untouchable", "Complete a boss with 0 downs across the entire squad", GuildAchievementCategory.Performance),
        new("photo_finish", "Photo Finish", "Kill a boss in the final 10 seconds before enrage", GuildAchievementCategory.Performance),

        // Fun/Rare Moments
        new("bench_warmers", "Bench Warmers", "Complete a boss with 7 or fewer players", GuildAchievementCategory.FunRare),
        new("synchronized", "Synchronized", "3+ players set a personal best DPS in the same encounter", GuildAchievementCategory.FunRare),
        new("record_breakers", "Record Breakers", "Break DPS and boon DPS records in the same encounter", GuildAchievementCategory.FunRare),
        new("the_comeback", "The Comeback", "Kill a boss after wiping 5+ times on it in the same session", GuildAchievementCategory.FunRare),
        new("full_clear", "Full Clear", "Clear all 8 wings in a single session", GuildAchievementCategory.FunRare),

        // Musical Chairs - Different boon providers each boss in a wing (within same session)
        new("musical_chairs_w1", "Spirit Shuffle", "Complete Wing 1 with different boon providers on each boss", GuildAchievementCategory.FunRare),
        new("musical_chairs_w2", "Salvation Rotation", "Complete Wing 2 with different boon providers on each boss", GuildAchievementCategory.FunRare),
        new("musical_chairs_w3", "Stronghold Swap", "Complete Wing 3 with different boon providers on each boss", GuildAchievementCategory.FunRare),
        new("musical_chairs_w4", "Musical Chairs", "Complete Wing 4 with different boon providers on each boss", GuildAchievementCategory.FunRare),
        new("musical_chairs_w5", "Death's Dance", "Complete Wing 5 with different boon providers on each boss", GuildAchievementCategory.FunRare),
        new("musical_chairs_w6", "Mythwright Medley", "Complete Wing 6 with different boon providers on each boss", GuildAchievementCategory.FunRare),
        new("musical_chairs_w7", "Key Change", "Complete Wing 7 with different boon providers on each boss", GuildAchievementCategory.FunRare),
        new("musical_chairs_w8", "Balrior Ballet", "Complete Wing 8 with different boon providers on each boss", GuildAchievementCategory.FunRare)
    };

    /// <summary>
    /// Wing Master requirements - maps wing number to list of boss trigger IDs
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int[]> WingMasterBosses = new Dictionary<int, int[]>
    {
        { 1, new[] { 15438, 15429, 15375 } },           // VG, Gorseval, Sabetha
        { 2, new[] { 16123, 16137 } },                   // Slothasor, Matthias (canonical ID)
        { 3, new[] { 16235, 16246 } },                   // Keep Construct, Xera (skip Escort, TC)
        { 4, new[] { 17194, 17172, 17188, 17154 } },     // Cairn, MO, Samarog, Deimos
        { 5, new[] { 19767, 19450 } },                   // Soulless Horror, Dhuum
        { 6, new[] { 43974, 21105, 20934 } },            // CA, Twin Largos, Qadim
        { 7, new[] { 22006, 21964, 22000 } },            // Adina, Sabir, QtP
        { 8, new[] { 26725, 26774, 26712 } }             // Greer, Decima, Ura (NM trigger IDs)
    };

    /// <summary>
    /// Wing 8 CM trigger IDs
    /// </summary>
    // Wing 8 CM trigger IDs - Greer and Ura use same ID as NM (with IsCM flag), Decima has separate CM ID
    public static readonly int[] Wing8CMBosses = new[] { 26725, 26867, 26712 }; // Greer CM, Decima CM, Ura CM

    /// <summary>
    /// Bosses in Wings 1-7 that have CM versions (Wing 1, 2 have no CMs; Xera has no CM)
    /// Maps to the normal mode trigger ID (CM uses same trigger with IsCM flag)
    /// </summary>
    public static readonly HashSet<int> Wings1To7CMBosses = new()
    {
        // Wing 3 (only KC has CM, Xera does not)
        16235, // Keep Construct
        // Wing 4
        17194, // Cairn
        17172, // Mursaat Overseer
        17188, // Samarog
        17154, // Deimos
        // Wing 5
        19767, // Soulless Horror
        19450, // Dhuum
        // Wing 6
        43974, // Conjured Amalgamate
        21105, // Twin Largos
        20934, // Qadim
        // Wing 7
        22006, // Adina
        21964, // Sabir
        22000  // Qadim the Peerless
    };

    /// <summary>
    /// Maps alternative trigger IDs to their canonical ID (for Matthias)
    /// </summary>
    public static int NormalizeTriggerId(int triggerId) => triggerId switch
    {
        16115 => 16137, // Matthias alternate -> canonical
        _ => triggerId
    };

    /// <summary>
    /// Boss names by trigger ID for display purposes
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> BossNames = new Dictionary<int, string>
    {
        // Wing 1
        { 15438, "Vale Guardian" },
        { 15429, "Gorseval" },
        { 15375, "Sabetha" },
        // Wing 2
        { 16123, "Slothasor" },
        { 16137, "Matthias" },
        { 16115, "Matthias" },  // Alternate trigger ID
        // Wing 3
        { 16235, "Keep Construct" },
        { 16246, "Xera" },
        // Wing 4
        { 17194, "Cairn" },
        { 17172, "Mursaat Overseer" },
        { 17188, "Samarog" },
        { 17154, "Deimos" },
        // Wing 5
        { 19767, "Soulless Horror" },
        { 19450, "Dhuum" },
        // Wing 6
        { 43974, "Conjured Amalgamate" },
        { 21105, "Twin Largos" },
        { 20934, "Qadim" },
        // Wing 7
        { 22006, "Cardinal Adina" },
        { 21964, "Cardinal Sabir" },
        { 22000, "Qadim the Peerless" },
        // Wing 8 (NM)
        { 26725, "Greer" },
        { 26774, "Decima" },
        { 26712, "Ura" },
        // Wing 8 (CM) - Greer/Ura use same ID as NM, Decima has unique CM ID
        { 26867, "Decima CM" }
    };

    /// <summary>
    /// Role display names
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RoleDisplayNames = new Dictionary<string, string>
    {
        { "heal_alac", "Heal Alac" },
        { "heal_quick", "Heal Quick" },
        { "dps_alac", "DPS Alac" },
        { "dps_quick", "DPS Quick" },
        { "pure_dps", "Pure DPS" }
    };

    /// <summary>
    /// Required roles for Wing Master achievement
    /// </summary>
    public static readonly string[] RequiredRoles = { "heal_alac", "heal_quick", "dps_alac", "dps_quick", "pure_dps" };

    /// <summary>
    /// Elite specs per profession for Class Completionist achievement (4 per profession)
    /// HoT (2015), PoF (2017), EoD (2022), VoE (2025)
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> EliteSpecsByProfession = new Dictionary<string, string[]>
    {
        { "Guardian", new[] { "Dragonhunter", "Firebrand", "Willbender", "Luminary" } },
        { "Warrior", new[] { "Berserker", "Spellbreaker", "Bladesworn", "Paragon" } },
        { "Revenant", new[] { "Herald", "Renegade", "Vindicator", "Conduit" } },
        { "Engineer", new[] { "Scrapper", "Holosmith", "Mechanist", "Amalgam" } },
        { "Ranger", new[] { "Druid", "Soulbeast", "Untamed", "Galeshot" } },
        { "Thief", new[] { "Daredevil", "Deadeye", "Specter", "Antiquary" } },
        { "Elementalist", new[] { "Tempest", "Weaver", "Catalyst", "Evoker" } },
        { "Mesmer", new[] { "Chronomancer", "Mirage", "Virtuoso", "Troubadour" } },
        { "Necromancer", new[] { "Reaper", "Scourge", "Harbinger", "Ritualist" } }
    };

    /// <summary>
    /// All elite specs (for Versatile achievement tracking)
    /// </summary>
    public static readonly HashSet<string> AllEliteSpecs = EliteSpecsByProfession.Values
        .SelectMany(specs => specs)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Armor class groupings for composition achievements
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> ArmorClasses = new Dictionary<string, string[]>
    {
        { "Heavy", new[] { "Guardian", "Warrior", "Revenant", "Dragonhunter", "Firebrand", "Willbender", "Luminary", "Berserker", "Spellbreaker", "Bladesworn", "Paragon", "Herald", "Renegade", "Vindicator", "Conduit" } },
        { "Medium", new[] { "Engineer", "Ranger", "Thief", "Scrapper", "Holosmith", "Mechanist", "Amalgam", "Druid", "Soulbeast", "Untamed", "Galeshot", "Daredevil", "Deadeye", "Specter", "Antiquary" } },
        { "Light", new[] { "Elementalist", "Mesmer", "Necromancer", "Tempest", "Weaver", "Catalyst", "Evoker", "Chronomancer", "Mirage", "Virtuoso", "Troubadour", "Reaper", "Scourge", "Harbinger", "Ritualist" } }
    };

    /// <summary>
    /// Heart of Thorns elite specs (expansion 1 - 2015)
    /// </summary>
    public static readonly HashSet<string> HotEliteSpecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dragonhunter", "Berserker", "Herald", "Scrapper", "Druid", "Daredevil", "Tempest", "Chronomancer", "Reaper"
    };

    /// <summary>
    /// Path of Fire elite specs (expansion 2 - 2017)
    /// </summary>
    public static readonly HashSet<string> PofEliteSpecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Firebrand", "Spellbreaker", "Renegade", "Holosmith", "Soulbeast", "Deadeye", "Weaver", "Mirage", "Scourge"
    };

    /// <summary>
    /// End of Dragons elite specs (expansion 3 - 2022)
    /// </summary>
    public static readonly HashSet<string> EodEliteSpecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Willbender", "Bladesworn", "Vindicator", "Mechanist", "Untamed", "Specter", "Catalyst", "Virtuoso", "Harbinger"
    };

    /// <summary>
    /// Janthir Wilds elite specs (expansion 4 - 2025)
    /// </summary>
    public static readonly HashSet<string> JwEliteSpecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Luminary", "Paragon", "Conduit", "Amalgam", "Galeshot", "Antiquary", "Evoker", "Troubadour", "Ritualist"
    };

    /// <summary>
    /// Core professions (base classes with no elite spec)
    /// </summary>
    public static readonly HashSet<string> CoreProfessions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Guardian", "Warrior", "Revenant", "Engineer", "Ranger", "Thief", "Elementalist", "Mesmer", "Necromancer"
    };

    /// <summary>
    /// Check if a profession/spec is a core class (not an elite spec)
    /// </summary>
    public static bool IsCoreProfession(string profession)
    {
        if (string.IsNullOrEmpty(profession))
            return false;
        return CoreProfessions.Contains(profession);
    }

    /// <summary>
    /// Check if a profession/spec is a Heart of Thorns elite spec
    /// </summary>
    public static bool IsHotSpec(string profession)
    {
        if (string.IsNullOrEmpty(profession))
            return false;
        return HotEliteSpecs.Contains(profession);
    }

    /// <summary>
    /// Check if a profession/spec is a Path of Fire elite spec
    /// </summary>
    public static bool IsPofSpec(string profession)
    {
        if (string.IsNullOrEmpty(profession))
            return false;
        return PofEliteSpecs.Contains(profession);
    }

    /// <summary>
    /// Maps elite spec/profession to base profession for counting
    /// </summary>
    public static string GetBaseProfession(string profession)
    {
        if (string.IsNullOrEmpty(profession))
            return profession;

        // Check if it's already a base profession (case-insensitive)
        foreach (var baseProfession in EliteSpecsByProfession.Keys)
        {
            if (string.Equals(baseProfession, profession, StringComparison.OrdinalIgnoreCase))
                return baseProfession;
        }

        // Find which base profession this elite spec belongs to
        foreach (var (baseProfession, eliteSpecs) in EliteSpecsByProfession)
        {
            if (eliteSpecs.Contains(profession, StringComparer.OrdinalIgnoreCase))
                return baseProfession;
        }

        // Unknown - return as-is (this will NOT count as a valid profession for Rainbow Squad)
        return profession;
    }

    /// <summary>
    /// Get all achievements by category
    /// </summary>
    public static IReadOnlyDictionary<AchievementCategory, List<AchievementDefinition>> PersonalByCategory =>
        Personal.GroupBy(a => a.Category).ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>
    /// Get all guild achievements by category
    /// </summary>
    public static IReadOnlyDictionary<GuildAchievementCategory, List<GuildAchievementDefinition>> GuildByCategory =>
        Guild.GroupBy(a => a.Category).ToDictionary(g => g.Key, g => g.ToList());
}

/// <summary>
/// Definition of a personal achievement
/// </summary>
public record AchievementDefinition(
    string Code,
    string Name,
    string Description,
    AchievementCategory Category
);

/// <summary>
/// Definition of a guild achievement
/// </summary>
public record GuildAchievementDefinition(
    string Code,
    string Name,
    string Description,
    GuildAchievementCategory Category
);

/// <summary>
/// Categories for personal achievements
/// </summary>
public enum AchievementCategory
{
    WingMaster,
    Completion,
    Performance,
    Records,
    SpecDiversity,
    Support,
    Dedication,
    Growth,
    Social,
    Shame
}

/// <summary>
/// Categories for guild achievements
/// </summary>
public enum GuildAchievementCategory
{
    Composition,
    Performance,
    FunRare
}
