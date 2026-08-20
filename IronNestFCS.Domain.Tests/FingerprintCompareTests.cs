using IronNestFCS.Domain.Diagnostics;
using IronNestFCS.Domain.Geometry;
using IronNestFCS.Domain.Observation;
using IronNestFCS.Domain.Planning;

namespace IronNestFCS.Domain.Tests;

internal static class FingerprintCompareTests
{
    private static EngagementPlan PlanWith(ShotIntent shot, string groupId = "group-t1") => new(
        SnapshotVersion: 1,
        Source: PlanSource.Autonomous,
        Groups: new[] { new FireGroup(GroupId: groupId, TriggerMode: TriggerMode.Independent, Shots: new[] { shot }, AllowIndependentFallback: false) },
        Relations: Array.Empty<GroupRelation>(),
        NoPlanReason: NoPlanReason.None);

    public static void IdenticalPlansCompareClean()
    {
        var expected = SnapshotBuilder.Plan(1, SnapshotBuilder.IndependentGroup("t1"));
        var actual = SnapshotBuilder.Plan(1, SnapshotBuilder.IndependentGroup("t1"));
        AssertEx.SequenceEqual(Array.Empty<string>(), PlanFingerprint.Compare(expected, actual));
    }

    public static void TargetDifferenceIsReported()
    {
        var s = SnapshotBuilder.IndependentShot("t1");
        var diff = PlanFingerprint.Compare(PlanWith(s), PlanWith(s with { TargetEntityId = "t2" }));
        AssertEx.SequenceEqual(new[] { "groups[0].shots[0].target:t1!=t2" }, diff);
    }

    public static void GroupOrderDifferenceIsReported()
    {
        var expected = SnapshotBuilder.Plan(1, SnapshotBuilder.IndependentGroup("t1"), SnapshotBuilder.IndependentGroup("t2"));
        var actual = SnapshotBuilder.Plan(1, SnapshotBuilder.IndependentGroup("t2"), SnapshotBuilder.IndependentGroup("t1"));
        var diff = PlanFingerprint.Compare(expected, actual);
        AssertEx.SequenceEqual(new[]
        {
            "groups[0].shots[0].target:t1!=t2",
            "groups[1].shots[0].target:t2!=t1"
        }, diff);
    }

    public static void TriggerModeDifferenceIsReported()
    {
        var g = SnapshotBuilder.IndependentGroup("t1");
        var expected = SnapshotBuilder.Plan(1, g);
        var actual = SnapshotBuilder.Plan(1, g with { TriggerMode = TriggerMode.SameTrigger });
        var diff = PlanFingerprint.Compare(expected, actual);
        AssertEx.SequenceEqual(new[] { "groups[0].triggerMode:Independent!=SameTrigger" }, diff);
    }

    public static void ClusterVsSingleDifferenceIsReported()
    {
        var s = SnapshotBuilder.IndependentShot("t1");
        var diff = PlanFingerprint.Compare(PlanWith(s), PlanWith(s with { ClusterMembers = new[] { "t1", "t2" } }));
        AssertEx.SequenceEqual(new[] { "groups[0].shots[0].members:-!=t1+t2" }, diff);
    }

    public static void MembersOrderDoesNotMatter()
    {
        var s = SnapshotBuilder.IndependentShot("t1");
        var a = PlanWith(s with { ClusterMembers = new[] { "t1", "t2", "t3" } });
        var b = PlanWith(s with { ClusterMembers = new[] { "t3", "t1", "t2" } });
        AssertEx.SequenceEqual(Array.Empty<string>(), PlanFingerprint.Compare(a, b));
    }

    public static void ImpactWithinToleranceIsClean()
    {
        var s = SnapshotBuilder.IndependentShot("t1");
        var near = s with { ImpactPoint = new WorldPoint(0.00009f, 0f, 0f) };
        AssertEx.SequenceEqual(Array.Empty<string>(), PlanFingerprint.Compare(PlanWith(s), PlanWith(near)));
    }

