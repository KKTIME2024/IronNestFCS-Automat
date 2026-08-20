using IronNestFCS.Domain.Observation;

namespace IronNestFCS.Domain.Planning;

public static class ShellRules
{
    public static ShellKind Choose(TargetSnapshot target)
    {
        var immune = target.ImmuneShells;
        if (target.IsArmored || target.IsUnderground)
        {
            if (!immune.Contains(ShellKind.AP)) return ShellKind.AP;
            if (!immune.Contains(ShellKind.HE)) return ShellKind.HE;
            return ShellKind.HCHE;
        }
        if (!immune.Contains(ShellKind.DRIL)) return ShellKind.DRIL;
        if (!immune.Contains(ShellKind.LE)) return ShellKind.LE;
        if (!immune.Contains(ShellKind.HE)) return ShellKind.HE;
        return ShellKind.HCHE;
    }

    public static float BlastRadiusKm(ShellKind shell) => shell switch
    {
        ShellKind.AP => 0.10f,
        ShellKind.DRIL => 0.05f,
        ShellKind.HE => 0.19f,
        ShellKind.HCHE => 0.43f,
        ShellKind.LE => 0.10f,
        _ => 0f
    };

    public static float DamageRadiusKm(ShellKind shell) => shell switch
    {
        ShellKind.AP => 0.15f,
        ShellKind.DRIL => 0.07f,
        ShellKind.HE => 0.25f,
        ShellKind.HCHE => 0.55f,
        ShellKind.LE => 0.15f,
        _ => 0f
    };

    public static float FriendlySafeRadiusKm(ShellKind shell) => DamageRadiusKm(shell) * 1.2f;

    public static int Cost(ShellKind shell) => shell switch
    {
        ShellKind.HCHE => 18,
        ShellKind.DRIL => 3,
        _ => 10
    };
}
