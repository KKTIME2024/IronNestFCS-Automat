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
