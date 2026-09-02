namespace IronNestFCS.Abstractions.Brain;

/// <summary>
/// Deterministic in-process bring-up policy. It is intentionally weaker than
/// a planner: it only proves that an observation can cross the same mask and
/// action boundary used by the external pipe client.
/// </summary>
public sealed class InProcessScriptedPolicy : IBrainPolicy
{
    public BrainAction Choose(BrainObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var trackId = observation.ActionMask.EngageableTrackIds
            .OrderBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();
        return trackId == null
            ? new BrainAction(BrainActionKind.Hold)
            : new BrainAction(BrainActionKind.Engage, trackId);
    }
}
