using IronNestFCS.Domain.Execution;
using IronNestFCS.Domain.Observation;
using IronNestFCS.Domain.Planning;

namespace IronNestFCS.Domain.Tests;

internal static class PlanContractTests
{
    public static void StalePlanIsRejectedBeforeAnyGroup()
    {
        var plan = SnapshotBuilder.Plan(version: 4, SnapshotBuilder.IndependentGroup("a"));
        var result = PlanContractValidator.Validate(plan, currentVersion: 5, legacyAdapter: true);
        AssertEx.False(result.Accepted);
        AssertEx.Equal(PlanRejection.StaleSnapshot, result.Rejection);
        AssertEx.Equal(0, result.AcceptedGroupIds.Count);
    }

    public static void SameTriggerIsAtomicAndUnsupportedByLegacyAdapter()
    {
        var group = SnapshotBuilder.SameTriggerGroup("a", "b");
        var result = PlanContractValidator.Validate(SnapshotBuilder.Plan(5, group), 5, legacyAdapter: true);
        AssertEx.False(result.Accepted);
        AssertEx.Equal(PlanRejection.UnsupportedTriggerMode, result.Rejection);
        AssertEx.Equal(0, result.AcceptedGroupIds.Count);
    }

    public static void LegacyAdapterAcceptsTwoIndependentOneShotGroups()
    {
        var plan = SnapshotBuilder.Plan(7, SnapshotBuilder.IndependentGroup("a"), SnapshotBuilder.IndependentGroup("b"));
        var result = PlanContractValidator.Validate(plan, 7, legacyAdapter: true);
        AssertEx.True(result.Accepted);
        AssertEx.Equal(PlanRejection.None, result.Rejection);
        AssertEx.SequenceEqual(new[] { "group-a", "group-b" }, result.AcceptedGroupIds);
    }

    public static void LegacyAdapterRejectsAnyRelation()
    {
        var plan = SnapshotBuilder.Plan(7, SnapshotBuilder.IndependentGroup("a"), SnapshotBuilder.IndependentGroup("b"))
            with { Relations = new[] { new GroupRelation("group-a", "group-b", GroupRelationKind.InOrder) } };
        var result = PlanContractValidator.Validate(plan, 7, legacyAdapter: true);
        AssertEx.False(result.Accepted);
        AssertEx.Equal(PlanRejection.InvalidRelation, result.Rejection);
        AssertEx.Equal(0, result.AcceptedGroupIds.Count);
    }

    public static void LegacyAdapterRejectsIndependentGroupWithTwoShots()
    {
        var group = new FireGroup(
            GroupId: "group-x",
            TriggerMode: TriggerMode.Independent,
            Shots: new[] { SnapshotBuilder.IndependentShot("a"), SnapshotBuilder.IndependentShot("b") },
            AllowIndependentFallback: false);
        var result = PlanContractValidator.Validate(SnapshotBuilder.Plan(7, group), 7, legacyAdapter: true);
        AssertEx.False(result.Accepted);
        AssertEx.Equal(PlanRejection.InvalidIndependentGroup, result.Rejection);
        AssertEx.Equal(0, result.AcceptedGroupIds.Count);
    }

    public static void SingleShotGateAuthorizesOnlyAssignedBarrel()
    {
        var gate = FireGateAuthorization.Independent(BarrelId.Left);
        AssertEx.True(gate.MayReleaseSafety(BarrelId.Left));
        AssertEx.False(gate.MayReleaseSafety(BarrelId.Right));
    }

    public static void SameTriggerGateAuthorizesBothBarrels()
    {
        var gate = FireGateAuthorization.SameTrigger(BarrelId.Left, BarrelId.Right);
        AssertEx.True(gate.MayReleaseSafety(BarrelId.Left));
        AssertEx.True(gate.MayReleaseSafety(BarrelId.Right));
    }
}
