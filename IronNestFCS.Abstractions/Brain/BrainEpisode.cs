namespace IronNestFCS.Abstractions.Brain;

/// <summary>
/// 纯逻辑 episode 生命周期。Unity 场景重载适配器只需调用这些方法，
/// 并用 EpisodeId 拒绝旧场景 callback。
/// </summary>
public sealed class BrainEpisode
{
    public BrainEpisode(string episodeId, int? seed, double startGameTimeSeconds)
    {
        if (string.IsNullOrWhiteSpace(episodeId))
            throw new ArgumentException("episode id is required", nameof(episodeId));

        EpisodeId = episodeId;
        Seed = seed;
        StartGameTimeSeconds = startGameTimeSeconds;
        StartWallTime = DateTimeOffset.UtcNow;
        State = EpisodeState.Starting;
    }

    public string EpisodeId { get; private set; }
    public int? Seed { get; private set; }
    public double StartGameTimeSeconds { get; private set; }
    public DateTimeOffset StartWallTime { get; private set; }
    public EpisodeState State { get; private set; }
    public int TransitionCount { get; private set; }
    public int DecisionCount { get; private set; }
    public RewardBreakdown RewardTotal { get; private set; } = new();
    public string? TerminalReason { get; private set; }
    public string? FailureReason { get; private set; }
    public bool IsTerminal => State is EpisodeState.Terminal or EpisodeState.Failed;

    public bool MarkInitialObservation()
    {
        if (State is not (EpisodeState.Starting or EpisodeState.WaitingForInitialObservation))
            return false;
        State = EpisodeState.Running;
        return true;
    }

    public bool RegisterDecision()
    {
        if (State != EpisodeState.Running)
            return false;
        DecisionCount++;
        return true;
    }

    public bool RegisterTransition(RewardBreakdown? reward = null)
    {
        if (State != EpisodeState.Running)
            return false;
        TransitionCount++;
        if (reward != null)
            RewardTotal = Add(RewardTotal, reward);
        return true;
    }

    public bool TryTerminate(string reason, bool failed = false)
    {
        if (IsTerminal || State == EpisodeState.Resetting || State == EpisodeState.Inactive)
            return false;
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("terminal reason is required", nameof(reason));

        TerminalReason = reason;
        FailureReason = failed ? reason : null;
        State = failed ? EpisodeState.Failed : EpisodeState.Terminal;
        return true;
    }

    public bool BeginReset()
    {
        if (!IsTerminal)
            return false;
        State = EpisodeState.Resetting;
        return true;
    }

    public void CompleteReset(string newEpisodeId, double startGameTimeSeconds, int? seed = null)
    {
        if (State != EpisodeState.Resetting)
            throw new InvalidOperationException("episode must be Resetting before CompleteReset");
        if (string.IsNullOrWhiteSpace(newEpisodeId))
            throw new ArgumentException("episode id is required", nameof(newEpisodeId));

        EpisodeId = newEpisodeId;
        Seed = seed;
        StartGameTimeSeconds = startGameTimeSeconds;
        StartWallTime = DateTimeOffset.UtcNow;
        TransitionCount = 0;
        DecisionCount = 0;
        RewardTotal = new RewardBreakdown();
        TerminalReason = null;
        FailureReason = null;
        State = EpisodeState.Starting;
    }

    public bool AcceptsCallback(string callbackEpisodeId)
    {
        return State is not (EpisodeState.Inactive or EpisodeState.Resetting)
            && string.Equals(EpisodeId, callbackEpisodeId, StringComparison.Ordinal);
    }

    private static RewardBreakdown Add(RewardBreakdown left, RewardBreakdown right)
    {
        return new RewardBreakdown(
            left.DamageReward + right.DamageReward,
            left.TargetDestroyedReward + right.TargetDestroyedReward,
            left.MissionSuccessReward + right.MissionSuccessReward,
            left.MissionFailedPenalty + right.MissionFailedPenalty,
            left.IronNestDamagePenalty + right.IronNestDamagePenalty,
            left.AmmoCostPenalty + right.AmmoCostPenalty,
            left.ElapsedTimePenalty + right.ElapsedTimePenalty,
            left.MissPenalty + right.MissPenalty,
            left.ImmuneHitPenalty + right.ImmuneHitPenalty);
    }
}
