using Il2Cpp;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.Brain;

/// <summary>
/// Main-thread, polling-only adapter for the game's native mission outcome
/// counters. The game updates these counters from native state-graph code, so
/// a Harmony patch on the generated C# wrapper is not a reliable event source.
/// This probe reads only public MissionStatsTracker mission statistics and the
/// mission-ended flag; it never enumerates or identifies enemy entities.
/// </summary>
public sealed class MissionStatsRuntimeProbe : IDisposable
{
    private MissionStatsTracker? tracker;
    private BrainGameStatsSnapshot? lastSnapshot;
    private float lastFindTime;
    private float lastSampleTime;

    public MissionStatsRuntimeProbe(Action<BrainStatsEvent> onEvent)
    {
        OnEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
    }

    public Action<BrainStatsEvent> OnEvent { get; private set; }

    /// <summary>Opt-in switch; reward sampling does not alter ordinary FCS.</summary>
    public static bool Enabled =>
        BrainExperimentFlags.IsEnabled(
            "IRONNEST_BRAIN_REWARD",
            "brain-reward.enabled");

    /// <summary>Call from FcsModule.Update on Unity's main thread.</summary>
    public void Update()
    {
        if (tracker == null && Time.time - lastFindTime >= 1f)
        {
            lastFindTime = Time.time;
            try
            {
                tracker = Object.FindObjectOfType<MissionStatsTracker>();
                if (tracker != null)
                {
                    lastSnapshot = null;
                    MelonLogger.Msg("[Brain] MissionStatsTracker bound for native outcome sampling");
                }
            }
            catch (Exception ex)
            {
                tracker = null;
                MelonLogger.Warning($"[Brain] MissionStatsTracker lookup failed: {ex.Message}");
            }
        }

        if (tracker == null || Time.time - lastSampleTime < 0.1f)
            return;
        lastSampleTime = Time.time;

        BrainGameStatsSnapshot current;
        try
        {
            var mission = tracker.mission;
            current = new BrainGameStatsSnapshot(
                mission.shotsFired,
                mission.targetsDestroyed,
                mission.hitsOnTargets,
                mission.missedShots,
                mission.missionTime,
                tracker.RequisitionPoints,
                tracker.missionEnded);
        }
        catch (Exception ex)
        {
            // Scene teardown can invalidate an IL2CPP reference between two
            // frames. Rebind on the next lookup tick instead of leaking it.
            MelonLogger.Warning($"[Brain] MissionStatsTracker sample failed: {ex.Message}");
            tracker = null;
            lastSnapshot = null;
            return;
        }

        if (lastSnapshot == null)
        {
            lastSnapshot = current;
            OnEvent(new BrainStatsEvent("stats-bound", current, BrainGameStatsSnapshot.Zero));
            return;
        }

        var previous = lastSnapshot.Value;
        var delta = current.DeltaFrom(previous);
        if (delta.IsZero && current.MissionEnded == previous.MissionEnded)
            return;

        var kind = current.MissionEnded && !previous.MissionEnded
            ? "mission-ended"
            : current.IsResetComparedTo(previous)
                ? "mission-reset"
                : "stats-changed";
        lastSnapshot = current;
        OnEvent(new BrainStatsEvent(kind, current, delta));
    }

    public void Dispose()
    {
        tracker = null;
        lastSnapshot = null;
        OnEvent = static _ => { };
    }
}

/// <summary>Only native counters used for reward/terminal audit; no target IDs.</summary>
public readonly record struct BrainGameStatsSnapshot(
    int ShotsFired,
    int TargetsDestroyed,
    int HitsOnTargets,
    int MissedShots,
    float MissionTime,
    int RequisitionPoints,
    bool MissionEnded)
{
    public static BrainGameStatsSnapshot Zero => new(0, 0, 0, 0, 0f, 0, false);

    public BrainGameStatsSnapshot DeltaFrom(BrainGameStatsSnapshot previous) => new(
        ShotsFired - previous.ShotsFired,
        TargetsDestroyed - previous.TargetsDestroyed,
        HitsOnTargets - previous.HitsOnTargets,
        MissedShots - previous.MissedShots,
        MissionTime - previous.MissionTime,
        RequisitionPoints - previous.RequisitionPoints,
        MissionEnded);

    public bool IsZero => ShotsFired == 0
        && TargetsDestroyed == 0
        && HitsOnTargets == 0
        && MissedShots == 0
        && Math.Abs(MissionTime) < 0.0001f
        && RequisitionPoints == 0;

    public bool IsResetComparedTo(BrainGameStatsSnapshot previous) =>
        ShotsFired < previous.ShotsFired
        || TargetsDestroyed < previous.TargetsDestroyed
        || HitsOnTargets < previous.HitsOnTargets
        || MissedShots < previous.MissedShots
        || MissionTime + 0.1f < previous.MissionTime;
}

public readonly record struct BrainStatsEvent(
    string Kind,
    BrainGameStatsSnapshot Current,
    BrainGameStatsSnapshot Delta);
