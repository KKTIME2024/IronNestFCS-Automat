namespace IronNestFCS.Abstractions.Brain;

public enum BrainResetPhase
{
    Idle,
    WaitingForTeardown,
    WaitingForNewMission,
    Succeeded,
    Failed,
}

public sealed record BrainResetAuditEvent(
    string Type,
    string EpisodeId,
    double WallTimeSeconds,
    string? Detail = null);

/// <summary>
/// Main-thread-friendly reset coordinator. Runtime adapters provide the
/// MissionManager reload call and readiness predicates; this class only owns
/// ordering, watchdogs, episode IDs, and audit events.
/// </summary>
public sealed class BrainResetHarness
{
    private readonly double timeoutSeconds;
    private readonly Action requestReload;
    private readonly Action rebind;
    private readonly Action clearKnowledge;
    private double resetStartWallTime;
    private int resetCount;

    public BrainResetHarness(
        string initialEpisodeId,
        double timeoutSeconds,
        Action requestReload,
        Action rebind,
        Action clearKnowledge)
    {
        if (string.IsNullOrWhiteSpace(initialEpisodeId))
            throw new ArgumentException("episode id is required", nameof(initialEpisodeId));
        if (timeoutSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

        CurrentEpisodeId = initialEpisodeId;
        this.timeoutSeconds = timeoutSeconds;
        this.requestReload = requestReload ?? throw new ArgumentNullException(nameof(requestReload));
        this.rebind = rebind ?? throw new ArgumentNullException(nameof(rebind));
        this.clearKnowledge = clearKnowledge ?? throw new ArgumentNullException(nameof(clearKnowledge));
        Phase = BrainResetPhase.Idle;
    }

    public BrainResetPhase Phase { get; private set; }
    public string CurrentEpisodeId { get; private set; }
    public double ResetLatencySeconds { get; private set; }
    public IReadOnlyList<BrainResetAuditEvent> AuditEvents => auditEvents;
    private readonly List<BrainResetAuditEvent> auditEvents = new();

    public bool RequestReset(double wallTimeSeconds)
    {
        if (Phase is not (BrainResetPhase.Idle or BrainResetPhase.Succeeded))
            return false;

        resetStartWallTime = wallTimeSeconds;
        resetCount++;
        ResetLatencySeconds = 0.0;
        auditEvents.Clear();
        auditEvents.Add(new BrainResetAuditEvent("RESET_START", CurrentEpisodeId, wallTimeSeconds));
        Phase = BrainResetPhase.WaitingForTeardown;
        try
        {
            requestReload();
            return true;
        }
        catch (Exception exception)
        {
            Fail(wallTimeSeconds, $"reload failed: {exception.GetType().Name}");
            return false;
        }
    }

    public void Tick(double wallTimeSeconds, bool oldObjectsGone, bool newMissionDetected)
    {
        if (Phase is BrainResetPhase.Idle or BrainResetPhase.Succeeded or BrainResetPhase.Failed)
            return;
        if (wallTimeSeconds - resetStartWallTime > timeoutSeconds)
        {
            Fail(wallTimeSeconds, "watchdog timeout");
            return;
        }

        if (Phase == BrainResetPhase.WaitingForTeardown && oldObjectsGone)
        {
            auditEvents.Add(new BrainResetAuditEvent("OBJECT_TEARDOWN", CurrentEpisodeId, wallTimeSeconds));
            Phase = BrainResetPhase.WaitingForNewMission;
            return;
        }

        if (Phase != BrainResetPhase.WaitingForNewMission || !newMissionDetected)
            return;

        auditEvents.Add(new BrainResetAuditEvent("NEW_MISSION_DETECTED", CurrentEpisodeId, wallTimeSeconds));
        try
        {
            rebind();
            auditEvents.Add(new BrainResetAuditEvent("FSC_REBOUND", CurrentEpisodeId, wallTimeSeconds));
            clearKnowledge();
            var oldEpisodeId = CurrentEpisodeId;
            CurrentEpisodeId = $"{oldEpisodeId}:reset:{resetCount}";
            auditEvents.Add(new BrainResetAuditEvent("KNOWLEDGE_CLEARED", CurrentEpisodeId, wallTimeSeconds));
            auditEvents.Add(new BrainResetAuditEvent("FIRST_OBSERVATION", CurrentEpisodeId, wallTimeSeconds));
            ResetLatencySeconds = wallTimeSeconds - resetStartWallTime;
            auditEvents.Add(new BrainResetAuditEvent("RESET_SUCCESS", CurrentEpisodeId, wallTimeSeconds));
            Phase = BrainResetPhase.Succeeded;
        }
        catch (Exception exception)
        {
            Fail(wallTimeSeconds, $"rebind failed: {exception.GetType().Name}");
        }
    }

    private void Fail(double wallTimeSeconds, string detail)
    {
        ResetLatencySeconds = wallTimeSeconds - resetStartWallTime;
        auditEvents.Add(new BrainResetAuditEvent("RESET_FAILED", CurrentEpisodeId, wallTimeSeconds, detail));
        Phase = BrainResetPhase.Failed;
    }
}
