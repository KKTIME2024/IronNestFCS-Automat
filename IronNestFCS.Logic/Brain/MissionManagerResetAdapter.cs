using Il2Cpp;

using IronNestFCS.Abstractions.Brain;

namespace IronNestFCS.Logic.Brain;

/// <summary>
/// Main-thread adapter between the pure reset coordinator and the game's
/// public MissionManager API. It deliberately observes only mission lifecycle
/// signals; it never exposes MissionGraph or any entity data to Brain.
/// </summary>
public sealed class MissionManagerResetAdapter
{
    private readonly BrainResetHarness harness;
    private readonly string? expectedMissionSceneName;
    private MissionManager? managerAtReset;

    public MissionManagerResetAdapter(
        string initialEpisodeId,
        double timeoutSeconds,
        Action rebind,
        Action clearKnowledge,
        string? expectedMissionSceneName = null)
    {
        if (string.IsNullOrWhiteSpace(initialEpisodeId))
            throw new ArgumentException("episode id is required", nameof(initialEpisodeId));

        this.expectedMissionSceneName = string.IsNullOrWhiteSpace(expectedMissionSceneName)
            ? null
            : expectedMissionSceneName;
        harness = new BrainResetHarness(
            initialEpisodeId,
            timeoutSeconds,
            requestReload: RequestReloadOnMainThread,
            rebind,
            clearKnowledge);
    }

    public BrainResetHarness Harness => harness;
    public BrainResetPhase Phase => harness.Phase;
    public string EpisodeId => harness.CurrentEpisodeId;

    /// <summary>
    /// Requests the game's own current-mission reload. Call only from Unity's
    /// main thread; no background thread or async continuation may touch IL2CPP.
    /// </summary>
    public bool RequestReset(double wallTimeSeconds)
    {
        return harness.RequestReset(wallTimeSeconds);
    }

    /// <summary>
    /// Advances reset readiness using MissionManager's public phase/mission
    /// state. The caller supplies wall time so tests and the game can use the
    /// same watchdog without coupling Abstractions to Unity clocks.
    /// </summary>
    public void Tick(double wallTimeSeconds)
    {
        var (oldObjectsGone, newMissionDetected) = ReadReadiness();
        harness.Tick(wallTimeSeconds, oldObjectsGone, newMissionDetected);
    }

    private void RequestReloadOnMainThread()
    {
        managerAtReset = MissionManager.Instance
            ?? throw new InvalidOperationException("MissionManager.Instance is unavailable");
        managerAtReset.ReloadCurrentMission();
    }

    private (bool OldObjectsGone, bool NewMissionDetected) ReadReadiness()
    {
        try
        {
            var manager = MissionManager.Instance;
            if (manager == null)
                return (true, false);

            var missionActive = manager.CurrentPhase == MissionManager.GamePhase.MissionActive;
            var oldObjectsGone = !missionActive;
            var newMissionDetected = missionActive
                && manager.CurrentMission != null
                && (expectedMissionSceneName == null
                    || string.Equals(manager.CurrentMissionSceneName, expectedMissionSceneName, StringComparison.Ordinal));
            return (oldObjectsGone, newMissionDetected);
        }
        catch
        {
            // A scene transition can invalidate IL2CPP wrappers between two
            // reads. Treat an unreadable frame as not-ready; the watchdog will
            // produce a deterministic RESET_FAILED audit event.
            return (false, false);
        }
    }
}
