using System.Collections.Concurrent;
using System.Text.Json;

namespace IronNestFCS.Abstractions.Brain;

public enum BrainRuntimeState
{
    Disabled,
    WaitingForMission,
    Running,
    Terminal,
    Resetting,
    Failed,
}

public sealed record BrainRuntimeAuditEvent(
    string Type,
    string EpisodeId,
    double GameTimeSeconds,
    string? DecisionId = null,
    string? Detail = null);

/// <summary>
/// Main-thread game adapter. Implementations may inspect Unity/IL2CPP, but the
/// coordinator calls them only from its Tick method.
/// </summary>
public interface IBrainRuntimeHost
{
    bool MissionReady { get; }
    void RefreshObservation(double gameTimeSeconds);
    void RecordDecisionResult(string decisionId, BrainAction action, BrainExecutionResult result);
    IReadOnlyList<BrainGameEvent> DrainEvents();
}

/// <summary>Pure coordinator boundary around BrainResetHarness or a fake.</summary>
public interface IBrainResetDriver
{
    BrainResetPhase Phase { get; }
    string EpisodeId { get; }
    /// <summary>
    /// Main-thread reset lifecycle events emitted by the concrete adapter.
    /// The coordinator forwards only newly observed events into its runtime
    /// audit so teardown/rebind ordering is visible in the game log.
    /// </summary>
    IReadOnlyList<BrainResetAuditEvent> AuditEvents { get; }
    bool RequestReset(double wallTimeSeconds);
    void Tick(double wallTimeSeconds);
}

/// <summary>
/// A pipe request waiting for the Unity main-thread coordinator. The pipe
/// thread may wait for a bounded response; a timed-out request is marked
/// cancelled and must never be executed later by the game thread.
/// </summary>
public sealed class BrainPipePendingRequest
{
    private readonly ManualResetEventSlim completed = new(false);
    private BrainPipeResponse? response;
    private int state;

    internal BrainPipePendingRequest(BrainPipeRequest request)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    public BrainPipeRequest Request { get; }
    public bool IsCancelled => Volatile.Read(ref state) == 2;

    internal BrainPipeResponse WaitForResponse()
    {
        var timeoutMs = Request.TimeoutMs <= 0 ? 5000 : Math.Clamp(Request.TimeoutMs, 1, 60000);
        if (!completed.Wait(timeoutMs))
        {
            Interlocked.CompareExchange(ref state, 2, 0);
            return BrainPipeResponse.HoldFallback("runtime policy response timeout");
        }

        return response ?? BrainPipeResponse.HoldFallback("runtime response was empty");
    }

    public bool Complete(BrainPipeResponse nextResponse)
    {
        ArgumentNullException.ThrowIfNull(nextResponse);
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
            return false;

        response = nextResponse;
        completed.Set();
        return true;
    }

    internal void Cancel(string reason)
    {
        if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
        {
            response = BrainPipeResponse.HoldFallback(reason);
            completed.Set();
        }
    }
}

/// <summary>
/// Thread-safe handoff used by NamedPipeBrainServer. It contains no game
/// references; only the FcsModule main thread drains and completes requests.
/// </summary>
public sealed class BrainPipeRequestQueue : IDisposable
{
    private readonly ConcurrentQueue<BrainPipePendingRequest> pending = new();
    private volatile bool stopped;

    public bool IsStopped => stopped;
    public int PendingCount => pending.Count;

    public void Start() => stopped = false;

    /// <summary>Called by the pipe worker, never by Unity gameplay code.</summary>
    public BrainPipeResponse Handle(BrainPipeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (stopped)
            return BrainPipeResponse.HoldFallback("runtime transport is stopped");

        var item = new BrainPipePendingRequest(request);
        pending.Enqueue(item);
        if (stopped)
            item.Cancel("runtime transport stopped before main-thread dispatch");
        return item.WaitForResponse();
    }

