using IronNestFCS.Domain.Observation;

namespace IronNestFCS.Domain.Execution;

public sealed class FireGateAuthorization
{
    private readonly HashSet<BarrelId> _authorized;

    private FireGateAuthorization(IEnumerable<BarrelId> authorized) => _authorized = new(authorized);

    public static FireGateAuthorization Independent(BarrelId barrel) => new(new[] { barrel });

    public static FireGateAuthorization SameTrigger(BarrelId first, BarrelId second)
        => first == second ? throw new ArgumentException("SameTrigger requires two barrels.") : new(new[] { first, second });

    public bool MayReleaseSafety(BarrelId barrel) => _authorized.Contains(barrel);
}