    public static void ImpactDifferenceIsReported()
    {
        var s = SnapshotBuilder.IndependentShot("t1");
        var far = s with { ImpactPoint = new WorldPoint(0.0002f, 0f, 0f) };
        var diff = PlanFingerprint.Compare(PlanWith(s), PlanWith(far));
        AssertEx.SequenceEqual(new[] { "groups[0].shots[0].impact:0.0000,0.0000,0.0000!=0.0002,0.0000,0.0000" }, diff);
    }

    public static void ShellDifferenceIsReported()
    {
        var s = SnapshotBuilder.IndependentShot("t1");
        var diff = PlanFingerprint.Compare(PlanWith(s), PlanWith(s with { Shell = ShellKind.LE }));
        AssertEx.SequenceEqual(new[] { "groups[0].shots[0].shell:DRIL!=LE" }, diff);
    }

    public static void ChargePolicyDifferenceIsReported()
    {
        var s = SnapshotBuilder.IndependentShot("t1");
        var diff = PlanFingerprint.Compare(PlanWith(s), PlanWith(s with { ChargePolicy = ChargePolicy.Maximum }));
        AssertEx.SequenceEqual(new[] { "groups[0].shots[0].chargePolicy:Minimum!=Maximum" }, diff);
    }

    public static void MotionSnapshotDifferencesAreReported()
    {
        var s = SnapshotBuilder.IndependentShot("t1");
        var actual = s with
        {
            IsMoving = true,
            VelocityUnknown = false,
            AimOrigin = new WorldPoint(1f, 0f, 0f),
            AimVelocity = new WorldPoint(0f, 2f, 0f),
            AimStartTime = 3.5f
        };
        var diff = PlanFingerprint.Compare(PlanWith(s), PlanWith(actual));
        AssertEx.SequenceEqual(new[]
        {
            "groups[0].shots[0].isMoving:False!=True",
            "groups[0].shots[0].velocityKnown:False!=True",
            "groups[0].shots[0].aimOrigin:0.0000,0.0000,0.0000!=1.0000,0.0000,0.0000",
            "groups[0].shots[0].aimVelocity:0.0000,0.0000,0.0000!=0.0000,2.0000,0.0000",
            "groups[0].shots[0].aimStartTime:0.0000!=3.5000"
        }, diff);
    }

    public static void AllDifferencesAreReported()
    {
        var s = SnapshotBuilder.IndependentShot("t1");
        var actual = s with { TargetEntityId = "t9", Shell = ShellKind.HE, ChargePolicy = ChargePolicy.Maximum };
        var diff = PlanFingerprint.Compare(PlanWith(s), PlanWith(actual));
        AssertEx.SequenceEqual(new[]
        {
            "groups[0].shots[0].target:t1!=t9",
            "groups[0].shots[0].shell:DRIL!=HE",
            "groups[0].shots[0].chargePolicy:Minimum!=Maximum"
        }, diff);
    }

    public static void GroupCountMismatchIsReported()
    {
        var expected = SnapshotBuilder.Plan(1, SnapshotBuilder.IndependentGroup("t1"), SnapshotBuilder.IndependentGroup("t2"));
        var actual = SnapshotBuilder.Plan(1, SnapshotBuilder.IndependentGroup("t1"));
        var diff = PlanFingerprint.Compare(expected, actual);
        AssertEx.SequenceEqual(new[] { "groups:count:2!=1" }, diff);
    }

    public static void NoPlanReasonDifferenceIsReported()
    {
        var expected = SnapshotBuilder.Plan(1);
        var actual = expected with { NoPlanReason = NoPlanReason.NoFreeBarrel };
        var diff = PlanFingerprint.Compare(expected, actual);
        AssertEx.SequenceEqual(new[] { "noPlanReason:None!=NoFreeBarrel" }, diff);
    }
}