    /// <summary>Called only from the main-thread coordinator.</summary>
    public bool TryDequeue(out BrainPipePendingRequest request)
    {
        while (pending.TryDequeue(out var next))
        {
            if (next.IsCancelled)
                continue;
            request = next;
            return true;
        }

        request = null!;
        return false;
    }

    public void Stop()
    {
        stopped = true;
        while (pending.TryDequeue(out var item))
            item.Cancel("runtime transport stopped");
    }

    public void Dispose() => Stop();
}

/// <summary>Snake-case wire payloads without exposing Unity or IL2CPP types.</summary>
public static class BrainWireCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static string EncodeObservation(
        string episodeId,
        long runtimeGeneration,
        BrainDecisionRequest decision,
        EpisodeState episodeState)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var observation = decision.Observation;
        var payload = new Dictionary<string, object?>
        {
            ["episode_id"] = episodeId,
            ["runtime_generation"] = runtimeGeneration,
            ["decision_id"] = decision.DecisionId,
            ["game_time_seconds"] = observation.GameTimeSeconds,
            ["episode_state"] = episodeState.ToString(),
            ["tracks"] = observation.Tracks.Select(EncodeTrack).ToArray(),
            ["valid_actions"] = new Dictionary<string, object?>
            {
                ["hold"] = observation.ActionMask.HoldAllowed,
                ["continue"] = observation.ActionMask.ContinueCurrentMissionAllowed,
                ["abort"] = observation.ActionMask.AbortIfSafeAllowed,
                ["engageable_track_ids"] = observation.ActionMask.EngageableTrackIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            },
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static string EncodeStatus(string state, string episodeId, long runtimeGeneration = 0)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["episode_id"] = episodeId,
            ["runtime_generation"] = runtimeGeneration,
            ["state"] = state,
        }, JsonOptions);
    }

    public static string EncodeTransition(
        BrainTransition? transition,
        BrainRuntimeState state,
        RewardBreakdown reward)
    {
        var payload = new Dictionary<string, object?>
        {
            ["state"] = state.ToString(),
            ["terminal"] = transition?.Terminal ?? state is BrainRuntimeState.Terminal or BrainRuntimeState.Failed,
            ["reward"] = new Dictionary<string, object?>
            {
                ["damage"] = reward.DamageReward,
                ["target_destroyed"] = reward.TargetDestroyedReward,
                ["mission_success"] = reward.MissionSuccessReward,
                ["mission_failed"] = reward.MissionFailedPenalty,
                ["iron_nest_damage"] = reward.IronNestDamagePenalty,
                ["ammo_cost"] = reward.AmmoCostPenalty,
                ["elapsed_time"] = reward.ElapsedTimePenalty,
                ["miss"] = reward.MissPenalty,
                ["immune_hit"] = reward.ImmuneHitPenalty,
                ["total"] = reward.Total,
            },
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static object EncodeTrack(TrackSnapshot track)
    {
        return new Dictionary<string, object?>
        {
            ["track_id"] = track.TrackId,
            ["display_name"] = track.DisplayName,
            ["reported_grid"] = track.ReportedGrid,
            ["reported_position"] = track.ReportedPosition is { } position
                ? new Dictionary<string, double> { ["x_km"] = position.XKm, ["y_km"] = position.YKm }
                : null,
            ["reported_distance_km"] = track.ReportedDistanceKm,
            ["reported_bearing_degrees"] = track.ReportedBearingDegrees,
            ["reported_at_seconds"] = track.ReportedAtSeconds,
            ["motion"] = track.Motion is { } motion
                ? new Dictionary<string, double>
                {
                    ["course_degrees"] = motion.CourseDegrees,
                    ["speed_km_per_second"] = motion.SpeedKmPerSecond,
                }
                : null,
            ["estimated_arrival_seconds"] = track.EstimatedArrivalSeconds,
            ["last_updated_at_seconds"] = track.LastUpdatedAtSeconds,
            ["lifecycle_state"] = track.LifecycleState.ToString(),
            ["destroyed_confirmed"] = track.DestroyedConfirmed,
        };
    }
}
