using IronNestFCS.Abstractions.Brain;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.Brain;

/// <summary>
/// The first guarded actuator from the PlayerParity Brain boundary into the
/// existing FCS. It accepts only an already-masked high-level action and builds
/// a position mission from legal map/telegram facts. It deliberately leaves
/// EntityId empty: the FCS then performs a position submission and cannot use
/// EntityLocator or a hidden MapEntity to resolve the target.
///
/// This class must be called on Unity's main thread. It is intentionally opt-in
/// through <c>IRONNEST_BRAIN_ACTUATOR=1</c>; ordinary FCS and OmniscientDebug
/// flows are unchanged while this boundary is being validated.
/// </summary>
public sealed class PlayerParityActionBridge
{
    private readonly FSC fcs;
    private readonly PlayerParityBrainSession session;
    private readonly PlayerParityMapGridAdapter mapGrid;
    private readonly int mainThreadId;
    private int nextMissionNumber = 1;

    public PlayerParityActionBridge(
        FSC fcs,
        PlayerParityBrainSession session,
        PlayerParityMapGridAdapter mapGrid)
    {
        this.fcs = fcs ?? throw new ArgumentNullException(nameof(fcs));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.mapGrid = mapGrid ?? throw new ArgumentNullException(nameof(mapGrid));
        mainThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>Opt-in switch; no actuator is connected by default.</summary>
    public static bool Enabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("IRONNEST_BRAIN_ACTUATOR"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Rebuilds the action mask using only legal tracks not already represented
    /// by a current/queued/in-flight position mission. This is intentionally a
    /// low-frequency/event-boundary operation, not a per-frame policy call.
    /// </summary>
    public void RefreshObservation(double gameTimeSeconds)
    {
        var reserved = session.Knowledge.Snapshot()
            .Where(x => IsTrackReserved(x.TrackId))
            .Select(x => x.TrackId);
        session.RefreshEngageableTracks(gameTimeSeconds, reserved);
    }

    /// <summary>
    /// Executes one semantic Brain action. No low-level turret, loader, or
    /// trigger method is exposed here; Mission/FSC owns those details.
    /// </summary>
    public BrainExecutionResult Execute(BrainAction action, double gameTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Environment.CurrentManagedThreadId != mainThreadId)
            return Reject("FCS actuator must be called on Unity main thread");

        // BrainFacade normally enforces this first. Repeat the check at the
        // actuator boundary so a future IPC adapter cannot bypass the mask by
        // calling this class directly.
        if (!session.GetValidActions().Allows(action))
            return Reject("action is not legal in the current observation");

        try
        {
            return action.Kind switch
            {
                BrainActionKind.Hold => Accept("hold"),
                BrainActionKind.ContinueCurrentMission =>
                    HasCurrentMission()
                        ? Accept("continue current mission")
                        : Reject("no current mission"),
                BrainActionKind.AbortIfSafe =>
                    Reject("safe abort actuator is not connected yet"),
                BrainActionKind.Engage => ExecuteEngage(action.TrackId!, gameTimeSeconds),
                _ => Reject("unknown Brain action"),
            };
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[Brain] PlayerParity actuator rejected action: {ex}");
            return Reject("actuator exception; mission was not submitted");
        }
    }

    private BrainExecutionResult ExecuteEngage(string trackId, double gameTimeSeconds)
    {
        if (!fcs.IsBound)
            return Reject("FCS is not bound to the current scene");

        if (IsTrackReserved(trackId))
            return Reject($"track '{trackId}' already has a mission");

        if (!session.Knowledge.TryGet(trackId, out var snapshot) || snapshot == null)
            return Reject($"track '{trackId}' is not in PlayerParityKnowledge");

        if (!snapshot.LatestProvenance.IsPlayerParity)
            return Reject("omniscient/debug provenance cannot reach the actuator");

        if (snapshot.ReportedPosition == null)
            return Reject("track has no legal map position");

        // Reuse the legal kinematic model. This is the only movement input:
        // no TacticalRadar velocity or entity transform is consulted.
        var mapPosition = snapshot.ReportedPosition.Value;
        MotionIntel? motion = snapshot.Motion;
        if (session.Knowledge.TryEstimateAt(trackId, gameTimeSeconds, out var estimate)
            && estimate?.EstimatedPosition is { } predicted)
        {
            mapPosition = predicted;
            motion = estimate.Motion;
        }

        if (!mapGrid.TryMapPointToWorld(mapPosition, out var worldPosition))
        {
            // A grid is retained as evidence and provides a conservative
            // fallback when a map-point conversion was not available yet.
            if (!mapGrid.TryResolveWorldPosition(snapshot.ReportedGrid, out worldPosition))
                return Reject("public map geometry is not bound");
        }

        var target = BuildTargetInfo(snapshot, worldPosition, motion);
        var mission = Mission.Single(new MissionRound
        {
            Target = target,
            // Target classification is not present in the first legal
            // telegram schema. DRIL is the conservative soft-target default;
            // FSC's existing availability fallback remains authoritative.
            Shell = TacticalDecider.ChooseShellType(target, float.MaxValue),
            UseMaxCharge = false,
            UseCluster = false,
        });
        mission.MissionId = $"brain:{trackId}:{nextMissionNumber++}";

        fcs.EnqueueMission(mission);
        try
        {
            RefreshObservation(gameTimeSeconds);
        }
        catch (Exception ex)
        {
            // The mission is already accepted; a stale mask can be repaired
            // at the next event boundary without reporting a false rejection.
            MelonLogger.Warning($"[Brain] action mask refresh after Engage failed: {ex.Message}");
        }
        MelonLogger.Msg(
            $"[Brain] PlayerParity Engage accepted track={trackId} " +
            $"mission={mission.MissionId} pos=({mapPosition.XKm:F2},{mapPosition.YKm:F2})km " +
            $"moving={(motion != null)} entityId=<empty>");
        return Accept($"mission {mission.MissionId} queued");
    }

    private TacticalDecider.TargetInfo BuildTargetInfo(
        TrackSnapshot snapshot,
        Vector3 worldPosition,
        MotionIntel? motion)
    {
        var turretLocal = fcs.MapTable.GetTurretLocal();
        var targetLocal = fcs.MapTable.WorldToMapLocal(worldPosition);
        var delta = targetLocal - turretLocal;
        var distance = delta.magnitude * ShellData.KmPerWorldUnit;
        var angle = Vector3.SignedAngle(delta, Vector3.up, Vector3.forward);
        if (angle < 0f) angle += 360f;

        var velocity = motion == null
            ? Vector3.zero
            : CourseToWorldVelocity(motion);
        var moving = motion != null && TargetLeadSolver.IsMoving(velocity);

        return new TacticalDecider.TargetInfo
        {
            Name = snapshot.DisplayName is { Length: > 0 } name ? name : snapshot.TrackId,
            // Empty on purpose: this is a legal map position, not an entity
            // handle. FSC therefore cannot perform a hidden entity lookup.
            EntityId = "",
            Angle = angle,
            Distance = distance,
            Priority = 1,
            Stars = 0,
            IsArmored = false,
            IsUnderground = false,
            IsMoving = moving,
            VelocityKnown = motion != null,
            Velocity = velocity,
            WorldPos = worldPosition,
            ImmuneShells = new HashSet<string>(StringComparer.Ordinal),
        };
    }

    private static Vector3 CourseToWorldVelocity(MotionIntel motion)
    {
        var radians = (float)(motion.CourseDegrees * Math.PI / 180.0);
        var worldSpeed = (float)(motion.SpeedKmPerSecond / ShellData.KmPerWorldUnit);
        return new Vector3(
            Mathf.Sin(radians) * worldSpeed,
            Mathf.Cos(radians) * worldSpeed,
            0f);
    }

    private bool HasCurrentMission()
    {
        return fcs.LeftTask != null
            || fcs.RightTask != null
            || fcs.PendingCount > 0;
    }

    private bool IsTrackReserved(string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return false;
        return IsTaskForTrack(fcs.LeftTask, trackId)
            || IsTaskForTrack(fcs.RightTask, trackId)
            || fcs.QueueCan.Any(task => IsTaskForTrack(task, trackId))
            || fcs.InFlight.Any(task => IsTaskForTrack(task, trackId));
    }

    private static bool IsTaskForTrack(ArtilleryTask? task, string trackId)
    {
        return task != null
            && string.Equals(task.entityName, trackId, StringComparison.Ordinal);
    }

    private static BrainExecutionResult Accept(string reason) =>
        new(true, reason);

    private static BrainExecutionResult Reject(string reason) =>
        new(false, reason);
}
