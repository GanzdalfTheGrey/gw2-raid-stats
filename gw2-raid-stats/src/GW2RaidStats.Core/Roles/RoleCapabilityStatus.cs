namespace GW2RaidStats.Core.Roles;

public enum RoleCapabilityStatus
{
    Cant = 0,
    Maybe = 1,
    Can = 2,
    WantToLearn = 3
}

public static class RoleCapabilityStatusExtensions
{
    public static string DisplayName(this RoleCapabilityStatus s) => s switch
    {
        RoleCapabilityStatus.Cant => "Can't",
        RoleCapabilityStatus.Maybe => "Maybe",
        RoleCapabilityStatus.Can => "Can",
        RoleCapabilityStatus.WantToLearn => "Want to Learn",
        _ => s.ToString()
    };
}
