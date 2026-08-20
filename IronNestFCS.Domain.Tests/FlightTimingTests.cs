using IronNestFCS.Domain.Execution;

namespace IronNestFCS.Domain.Tests;

internal static class FlightTimingTests
{
    public static void ReachesImpactAtEstimatedTime()
    {
        AssertEx.False(FlightTiming.HasReachedImpact(4.99f, 5f));
        AssertEx.True(FlightTiming.HasReachedImpact(5f, 5f));
        AssertEx.True(FlightTiming.HasReachedImpact(5.01f, 5f));
    }
}
