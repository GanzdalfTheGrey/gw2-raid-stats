namespace GW2RaidStats.Core;

public static class WingMapping
{
    public static int? GetWing(int triggerId) => triggerId switch
    {
        // Wing 1 - Spirit Vale
        15438 => 1,  // Vale Guardian
        15429 => 1,  // Gorseval
        15375 => 1,  // Sabetha

        // Wing 2 - Salvation Pass
        16123 => 2,  // Slothasor
        16088 => 2,  // Bandit Trio
        16137 => 2,  // Matthias

        // Wing 3 - Stronghold of the Faithful
        16235 => 3,  // Escort
        16246 => 3,  // Keep Construct
        16286 => 3,  // Twisted Castle
        16253 => 3,  // Xera

        // Wing 4 - Bastion of the Penitent
        17194 => 4,  // Cairn
        17172 => 4,  // Mursaat Overseer
        17188 => 4,  // Samarog
        17154 => 4,  // Deimos

        // Wing 5 - Hall of Chains
        19767 => 5,  // Soulless Horror
        19828 => 5,  // River of Souls
        19536 => 5,  // Statues of Grenth
        19450 => 5,  // Dhuum

        // Wing 6 - Mythwright Gambit
        21105 => 6,  // Conjured Amalgamate
        21089 => 6,  // Twin Largos
        20934 => 6,  // Qadim

        // Wing 7 - The Key of Ahdashim
        22006 => 7,  // Cardinal Adina
        21964 => 7,  // Cardinal Sabir
        22000 => 7,  // Qadim the Peerless

        // Wing 8 - Mount Balrior
        26725 => 8,  // Greer
        26774 => 8,  // Decima
        26712 => 8,  // Ura

        _ => null    // Strikes, fractals, etc.
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
