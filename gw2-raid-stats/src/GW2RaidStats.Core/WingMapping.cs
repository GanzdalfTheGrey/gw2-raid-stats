namespace GW2RaidStats.Core;

public static class WingMapping
{
    /// <summary>
    /// Encounters that should be ignored (not real boss fights)
    /// </summary>
    private static readonly HashSet<string> IgnoredEncounters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Spirit Race",
        "Twisted Castle",
        "River of Souls",  // This is an event, not a boss
        "Statues of Grenth", // Wing 5 event (Broken King, Eater of Souls, Eyes)
        "Bandit Trio" // Wing 2 event (Berg, Zane, Narella)
    };

    /// <summary>
    /// Encounters that should ALWAYS be allowed (never filtered out)
    /// Used for multi-target fights where each target needs to be tracked separately
    /// </summary>
    private static readonly HashSet<string> AlwaysAllowedEncounters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Nikare",   // Twin Largos - first twin
        "Kenut"     // Twin Largos - second twin
    };

    /// <summary>
    /// Trigger IDs for multi-target encounters where DPS should be calculated
    /// against ALL targets combined (dpsAll) rather than just the first target (dpsTargets[0]).
    /// This ensures leaderboards show the correct combined DPS for fights with multiple bosses.
    /// </summary>
    private static readonly HashSet<int> MultiTargetTriggerIds = new()
    {
        21105,          // Twin Largos (combined encounter)
        21089, 21177    // Twin Largos - Nikare/Kenut individual triggers
    };

    /// <summary>
    /// Check if an encounter is a multi-target fight where combined DPS should be used
    /// </summary>
    public static bool IsMultiTargetEncounter(int triggerId) => MultiTargetTriggerIds.Contains(triggerId);

    /// <summary>
    /// Check if an encounter should be ignored based on boss name
    /// </summary>
    public static bool IsIgnoredEncounter(string bossName)
    {
        // Never ignore explicitly allowed encounters (like Twin Largos twins)
        if (AlwaysAllowedEncounters.Any(allowed => bossName.Contains(allowed, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return IgnoredEncounters.Any(ignored => bossName.Contains(ignored, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Get wing number for a trigger ID. Returns null for non-raid content.
    /// </summary>
    public static int? GetWing(int triggerId) => triggerId switch
    {
        // Wing 1 - Spirit Vale
        15438 => 1,  // Vale Guardian
        15429 => 1,  // Gorseval
        15375 => 1,  // Sabetha

        // Wing 2 - Salvation Pass
        16123 => 2,  // Slothasor
        16088 => 2,  // Bandit Trio (Berg)
        16137 or 16115 => 2,  // Matthias Gabrel (both possible IDs)

        // Wing 3 - Stronghold of the Faithful
        16235 => 3,  // Escort
        16246 => 3,  // Keep Construct
        16286 => 3,  // Twisted Castle (event)
        16253 => 3,  // Xera

        // Wing 4 - Bastion of the Penitent
        17194 => 4,  // Cairn
        17172 => 4,  // Mursaat Overseer
        17188 => 4,  // Samarog
        17154 => 4,  // Deimos

        // Wing 5 - Hall of Chains
        19767 => 5,  // Soulless Horror (Desmina)
        // 19828 => 5,  // River of Souls (ignored - event)
        // 19536 => 5,  // Statues of Grenth (ignored - event)
        19450 => 5,  // Dhuum

        // Wing 6 - Mythwright Gambit
        43974 => 6,  // Conjured Amalgamate
        21105 or 21089 or 21177 => 6,  // Twin Largos (21105=combined, 21089=Nikare, 21177=Kenut)
        20934 => 6,  // Qadim

        // Wing 7 - The Key of Ahdashim
        22006 => 7,  // Cardinal Adina
        21964 => 7,  // Cardinal Sabir
        22000 => 7,  // Qadim the Peerless

        // Wing 8 - Mount Balrior (NM and CM trigger IDs)
        26725 or 26957 => 8,  // Greer / Massive Greer (CM)
        26774 or 26956 => 8,  // Decima / Godsquall Decima (CM)
        26712 or 26952 => 8,  // Ura / Ura the Adorned (CM)

        _ => null    // Strikes, fractals, etc.
    };

    /// <summary>
    /// Get wing by boss name (fallback when trigger ID is unknown)
    /// </summary>
    public static int? GetWingByBossName(string bossName)
    {
        // Wing 8 CM variants
        if (bossName.Contains("Greer", StringComparison.OrdinalIgnoreCase)) return 8;
        if (bossName.Contains("Decima", StringComparison.OrdinalIgnoreCase)) return 8;
        if (bossName.Contains("Ura", StringComparison.OrdinalIgnoreCase) &&
            !bossName.Contains("Drakkar", StringComparison.OrdinalIgnoreCase)) return 8;

        // Add more fallbacks as needed
        return null;
    }

    /// <summary>
    /// Get encounter order within wing for sorting (1-based)
    /// </summary>
    public static int GetEncounterOrder(int triggerId) => triggerId switch
    {
        // Wing 1
        15438 => 1,  // Vale Guardian
        15429 => 2,  // Gorseval
        15375 => 3,  // Sabetha

        // Wing 2
        16123 => 1,  // Slothasor
        16088 => 2,  // Bandit Trio
        16137 or 16115 => 3,  // Matthias

        // Wing 3
        16235 => 1,  // Escort
        16246 => 2,  // Keep Construct
        16286 => 3,  // Twisted Castle
        16253 => 4,  // Xera

        // Wing 4
        17194 => 1,  // Cairn
        17172 => 2,  // Mursaat Overseer
        17188 => 3,  // Samarog
        17154 => 4,  // Deimos

        // Wing 5
        19767 => 1,  // Soulless Horror
        // 19828, 19536 are ignored events
        19450 => 2,  // Dhuum

        // Wing 6
        43974 => 1,  // Conjured Amalgamate
        21105 or 21089 or 21177 => 2,  // Twin Largos (21105=combined, 21089=Nikare, 21177=Kenut)
        20934 => 3,  // Qadim

        // Wing 7
        22006 => 1,  // Cardinal Adina
        21964 => 2,  // Cardinal Sabir
        22000 => 3,  // Qadim the Peerless

        // Wing 8 (NM and CM)
        26725 or 26957 => 1,  // Greer / Massive Greer
        26774 or 26956 => 2,  // Decima / Godsquall Decima
        26712 or 26952 => 3,  // Ura / Ura the Adorned

        _ => 999     // Unknown encounters sort last
    };

    public static string GetWingName(int wing) => wing switch
    {
        1 => "Spirit Vale",
        2 => "Salvation Pass",
        3 => "Stronghold of the Faithful",
        4 => "Bastion of the Penitent",
        5 => "Hall of Chains",
        6 => "Mythwright Gambit",
        7 => "The Key of Ahdashim",
        8 => "Mount Balrior",
        _ => "Unknown"
    };
}
