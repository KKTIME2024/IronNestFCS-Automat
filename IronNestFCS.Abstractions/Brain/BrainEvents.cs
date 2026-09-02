namespace IronNestFCS.Abstractions.Brain;

/// <summary>游戏侧事件类型；事件链只使用公开的 Brain decision/mission IDs。</summary>
public enum BrainEventType
{
    BrainDecision,
    MissionCreated,
    MissionAssigned,
    GunCommitted,
    ShotFired,
    ShellImpact,
    TargetDamaged,
    TargetDestroyed,
    MissionCompleted,
    MissionFailed,
    BrainDecisionSuperseded,
    ShotMissed,
    ImmuneHit,
    AmmoConsumed,
    IronNestDamaged,
}

/// <summary>
/// Typed event used by the pure reward ledger. Amount is interpreted by event
/// type (damage, reward, cost, or penalty); it is never read from an enemy
/// object by the ledger.
/// </summary>
public sealed record BrainGameEvent(
    BrainEventType Type,
    string EpisodeId,
    double GameTimeSeconds,
    string? DecisionId,
    string? MissionId,
    string? RoundId,
    double Amount = 0.0,
    string? PublicTrackId = null);

/// <summary>把延迟到达的命中/摧毁/任务结果绑定回最初 decision。</summary>
public sealed class BrainRewardAttributionLedger
{
    private readonly Dictionary<string, RewardBreakdown> rewardsByDecision = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> missionToDecision = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> roundToDecision = new(StringComparer.Ordinal);
    private string? activeEpisodeId;

    public string? ActiveEpisodeId => activeEpisodeId;

    public void StartEpisode(string episodeId)
    {
        if (string.IsNullOrWhiteSpace(episodeId))
            throw new ArgumentException("episode id is required", nameof(episodeId));

        activeEpisodeId = episodeId;
        rewardsByDecision.Clear();
        missionToDecision.Clear();
        roundToDecision.Clear();
    }

    public bool Apply(BrainGameEvent gameEvent)
    {
        ArgumentNullException.ThrowIfNull(gameEvent);
        if (activeEpisodeId == null
            || !string.Equals(activeEpisodeId, gameEvent.EpisodeId, StringComparison.Ordinal))
            return false;

        if (gameEvent.Type == BrainEventType.BrainDecision)
        {
            if (string.IsNullOrWhiteSpace(gameEvent.DecisionId))
                return false;
            rewardsByDecision.TryAdd(gameEvent.DecisionId, new RewardBreakdown());
            Bind(gameEvent);
            return true;
        }

        var decisionId = ResolveDecision(gameEvent);
        if (decisionId == null || !rewardsByDecision.ContainsKey(decisionId))
            return false;

        Bind(gameEvent, decisionId);
        rewardsByDecision[decisionId] = AddReward(rewardsByDecision[decisionId], gameEvent);
        return true;
    }

    public RewardBreakdown GetReward(string decisionId)
    {
        return decisionId != null && rewardsByDecision.TryGetValue(decisionId, out var reward)
            ? reward
            : new RewardBreakdown();
    }

    public void Clear()
    {
        activeEpisodeId = null;
        rewardsByDecision.Clear();
        missionToDecision.Clear();
        roundToDecision.Clear();
    }

    private string? ResolveDecision(BrainGameEvent gameEvent)
    {
        if (!string.IsNullOrWhiteSpace(gameEvent.DecisionId)
            && rewardsByDecision.ContainsKey(gameEvent.DecisionId))
            return gameEvent.DecisionId;
        if (!string.IsNullOrWhiteSpace(gameEvent.MissionId)
            && missionToDecision.TryGetValue(gameEvent.MissionId, out var missionDecision))
            return missionDecision;
        if (!string.IsNullOrWhiteSpace(gameEvent.RoundId)
            && roundToDecision.TryGetValue(gameEvent.RoundId, out var roundDecision))
            return roundDecision;
        return null;
    }

    private void Bind(BrainGameEvent gameEvent, string? decisionId = null)
    {
        var resolved = decisionId ?? gameEvent.DecisionId;
        if (resolved == null)
            return;
        if (!string.IsNullOrWhiteSpace(gameEvent.MissionId))
            missionToDecision[gameEvent.MissionId] = resolved;
        if (!string.IsNullOrWhiteSpace(gameEvent.RoundId))
            roundToDecision[gameEvent.RoundId] = resolved;
    }

    private static RewardBreakdown AddReward(RewardBreakdown current, BrainGameEvent gameEvent)
    {
        var amount = gameEvent.Amount;
        return gameEvent.Type switch
        {
            BrainEventType.TargetDamaged => current with { DamageReward = current.DamageReward + amount },
            BrainEventType.TargetDestroyed => current with { TargetDestroyedReward = current.TargetDestroyedReward + amount },
            BrainEventType.MissionCompleted => current with { MissionSuccessReward = current.MissionSuccessReward + amount },
            BrainEventType.MissionFailed => current with { MissionFailedPenalty = current.MissionFailedPenalty + Math.Abs(amount) },
            BrainEventType.ShotMissed => current with { MissPenalty = current.MissPenalty + Math.Abs(amount) },
            BrainEventType.ImmuneHit => current with { ImmuneHitPenalty = current.ImmuneHitPenalty + Math.Abs(amount) },
            BrainEventType.AmmoConsumed => current with { AmmoCostPenalty = current.AmmoCostPenalty + Math.Abs(amount) },
            BrainEventType.IronNestDamaged => current with { IronNestDamagePenalty = current.IronNestDamagePenalty + Math.Abs(amount) },
            _ => current,
        };
    }
}
