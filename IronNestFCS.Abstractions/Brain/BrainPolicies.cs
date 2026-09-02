namespace IronNestFCS.Abstractions.Brain;

public interface IBrainPolicy
{
    BrainAction Choose(BrainObservation observation);
}

/// <summary>用于压力测试的随机策略；采样集合严格来自当前 action mask。</summary>
public sealed class RandomLegalPolicy : IBrainPolicy
{
    private readonly Random random;

    public RandomLegalPolicy(Random? random = null)
    {
        this.random = random ?? new Random();
    }

    public BrainAction Choose(BrainObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var actions = new List<BrainAction>();
        if (observation.ActionMask.HoldAllowed)
            actions.Add(new BrainAction(BrainActionKind.Hold));
        actions.AddRange(observation.ActionMask.EngageableTrackIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new BrainAction(BrainActionKind.Engage, id)));
        if (observation.ActionMask.ContinueCurrentMissionAllowed)
            actions.Add(new BrainAction(BrainActionKind.ContinueCurrentMission));
        if (observation.ActionMask.AbortIfSafeAllowed)
            actions.Add(new BrainAction(BrainActionKind.AbortIfSafe));

        // A safe fallback is useful if a malformed runtime mask contains no
        // action. Normal sessions always allow Hold, so this branch is not a
        // hidden action and will be rejected by the facade if misconfigured.
        return actions.Count == 0
            ? new BrainAction(BrainActionKind.Hold)
            : actions[random.Next(actions.Count)];
    }
}

/// <summary>
/// Deterministic baseline: continue an existing mission, otherwise engage the
/// freshest non-lost legal track and finally hold. It is deliberately simple
/// so it can serve as an environment bring-up policy, not as tactical AI.
/// </summary>
public sealed class ScriptedBaselinePolicy : IBrainPolicy
{
    public BrainAction Choose(BrainObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.ActionMask.ContinueCurrentMissionAllowed)
            return new BrainAction(BrainActionKind.ContinueCurrentMission);

        var candidate = observation.Tracks
            .Where(track => observation.ActionMask.Allows(new BrainAction(BrainActionKind.Engage, track.TrackId)))
            .Where(track => track.LifecycleState is not TrackLifecycleState.Lost
                and not TrackLifecycleState.DestroyedConfirmed)
            .OrderBy(track => StateRank(track.LifecycleState))
            .ThenByDescending(track => Freshness(track, observation.GameTimeSeconds))
            .ThenBy(track => track.ReportedDistanceKm ?? double.MaxValue)
            .ThenBy(track => track.TrackId, StringComparer.Ordinal)
            .FirstOrDefault();

        return candidate == null
            ? new BrainAction(BrainActionKind.Hold)
            : new BrainAction(BrainActionKind.Engage, candidate.TrackId);
    }

    private static int StateRank(TrackLifecycleState state)
    {
        return state switch
        {
            TrackLifecycleState.Tracked => 0,
            TrackLifecycleState.Located => 1,
            TrackLifecycleState.Reported => 2,
            TrackLifecycleState.Unknown => 3,
            _ => 4,
        };
    }

    private static double Freshness(TrackSnapshot track, double nowSeconds)
    {
        var age = Math.Max(0.0, nowSeconds - track.LastUpdatedAtSeconds);
        return -age;
    }
}
