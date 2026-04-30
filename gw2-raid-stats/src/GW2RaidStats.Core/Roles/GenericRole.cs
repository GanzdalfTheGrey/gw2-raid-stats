namespace GW2RaidStats.Core.Roles;

public enum GenericRole
{
    AlacHeal = 1,
    QuickHeal = 2,
    AlacDpsPower = 3,
    AlacDpsCondi = 4,
    QuickDpsPower = 5,
    QuickDpsCondi = 6,
    DpsPower = 7,
    DpsCondi = 8
}

public enum RoleSlot
{
    Heal,
    BoonDps,
    Dps
}

public static class GenericRoleExtensions
{
    public static RoleSlot GetSlot(this GenericRole role) => role switch
    {
        GenericRole.AlacHeal or GenericRole.QuickHeal => RoleSlot.Heal,
        GenericRole.AlacDpsPower or GenericRole.AlacDpsCondi
            or GenericRole.QuickDpsPower or GenericRole.QuickDpsCondi => RoleSlot.BoonDps,
        GenericRole.DpsPower or GenericRole.DpsCondi => RoleSlot.Dps,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };

    public static string DisplayName(this GenericRole role) => role switch
    {
        GenericRole.AlacHeal => "Alac Heal",
        GenericRole.QuickHeal => "Quick Heal",
        GenericRole.AlacDpsPower => "Alac DPS Power",
        GenericRole.AlacDpsCondi => "Alac DPS Condi",
        GenericRole.QuickDpsPower => "Quick DPS Power",
        GenericRole.QuickDpsCondi => "Quick DPS Condi",
        GenericRole.DpsPower => "DPS Power",
        GenericRole.DpsCondi => "DPS Condi",
        _ => role.ToString()
    };
}
