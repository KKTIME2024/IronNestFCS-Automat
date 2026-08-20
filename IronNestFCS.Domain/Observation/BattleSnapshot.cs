using IronNestFCS.Domain.Geometry;

namespace IronNestFCS.Domain.Observation;

public enum ObservationStatus { Valid, Invalid }

public enum BarrelId { Left, Right }

public enum BarrelMechanicalState { Empty, ShellLoaded, Transitioning, Committed, Recovering, Unknown }

public enum ShellKind { AP, DRIL, LE, HE, HCHE, STAR }

public sealed record BarrelSnapshot(
    BarrelId Id, long StateVersion, BarrelMechanicalState State,
    ShellKind? ChamberShell, int? ChamberCharges, bool? CanFire,
    bool? PendingReload, bool? BreechLocked, bool? SafetyReleased,
    bool HasActiveTask, string? ActiveLeaseId)
{
    public bool HasCompatibleShell(ShellKind required) => ChamberShell == required;
}

public sealed record TargetSnapshot(
    string EntityId, string Name, WorldPoint Position, float BearingDeg,
    float DistanceKm, int Priority, int Stars, bool IsArmored,
    bool IsUnderground, bool IsFdc, bool IsMoving, bool VelocityKnown,
    WorldPoint VelocityWorldPerSecond, IReadOnlyList<ShellKind> ImmuneShells,
    bool IsEntityReserved, bool IsPositionReserved)
{
    public TargetSnapshot(
        string entityid, string name, WorldPoint position, float bearingdeg,
        float distancekm, int priority, int stars, bool isarmored,
        bool isunderground, bool isfdc, bool ismoving, bool velocityknown,
        WorldPoint velocityworldpersecond, IEnumerable<ShellKind> immuneshells,
        bool isentityreserved, bool ispositionreserved)
        : this(entityid, name, position, bearingdeg, distancekm, priority, stars,
            isarmored, isunderground, isfdc, ismoving, velocityknown,
            velocityworldpersecond, Array.AsReadOnly(immuneshells.ToArray()),
            isentityreserved, ispositionreserved)
    {
    }
}

public sealed record BattleSnapshot(
    long Version, float GameTime, ObservationStatus Status, string? InvalidReason,
    float TurretBearingDeg, MapGeometry Geometry,
    BarrelSnapshot LeftBarrel, BarrelSnapshot RightBarrel,
    IReadOnlyList<TargetSnapshot> Targets, IReadOnlyList<WorldPoint> Friendlies,
    int PendingManualTaskCount, bool CbtEnabled)
{
    public BattleSnapshot(
        long version, float gametime, ObservationStatus status, string? invalidreason,
        float turretbearingdeg, MapGeometry geometry,
        BarrelSnapshot leftbarrel, BarrelSnapshot rightbarrel,
        IEnumerable<TargetSnapshot> targets, IEnumerable<WorldPoint> friendlies,
        int pendingmanualtaskcount, bool cbtenabled)
        : this(version, gametime, status, invalidreason, turretbearingdeg, geometry,
            leftbarrel, rightbarrel, Array.AsReadOnly(targets.ToArray()),
            Array.AsReadOnly(friendlies.ToArray()), pendingmanualtaskcount, cbtenabled)
    {
    }

    public bool IsValid => Status == ObservationStatus.Valid;
}
