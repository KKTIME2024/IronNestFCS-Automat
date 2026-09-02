namespace IronNestFCS.Abstractions.Brain;

/// <summary>Brain 能提交的最小高层动作集合；底层操炮动作不在此枚举中。</summary>
public enum BrainActionKind
{
    Hold,
    Engage,
    ContinueCurrentMission,
    AbortIfSafe,
}

/// <summary>纯逻辑 episode 状态；不暴露 Unity 场景或组件实例。</summary>
public enum EpisodeState
{
    Inactive,
    Starting,
    WaitingForInitialObservation,
    Running,
    Terminal,
    Resetting,
    Failed,
}

/// <summary>跨 decision/mission/round 的机器可读事件边界。</summary>
public sealed record BrainEvent(
    string Type,
    double GameTimeSeconds,
    string? DecisionId,
    string? MissionId,
    string? RoundId,
    IReadOnlyDictionary<string, string>? Data = null);

public sealed record BrainAction(BrainActionKind Kind, string? TrackId = null);

/// <summary>
/// 当前决策点的合法动作掩码。非法动作在提交前拒绝，而不是交给 reward 惩罚学习。
/// </summary>
public sealed class ActionMask
{
    private readonly HashSet<string> engageableTrackIds;

    public ActionMask(
        bool holdAllowed,
        IEnumerable<string>? engageableTrackIds,
        bool continueCurrentMissionAllowed,
        bool abortIfSafeAllowed)
    {
        HoldAllowed = holdAllowed;
        this.engageableTrackIds = new HashSet<string>(
            engageableTrackIds ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        ContinueCurrentMissionAllowed = continueCurrentMissionAllowed;
        AbortIfSafeAllowed = abortIfSafeAllowed;
    }

    public bool HoldAllowed { get; }
    public bool ContinueCurrentMissionAllowed { get; }
    public bool AbortIfSafeAllowed { get; }
    public IReadOnlySet<string> EngageableTrackIds => engageableTrackIds;

    public bool Allows(BrainAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Kind switch
        {
            BrainActionKind.Hold => HoldAllowed && action.TrackId == null,
            BrainActionKind.Engage => action.TrackId != null && engageableTrackIds.Contains(action.TrackId),
            BrainActionKind.ContinueCurrentMission => ContinueCurrentMissionAllowed && action.TrackId == null,
            BrainActionKind.AbortIfSafe => AbortIfSafeAllowed && action.TrackId == null,
            _ => false,
        };
    }
}

/// <summary>Brain 在一个事件决策点看到的快照。</summary>
public sealed record BrainObservation
{
    public BrainObservation(
        double gameTimeSeconds,
        IReadOnlyList<TrackSnapshot> tracks,
        ActionMask actionMask)
    {
        GameTimeSeconds = gameTimeSeconds;
        Tracks = tracks?.ToArray() ?? throw new ArgumentNullException(nameof(tracks));
        ActionMask = actionMask ?? throw new ArgumentNullException(nameof(actionMask));
    }

    public double GameTimeSeconds { get; }
    public IReadOnlyList<TrackSnapshot> Tracks { get; }
    public ActionMask ActionMask { get; }
}

/// <summary>原生战果转成的 reward 组成项；busy-work 不在这里产生正奖励。</summary>
public sealed record RewardBreakdown(
    double DamageReward = 0.0,
    double TargetDestroyedReward = 0.0,
    double MissionSuccessReward = 0.0,
    double MissionFailedPenalty = 0.0,
    double IronNestDamagePenalty = 0.0,
    double AmmoCostPenalty = 0.0,
    double ElapsedTimePenalty = 0.0,
    double MissPenalty = 0.0,
    double ImmuneHitPenalty = 0.0)
{
    public double Total => DamageReward
        + TargetDestroyedReward
        + MissionSuccessReward
        - MissionFailedPenalty
        - IronNestDamagePenalty
        - AmmoCostPenalty
        - ElapsedTimePenalty
        - MissPenalty
        - ImmuneHitPenalty;
}

public sealed record BrainExecutionResult(bool Accepted, string Reason = "");

/// <summary>一条可审计的决策转移；后续 IPC 只需序列化这个边界。</summary>
public sealed record BrainTransition(
    BrainObservation Observation,
    BrainAction Action,
    BrainExecutionResult Execution,
    IReadOnlyList<string> GameEvents,
    RewardBreakdown Reward,
    BrainObservation? NextObservation,
    bool Terminal);

/// <summary>
/// Logic 层后续实现的窄 facade。接口不泄漏炮塔、IL2CPP 或 FCS 私有类型。
/// </summary>
public interface IBrainFacade
{
    BrainObservation GetObservation();
    BrainExecutionResult SubmitDecision(BrainAction action);
    IReadOnlyList<BrainEvent> DrainEvents();
    BrainTransition? LastTransition { get; }
    IReadOnlyList<BrainTransition> TransitionHistory { get; }
    bool IsTerminal { get; }
    EpisodeState EpisodeState { get; }
    void ResetEpisode();
}
