using System.Globalization;
using IronNestFCS.Domain.Diagnostics;
using IronNestFCS.Domain.Geometry;
using IronNestFCS.Domain.Observation;
using IronNestFCS.Domain.Planning;

namespace IronNestFCS.Domain.Tests;

internal static class BaselinePlannerTests
{
    public static void HigherPriorityWinsBeforeMechanicalCost()
    {
        var snapshot = SnapshotBuilder.Valid(targets: new[] {
            SnapshotBuilder.Target("near", priority: 2, distanceKm: 1, bearingDeg: 0),
            SnapshotBuilder.Target("gun", priority: 5, distanceKm: 10, bearingDeg: 180)
        });
        AssertEx.Equal("gun", new BaselinePlanner().Plan(snapshot).Groups[0].Shots[0].TargetEntityId);
    }

    public static void EqualPriorityUsesDistanceAndTurretCost()
    {
        var snapshot = SnapshotBuilder.Valid(turretBearingDeg: 10, targets: new[] {
            SnapshotBuilder.Target("slow", priority: 2, distanceKm: 5, bearingDeg: 170),
            SnapshotBuilder.Target("fast", priority: 2, distanceKm: 3, bearingDeg: 15)
        });
        AssertEx.Equal("fast", new BaselinePlanner().Plan(snapshot).Groups[0].Shots[0].TargetEntityId);
    }

    public static void ShellFallbackMatchesMaster()
    {
        AssertEx.Equal(ShellKind.AP, PlanOne(SnapshotBuilder.Target("armor", armored: true)).Shell);
        AssertEx.Equal(ShellKind.HE, PlanOne(SnapshotBuilder.Target("armor", armored: true, immune: new[] { ShellKind.AP })).Shell);
        AssertEx.Equal(ShellKind.DRIL, PlanOne(SnapshotBuilder.Target("soft")).Shell);
        AssertEx.Equal(ShellKind.LE, PlanOne(SnapshotBuilder.Target("soft", immune: new[] { ShellKind.DRIL })).Shell);
        AssertEx.Equal(ShellKind.HCHE, PlanOne(SnapshotBuilder.Target("soft", immune: new[] { ShellKind.DRIL, ShellKind.LE, ShellKind.HE })).Shell);
    }

    public static void InvalidObservationCreatesNoPlan()
    {
        var plan = new BaselinePlanner().Plan(SnapshotBuilder.Invalid(8, "EntitiesUnavailable"));
        AssertEx.False(plan.HasFire);
        AssertEx.Equal(NoPlanReason.ObservationInvalid, plan.NoPlanReason);
    }

    public static void PendingManualFifoSuppressesAutonomy()
    {
        var plan = new BaselinePlanner().Plan(SnapshotBuilder.Valid(pendingManualTasks: 1, targets: new[] { SnapshotBuilder.Target("a") }));
        AssertEx.Equal(NoPlanReason.ManualQueuePending, plan.NoPlanReason);
    }

    public static void StableBaselineWithCbtDisabledWithholdsFdc()
    {
        var snapshot = SnapshotBuilder.Valid(targets: new[] {
            SnapshotBuilder.Target("fdc", priority: 6, isFdc: true, bearingDeg: 90),
            SnapshotBuilder.Target("gun", priority: 5, bearingDeg: 0)
        });
        AssertEx.Equal("gun", new BaselinePlanner().Plan(snapshot).Groups[0].Shots[0].TargetEntityId);
    }

    public static void BothBarrelsBusyPlansNothing()
    {
        var snapshot = SnapshotBuilder.Valid(
            leftBarrel: SnapshotBuilder.Barrel(BarrelId.Left, hasActiveTask: true),
            rightBarrel: SnapshotBuilder.Barrel(BarrelId.Right, hasActiveTask: true),
            targets: new[] { SnapshotBuilder.Target("a"), SnapshotBuilder.Target("b") });
        var plan = new BaselinePlanner().Plan(snapshot);
        AssertEx.False(plan.HasFire);
        AssertEx.Equal(NoPlanReason.NoFreeBarrel, plan.NoPlanReason);
    }

    public static void OneBarrelBusyPlansOneShot()
    {
        var snapshot = SnapshotBuilder.Valid(
            leftBarrel: SnapshotBuilder.Barrel(BarrelId.Left, hasActiveTask: true),
            targets: new[] { SnapshotBuilder.Target("a"), SnapshotBuilder.Target("b") });
        var plan = new BaselinePlanner().Plan(snapshot);
        AssertEx.Equal(1, plan.Groups.Count);
        AssertEx.Equal(TriggerMode.Independent, plan.Groups[0].TriggerMode);
        AssertEx.Equal(ChargePolicy.Minimum, plan.Groups[0].Shots[0].ChargePolicy);
    }

    public static void ReservedTargetsAreSkipped()
    {
        var reserved = WithReservation(SnapshotBuilder.Target("reserved"));
        var snapshot = SnapshotBuilder.Valid(targets: new[] { reserved, SnapshotBuilder.Target("free") });
        var plan = new BaselinePlanner().Plan(snapshot);
        AssertEx.Equal("free", plan.Groups[0].Shots[0].TargetEntityId);
    }

    public static void MovingSnapshotFieldsArePreserved()
    {
        var velocity = new WorldPoint(0f, 2f, 0f);
        var target = SnapshotBuilder.Target("mover", moving: true);
        var withVelocity = target with { VelocityWorldPerSecond = velocity, VelocityKnown = true };
        var snapshot = SnapshotBuilder.Valid(targets: new[] { withVelocity });
        var shot = new BaselinePlanner().Plan(snapshot).Groups[0].Shots[0];
        AssertEx.True(shot.IsMoving);
        AssertEx.False(shot.VelocityUnknown);
        AssertEx.Equal(velocity, shot.AimVelocity);
        AssertEx.Equal(withVelocity.Position, shot.AimOrigin);
    }

    public static void FingerprintIsInvariantUnderCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var snapshot = SnapshotBuilder.Valid(targets: new[] { SnapshotBuilder.Target("a") });
            var plan = new BaselinePlanner().Plan(snapshot);
            var expected = PlanFingerprint.Format(plan);
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            AssertEx.Equal(expected, PlanFingerprint.Format(plan));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static TargetSnapshot WithReservation(TargetSnapshot target) =>
        target with { IsEntityReserved = true };

    private static ShotIntent PlanOne(TargetSnapshot target) =>
        new BaselinePlanner().Plan(SnapshotBuilder.Valid(targets: new[] { target })).Groups[0].Shots[0];
}
