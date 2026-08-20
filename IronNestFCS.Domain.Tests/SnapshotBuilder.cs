using IronNestFCS.Domain.Geometry;
using IronNestFCS.Domain.Observation;
using IronNestFCS.Domain.Planning;

namespace IronNestFCS.Domain.Tests;

internal static class SnapshotBuilder
{
    public static TargetSnapshot Target(
        string id,
        WorldPoint? position = null,
        int priority = 5,
        float distanceKm = 5f,
        float bearingDeg = 0f,
        bool armored = false,
        bool underground = false,
        bool isFdc = false,
        bool moving = false,
        IReadOnlyList<ShellKind>? immune = null) => new(
            EntityId: id,
            Name: id,
            Position: position ?? PositionFromBearingDistance(bearingDeg, distanceKm),
            BearingDeg: bearingDeg,
            DistanceKm: distanceKm,
            Priority: priority,
            Stars: 3,
            IsArmored: armored,
            IsUnderground: underground,
            IsFdc: isFdc,
            IsMoving: moving,
            VelocityKnown: moving,
            VelocityWorldPerSecond: WorldPoint.Zero,
            ImmuneShells: immune ?? Array.Empty<ShellKind>(),
            IsEntityReserved: false,
            IsPositionReserved: false);

    private static WorldPoint PositionFromBearingDistance(float bearingDeg, float distanceKm)
    {
        var d = distanceKm / MapGeometry.KmPerWorldUnit;
        var rad = bearingDeg * MathF.PI / 180f;
        return new WorldPoint(MathF.Sin(rad) * d, MathF.Cos(rad) * d, 0f);
    }

    public static BarrelSnapshot Barrel(
        BarrelId id = BarrelId.Left,
        BarrelMechanicalState state = BarrelMechanicalState.Empty,
        ShellKind? shell = null,
        int? charges = 0,
        bool? canFire = null,
        bool hasActiveTask = false) => new(
            Id: id,
            StateVersion: 0,
            State: state,
            ChamberShell: shell,
            ChamberCharges: charges,
            CanFire: canFire,
            PendingReload: null,
            BreechLocked: null,
            SafetyReleased: null,
            HasActiveTask: hasActiveTask,
            ActiveLeaseId: null);

    public static BattleSnapshot Valid(
        long version = 1,
        float turretBearingDeg = 0f,
        IEnumerable<TargetSnapshot>? targets = null,
        int pendingManualTasks = 0,
        BarrelSnapshot? leftBarrel = null,
        BarrelSnapshot? rightBarrel = null) => new(
        version, 0f, ObservationStatus.Valid, null, turretBearingDeg,
        new MapGeometry(MapGeometry.IdentityTransform, WorldPoint.Zero),
        leftBarrel ?? Barrel(BarrelId.Left), rightBarrel ?? Barrel(BarrelId.Right),
        targets ?? Array.Empty<TargetSnapshot>(), Array.Empty<WorldPoint>(),
        pendingManualTasks, false);

    public static BattleSnapshot Invalid(long version, string reason) => new(
        version, 0f, ObservationStatus.Invalid, reason, 0f,
        new MapGeometry(MapGeometry.IdentityTransform, WorldPoint.Zero),
        Barrel(BarrelId.Left), Barrel(BarrelId.Right),
        Array.Empty<TargetSnapshot>(), Array.Empty<WorldPoint>(), 0, false);

    public static FireGroup IndependentGroup(string targetId) => new(
        GroupId: $"group-{targetId}",
        TriggerMode: TriggerMode.Independent,
        Shots: new[] { IndependentShot(targetId) },
        AllowIndependentFallback: false);

    public static ShotIntent IndependentShot(string targetId) => new(
        ShotId: $"shot-{targetId}",
        TargetEntityId: targetId,
        DisplayName: targetId,
        ImpactPoint: WorldPoint.Zero,
        BearingDeg: 0f,
        DistanceKm: 5f,
        Shell: ShellKind.DRIL,
        ChargePolicy: ChargePolicy.Minimum,
        IsMoving: false,
        VelocityUnknown: true,
        AimOrigin: WorldPoint.Zero,
        AimVelocity: WorldPoint.Zero,
        AimStartTime: 0f,
        BlastRadiusKm: 0.05f,
        ClusterMembers: Array.Empty<string>(),
        TrackEntityId: null,
        ClusterOffset: WorldPoint.Zero);

    public static FireGroup SameTriggerGroup(string firstTargetId, string secondTargetId) => new(
        GroupId: "group-same-trigger",
        TriggerMode: TriggerMode.SameTrigger,
        Shots: new[] { IndependentShot(firstTargetId), IndependentShot(secondTargetId) },
        AllowIndependentFallback: false);

    public static EngagementPlan Plan(long version, params FireGroup[] groups) => new(
        SnapshotVersion: version,
        Source: PlanSource.Autonomous,
        Groups: groups,
        Relations: Array.Empty<GroupRelation>(),
        NoPlanReason: NoPlanReason.None);
}