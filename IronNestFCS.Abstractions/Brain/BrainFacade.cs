namespace IronNestFCS.Abstractions.Brain;

/// <summary>
/// 运行时无关的 Brain 外壳。Logic 层只需把一个高层 action sink 注入进来，
/// 不必向 policy 暴露 FCS、炮塔或 IL2CPP 对象。
/// </summary>
public sealed class BrainFacade : IBrainFacade
{
    private readonly Func<BrainAction, BrainExecutionResult> executeAction;
    private readonly List<BrainTransition> transitionHistory = new();
    private readonly List<BrainEvent> pendingEvents = new();
    private readonly string episodeBaseId;
    private BrainObservation observation;
    private bool terminal;
    private EpisodeState episodeState = EpisodeState.Running;
    private int episodeSequence;
    private long decisionSequence;
    private string episodeId;
    private string? pendingDecisionId;

    public BrainFacade(
        BrainObservation initialObservation,
        Func<BrainAction, BrainExecutionResult> executeAction,
        string? episodeId = null)
    {
        observation = initialObservation ?? throw new ArgumentNullException(nameof(initialObservation));
        this.executeAction = executeAction ?? throw new ArgumentNullException(nameof(executeAction));
        this.episodeBaseId = string.IsNullOrWhiteSpace(episodeId) ? "episode" : episodeId;
        this.episodeId = this.episodeBaseId;
    }

    public BrainObservation GetObservation() => observation;

    public BrainTransition? LastTransition { get; private set; }

    public IReadOnlyList<BrainTransition> TransitionHistory => transitionHistory;

    public bool IsTerminal => terminal;

    public EpisodeState EpisodeState => episodeState;

    public string EpisodeId => episodeId;

    public string? PendingDecisionId => pendingDecisionId;

    /// <summary>
    /// Allocates the id sent with an observation. Calling it repeatedly before
    /// an action returns the same request, so a reconnect cannot create a
    /// second valid decision point.
    /// </summary>
    public BrainDecisionRequest? BeginDecisionRequest()
    {
        if (terminal)
            return null;

        pendingDecisionId ??= NextDecisionId();
        return new BrainDecisionRequest(episodeId, pendingDecisionId, observation);
    }

    public BrainExecutionResult SubmitDecision(BrainAction action) => SubmitDecision(action, null);

    public BrainExecutionResult SubmitDecision(BrainAction action, string? requestedDecisionId)
    {
        ArgumentNullException.ThrowIfNull(action);

        BrainExecutionResult result;
        if (terminal)
        {
            result = new BrainExecutionResult(false, "episode is terminal");
        }
        else if (requestedDecisionId != null
            && !string.Equals(requestedDecisionId, pendingDecisionId, StringComparison.Ordinal))
        {
            result = new BrainExecutionResult(false, "decision is stale");
        }
        else if (!observation.ActionMask.Allows(action))
        {
            result = new BrainExecutionResult(false, "action is masked");
        }
        else
        {
            result = executeAction(action);
        }

        var staleRequest = requestedDecisionId != null
            && !string.Equals(requestedDecisionId, pendingDecisionId, StringComparison.Ordinal);
        var decisionId = staleRequest
            ? requestedDecisionId!
            : pendingDecisionId ?? NextDecisionId();
        if (!staleRequest)
            pendingDecisionId = null;
        var transition = new BrainTransition(
            observation,
            action,
            result,
            Array.Empty<string>(),
            new RewardBreakdown(),
            null,
            terminal)
        {
            EpisodeId = episodeId,
            DecisionId = decisionId,
        };
        LastTransition = transition;
        transitionHistory.Add(transition);
        EnqueueEvent(new BrainEvent(
            "decision",
            observation.GameTimeSeconds,
            decisionId,
            null,
            null,
            new Dictionary<string, string>
            {
                ["action"] = action.Kind.ToString(),
                ["accepted"] = result.Accepted.ToString(),
                ["track_id"] = action.TrackId ?? string.Empty,
            }));
        return result;
    }

    public void EnqueueEvent(BrainEvent gameEvent)
    {
        ArgumentNullException.ThrowIfNull(gameEvent);
        pendingEvents.Add(gameEvent);
    }

    public IReadOnlyList<BrainEvent> DrainEvents()
    {
        var drained = pendingEvents.ToArray();
        pendingEvents.Clear();
        return drained;
    }

    /// <summary>
    /// 由 Logic/游戏事件适配器在动作执行后发布下一观察和原生结果。
    /// </summary>
    public void PublishNextObservation(
        BrainObservation? nextObservation,
        IReadOnlyList<string>? gameEvents,
        RewardBreakdown reward,
        bool terminal)
    {
        ArgumentNullException.ThrowIfNull(reward);
        if (nextObservation != null)
            observation = nextObservation;
        this.terminal = terminal;
        episodeState = terminal ? EpisodeState.Terminal : EpisodeState.Running;

        if (LastTransition is { } previous)
        {
            var updated = previous with
            {
                GameEvents = gameEvents?.ToArray() ?? Array.Empty<string>(),
                Reward = reward,
                NextObservation = nextObservation,
                Terminal = terminal,
            };
            LastTransition = updated;
            transitionHistory[^1] = updated;
        }
    }

    /// <summary>场景重载后由适配器替换当前观察。</summary>
    public void SetObservation(BrainObservation nextObservation)
    {
        observation = nextObservation ?? throw new ArgumentNullException(nameof(nextObservation));
    }

    public void ResetEpisode()
    {
        terminal = false;
        episodeState = EpisodeState.Running;
        episodeSequence++;
        decisionSequence = 0;
        pendingDecisionId = null;
        episodeId = $"{episodeBaseId}:reset:{episodeSequence}";
        LastTransition = null;
        transitionHistory.Clear();
        pendingEvents.Clear();
    }

    private string NextDecisionId() => $"{episodeId}:decision:{++decisionSequence}";
}
