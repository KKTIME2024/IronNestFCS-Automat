using IronNestFCS.Domain.Geometry;
using IronNestFCS.Domain.Observation;
using IronNestFCS.Domain.Planning;

namespace IronNestFCS.Domain.Tests;

internal static class BattleSnapshotTests
{
    public static void InvalidScanIsNotAValidEmptyBattlefield()
    {
        var snapshot = SnapshotBuilder.Invalid(version: 7, reason: "EntitiesUnavailable");
        AssertEx.Equal(ObservationStatus.Invalid, snapshot.Status);
        AssertEx.Equal("EntitiesUnavailable", snapshot.InvalidReason);
        AssertEx.Equal(0, snapshot.Targets.Count);
    }

    public static void UnknownChargeIsNotZero()
    {
        var barrel = SnapshotBuilder.Barrel(charges: null, state: BarrelMechanicalState.ShellLoaded);
        AssertEx.Equal<int?>(null, barrel.ChamberCharges);
        AssertEx.False(barrel.State == BarrelMechanicalState.Empty);
    }

    public static void SnapshotCopiesMutableInputs()
    {
        var targets = new List<TargetSnapshot> { SnapshotBuilder.Target("a") };
        var snapshot = SnapshotBuilder.Valid(targets: targets);
        targets.Clear();
        AssertEx.Equal(1, snapshot.Targets.Count);
    }

    public static void BarrelStatesRemainDistinct()
    {
        AssertEx.Equal(BarrelMechanicalState.Empty, SnapshotBuilder.Barrel(state: BarrelMechanicalState.Empty).State);
        AssertEx.Equal(BarrelMechanicalState.ShellLoaded, SnapshotBuilder.Barrel(state: BarrelMechanicalState.ShellLoaded, shell: ShellKind.HE).State);
        AssertEx.Equal(BarrelMechanicalState.Transitioning, SnapshotBuilder.Barrel(state: BarrelMechanicalState.Transitioning, shell: ShellKind.AP).State);
        AssertEx.Equal(BarrelMechanicalState.Committed, SnapshotBuilder.Barrel(state: BarrelMechanicalState.Committed, shell: ShellKind.DRIL, canFire: true).State);
        AssertEx.Equal(BarrelMechanicalState.Unknown, SnapshotBuilder.Barrel(state: BarrelMechanicalState.Unknown, charges: null, canFire: null).State);
    }

    public static void LoadedShellCompatibilityIsExplicit()
    {
        var barrel = SnapshotBuilder.Barrel(state: BarrelMechanicalState.ShellLoaded, shell: ShellKind.HE);
        AssertEx.True(barrel.HasCompatibleShell(ShellKind.HE));
        AssertEx.False(barrel.HasCompatibleShell(ShellKind.AP));
    }

    public static void IdentityTransformKeepsPointUnchanged()
    {
        var transform = MapGeometry.IdentityTransform;
        var point = new WorldPoint(1.5f, -2f, 3.25f);
        var mapped = transform.TransformPoint(point);
        AssertEx.Equal(point, mapped);
    }

    public static void TranslationTransformShiftsPoint()
    {
        var transform = new MapAffineTransform(1, 0, 0, 10, 0, 1, 0, 20, 0, 0, 1, 30);
        var mapped = transform.TransformPoint(new WorldPoint(1, 2, 3));
        AssertEx.Equal(new WorldPoint(11, 22, 33), mapped);
    }

    public static void BearingNorthEastWestAreCanonical()
    {
        var geometry = new MapGeometry(MapGeometry.IdentityTransform, WorldPoint.Zero);
        AssertEx.Equal(0f, geometry.Locate(new WorldPoint(0, 10, 0)).BearingDeg);
        AssertEx.Equal(90f, geometry.Locate(new WorldPoint(10, 0, 0)).BearingDeg);
        AssertEx.Equal(270f, geometry.Locate(new WorldPoint(-10, 0, 0)).BearingDeg);
    }

    public static void DistanceUsesWorldUnitScale()
    {
        var geometry = new MapGeometry(MapGeometry.IdentityTransform, WorldPoint.Zero);
        var located = geometry.Locate(new WorldPoint(0, 1000, 0));
        AssertEx.True(MathF.Abs(located.DistanceKm - 1000f * 3.8164f) < 0.001f);
    }
}

