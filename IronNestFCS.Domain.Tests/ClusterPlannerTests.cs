using IronNestFCS.Domain.Geometry;
using IronNestFCS.Domain.Observation;
using IronNestFCS.Domain.Planning;

namespace IronNestFCS.Domain.Tests;

internal static class ClusterPlannerTests
{
    public static void TwoNearbySoftTargetsBecomeOneHeGroup()
    {
        var leader = SnapshotBuilder.Target("a", position: new(0, 0, 0));
        var neighbor = SnapshotBuilder.Target("b", position: new(0.05f, 0, 0));
        var plan = new BaselinePlanner().Plan(SnapshotBuilder.Valid(targets: new[] { leader, neighbor }));
        var shot = plan.Groups[0].Shots[0];
        AssertEx.Equal(ShellKind.HE, shot.Shell);
        AssertEx.Equal("", shot.TargetEntityId);
        AssertEx.True(MathF.Abs(shot.ImpactPoint.X - 0.025f) < 0.0001f);
    }

    public static void FourSpreadTargetsChooseHche()
    {
        var targets = new[] {
            SnapshotBuilder.Target("a", position: new(0, 0, 0)),
            SnapshotBuilder.Target("b", position: new(0.10f, 0, 0)),
            SnapshotBuilder.Target("c", position: new(-0.10f, 0, 0)),
            SnapshotBuilder.Target("d", position: new(0, 0.10f, 0))
        };
        var shot = FirstShot(targets);
        AssertEx.Equal(ShellKind.HCHE, shot.Shell);
    }

    public static void HcheRejectedWhenRatioDoesNotBeatHe()
    {
        var targets = new[] {
            SnapshotBuilder.Target("a", position: new(0, 0, 0)),
            SnapshotBuilder.Target("b", position: new(0.04f, 0, 0)),
            SnapshotBuilder.Target("c", position: new(-0.04f, 0, 0)),
            SnapshotBuilder.Target("d", position: new(0, 0.04f, 0)),
            SnapshotBuilder.Target("e", position: new(0, -0.04f, 0))
        };
        var shot = FirstShot(targets);
        AssertEx.Equal(ShellKind.HE, shot.Shell);
    }

    public static void FriendlyInsideSafeRadiusRejectsImpact()
    {
        var leader = SnapshotBuilder.Target("a", position: new(0, 0, 0));
        var neighbor = SnapshotBuilder.Target("b", position: new(0.05f, 0, 0));
        var friendly = new WorldPoint(0.025f, 0, 0);
        var snapshot = SnapshotBuilder.Valid(targets: new[] { leader, neighbor });
        var withFriendly = snapshot with { Friendlies = new[] { friendly } };
        var shot = new BaselinePlanner().Plan(withFriendly).Groups[0].Shots[0];
        AssertEx.Equal(ShellKind.DRIL, shot.Shell);
        AssertEx.Equal("a", shot.TargetEntityId);
    }

    public static void ArmoredAndUndergroundNeverEnterClusters()
    {
        var leader = SnapshotBuilder.Target("a", position: new(0, 0, 0));
        var armored = SnapshotBuilder.Target("armor", position: new(0.05f, 0, 0), armored: true);
        var underground = SnapshotBuilder.Target("under", position: new(-0.05f, 0, 0), underground: true);
        var shot = FirstShot(new[] { leader, armored, underground });
        AssertEx.Equal(ShellKind.DRIL, shot.Shell);
        AssertEx.Equal("a", shot.TargetEntityId);
    }

    public static void DifferentMotionFlagsNeverShareMovingCluster()
    {
        var moving = SnapshotBuilder.Target("mover", position: new(0, 0, 0), moving: true);
        var staticTarget = SnapshotBuilder.Target("static", position: new(0.05f, 0, 0));
        var shot = FirstShot(new[] { moving, staticTarget });
        AssertEx.Equal(ShellKind.DRIL, shot.Shell);
        AssertEx.Equal("mover", shot.TargetEntityId);
    }

    public static void VelocityDifferenceAboveThresholdSplitsCluster()
    {
        var a = SnapshotBuilder.Target("a", position: new(0, 0, 0), moving: true) with
            { VelocityWorldPerSecond = new WorldPoint(0, 0.001f, 0) };
        var b = SnapshotBuilder.Target("b", position: new(0.05f, 0, 0), moving: true) with
            { VelocityWorldPerSecond = new WorldPoint(0, 0.003f, 0) };
        var shot = FirstShot(new[] { a, b });
        AssertEx.Equal(ShellKind.DRIL, shot.Shell);
        AssertEx.Equal("a", shot.TargetEntityId);
    }

    public static void FirstClusterBlastReservesCoveredStaticMember()
    {
        var leader = SnapshotBuilder.Target("a", position: new(0, 0, 0));
        var covered = SnapshotBuilder.Target("b", position: new(0.05f, 0, 0));
        var far = SnapshotBuilder.Target("c", position: new(10, 0, 0));
        var plan = new BaselinePlanner().Plan(SnapshotBuilder.Valid(targets: new[] { leader, covered, far }));
        AssertEx.Equal(2, plan.Groups.Count);
        AssertEx.Equal(ShellKind.HE, plan.Groups[0].Shots[0].Shell);
        AssertEx.Equal("c", plan.Groups[1].Shots[0].TargetEntityId);
    }

    public static void MovingClusterRecordsMatchingCoveredEntities()
    {
        var velocity = new WorldPoint(0, 0.001f, 0);
        var leader = SnapshotBuilder.Target("a", position: new(0, 0, 0), moving: true) with { VelocityWorldPerSecond = velocity };
        var member = SnapshotBuilder.Target("b", position: new(0.05f, 0, 0), moving: true) with { VelocityWorldPerSecond = velocity };
        var reserved = SnapshotBuilder.Target("r", position: new(-0.02f, 0, 0), moving: true) with
            { VelocityWorldPerSecond = velocity, IsEntityReserved = true };
        var plan = new BaselinePlanner().Plan(SnapshotBuilder.Valid(targets: new[] { leader, member, reserved }));
        var shot = plan.Groups[0].Shots[0];
        AssertEx.Equal(ShellKind.HE, shot.Shell);
        AssertEx.SequenceEqual(new[] { "a", "b", "r" }, shot.ClusterMembers);
        AssertEx.Equal("a", shot.TrackEntityId);
        AssertEx.True(WorldPoint.Distance(shot.ClusterOffset, shot.ImpactPoint - leader.Position) < 0.0001f);
    }

    public static void StaticClusterRecordsUnreservedDiagnosticMembers()
    {
        var leader = SnapshotBuilder.Target("a", position: new(0, 0, 0));
        var member = SnapshotBuilder.Target("b", position: new(0.05f, 0, 0));
        var reserved = SnapshotBuilder.Target("r", position: new(-0.05f, 0, 0)) with { IsEntityReserved = true };
        var plan = new BaselinePlanner().Plan(SnapshotBuilder.Valid(targets: new[] { leader, member, reserved }));
        var shot = plan.Groups[0].Shots[0];
        AssertEx.Equal(ShellKind.HE, shot.Shell);
        AssertEx.SequenceEqual(new[] { "a", "b" }, shot.ClusterMembers);
        AssertEx.Equal(null, shot.TrackEntityId);
    }

    private static ShotIntent FirstShot(IReadOnlyList<TargetSnapshot> targets) =>
        new BaselinePlanner().Plan(SnapshotBuilder.Valid(targets: targets)).Groups[0].Shots[0];
}
