using Il2Cpp;
using IronNestFCS.Abstractions.Brain;
using IronNestFCS.Logic.FCS;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.Brain;

/// <summary>
/// Main-thread bridge for the runtime coordinator. It owns the small queue of
/// public game events emitted by the existing FCS/stats probe; it never passes
/// a GameObject, MapEntity, or radar collection across the Brain boundary.
/// </summary>
public sealed class FcsBrainRuntimeHost : IBrainRuntimeHost
{
    private readonly FSC fcs;
    private readonly PlayerParityBrainSession session;
    private readonly PlayerParityActionBridge? actuator;
    private readonly Queue<BrainGameEvent> events = new();
    private string? activeMissionId;
    private string? activeDecisionId;

    public FcsBrainRuntimeHost(
        FSC fcs,
        PlayerParityBrainSession session,
        PlayerParityActionBridge? actuator)
    {
        this.fcs = fcs ?? throw new ArgumentNullException(nameof(fcs));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.actuator = actuator;
    }

    /// <summary>
    /// FCS binding alone can outlive a scene transition, so require the public
    /// MissionManager phase and current mission as well. This keeps a runtime
    /// that was started in the menu in WaitingForMission.
    /// </summary>
    public bool MissionReady
    {
        get
        {
            if (!fcs.IsBound)
                return false;
            try
            {
                return MissionManager.Instance?.CurrentPhase == MissionManager.GamePhase.MissionActive
                    && MissionManager.Instance?.CurrentMission != null;
            }
            catch
            {
                return false;
            }
        }
    }

    public void RefreshObservation(double gameTimeSeconds)
    {
        if (actuator != null)
            actuator.RefreshObservation(gameTimeSeconds);
        else
            session.RefreshEngageableTracks(gameTimeSeconds);
    }

    /// <summary>
    /// Main-thread diagnostic boundary: an accepted Engage has actually been
    /// materialized as an FSC mission (or is already in flight). This does
    /// not expose the task to Brain; it only lets the coordinator mark the
    /// bring-up milestone.
    /// </summary>
    public bool HasActiveFscMission
    {
        get
        {
            if (string.IsNullOrWhiteSpace(activeMissionId))
                return false;

            static bool Matches(ArtilleryTask? task, string missionId)
            {
                return task?.MissionRef != null
                    && string.Equals(task.MissionRef.MissionId, missionId, StringComparison.Ordinal)
                    && task.progress is not (Progress.Finished or Progress.Canceled or Progress.Failed);
            }

            if (Matches(fcs.LeftTask, activeMissionId) || Matches(fcs.RightTask, activeMissionId))
                return true;
            if (fcs.InFlight.Any(task => Matches(task, activeMissionId)))
                return true;
            return fcs.QueueCan.Any(task => Matches(task, activeMissionId));
        }
    }

    public void RecordDecisionResult(string decisionId, BrainAction action, BrainExecutionResult result)
    {
        if (!result.Accepted)
            return;

        // Hold/Continue are control decisions, not new fire missions. Keep
        // the last mission/decision attribution alive so a delayed impact is
        // still credited to the Engage decision that created that mission.
        // Only a newly-created mission replaces the attribution anchor.
        if (result.MissionId != null)
        {
            activeDecisionId = decisionId;
            activeMissionId = result.MissionId;
            events.Enqueue(new BrainGameEvent(
                BrainEventType.MissionCreated,
                session.EpisodeId,
                Time.time,
                decisionId,
                activeMissionId,
                RoundId: null));
        }
    }

    /// <summary>
    /// Converts the existing public counter probe into coarse typed events.
    /// Counter deltas intentionally stay separate from hidden target identity;
    /// no damage amount is invented when the game exposes only hit counts.
    /// </summary>
    public void OnStats(BrainStatsEvent stats)
    {
        var delta = stats.Delta;
        if (delta.ShotsFired > 0)
            Enqueue(BrainEventType.ShotFired, delta.ShotsFired);
        if (delta.HitsOnTargets > 0)
            Enqueue(BrainEventType.ShellImpact, delta.HitsOnTargets);
        if (delta.TargetsDestroyed > 0)
            Enqueue(BrainEventType.TargetDestroyed, delta.TargetsDestroyed);
        if (delta.MissedShots > 0)
            Enqueue(BrainEventType.ShotMissed, delta.MissedShots);

        if (stats.Kind == "mission-ended" && stats.Current.MissionEnded)
        {
            var failed = ReadMissionFailedFlag();
            Enqueue(
                failed ? BrainEventType.MissionFailed : BrainEventType.MissionCompleted,
                Amount: 1.0);
        }
    }

    public IReadOnlyList<BrainGameEvent> DrainEvents()
    {
        var result = events.ToArray();
        events.Clear();
        return result;
    }

    public void ClearRuntimeState()
    {
        events.Clear();
        activeMissionId = null;
        activeDecisionId = null;
    }

    private void Enqueue(BrainEventType type, double Amount)
    {
        events.Enqueue(new BrainGameEvent(
            type,
            session.EpisodeId,
            Time.time,
            activeDecisionId,
            activeMissionId,
            RoundId: null,
            Amount: Amount));
    }

    private static bool ReadMissionFailedFlag()
    {
        try
        {
            return MissionManager.Instance?.CurrentMissionState?.Failed == true;
        }
        catch
        {
            return false;
        }
    }
}
