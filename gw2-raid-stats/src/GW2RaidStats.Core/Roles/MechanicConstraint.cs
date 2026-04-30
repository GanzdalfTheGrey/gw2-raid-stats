namespace GW2RaidStats.Core.Roles;

public enum MechanicConstraint
{
    Any = 0,
    PreferHealer = 1,
    PreferBoonDps = 2,
    PreferDps = 3,
    RequiresHealer = 4,
    RequiresBoonDps = 5,
    RequiresDps = 6,
    RequiresPureDps = 7
}

public static class MechanicConstraintExtensions
{
    public static string DisplayName(this MechanicConstraint c) => c switch
    {
        MechanicConstraint.Any => "Any",
        MechanicConstraint.PreferHealer => "Prefer Healer",
        MechanicConstraint.PreferBoonDps => "Prefer Boon DPS",
        MechanicConstraint.PreferDps => "Prefer DPS",
        MechanicConstraint.RequiresHealer => "Requires Healer",
        MechanicConstraint.RequiresBoonDps => "Requires Boon DPS",
        MechanicConstraint.RequiresDps => "Requires DPS",
        MechanicConstraint.RequiresPureDps => "Requires Pure DPS",
        _ => c.ToString()
    };

    public static bool IsSlotAllowed(this MechanicConstraint c, RoleSlot slot, bool hardOnly = false)
    {
        return c switch
        {
            MechanicConstraint.Any => true,
            MechanicConstraint.PreferHealer => hardOnly || true,
            MechanicConstraint.PreferBoonDps => hardOnly || true,
            MechanicConstraint.PreferDps => hardOnly || true,
            MechanicConstraint.RequiresHealer => slot == RoleSlot.Heal,
            MechanicConstraint.RequiresBoonDps => slot == RoleSlot.BoonDps,
            MechanicConstraint.RequiresDps => slot is RoleSlot.BoonDps or RoleSlot.Dps,
            MechanicConstraint.RequiresPureDps => slot == RoleSlot.Dps,
            _ => true
        };
    }

    public static RoleSlot? PreferredSlot(this MechanicConstraint c) => c switch
    {
        MechanicConstraint.PreferHealer or MechanicConstraint.RequiresHealer => RoleSlot.Heal,
        MechanicConstraint.PreferBoonDps or MechanicConstraint.RequiresBoonDps => RoleSlot.BoonDps,
        MechanicConstraint.PreferDps => RoleSlot.Dps,
        MechanicConstraint.RequiresPureDps => RoleSlot.Dps,
        _ => null
    };
}
