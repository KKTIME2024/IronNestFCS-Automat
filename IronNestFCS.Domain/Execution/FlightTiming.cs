namespace IronNestFCS.Domain.Execution;

/// <summary>预计飞行时间边界的共享判定。</summary>
public static class FlightTiming
{
    public static bool HasReachedImpact(float elapsed, float estimatedToF) => elapsed >= estimatedToF;
}
