namespace IronNestFCS.Abstractions.Brain;

/// <summary>
/// 运行时无关的 Brain 外壳。Logic 层只需把一个高层 action sink 注入进来，
/// 不必向 policy 暴露 FCS、炮塔或 IL2CPP 对象。
/// </summary>
public sealed class BrainFacade : IBrainFacade
{
    private readonly Func<BrainAction, BrainExecutionResult> executeAction;
    private readonly List<BrainTransition> transitionHistory = new();
    private BrainObservation observation;
    private bool terminal;

    public BrainFacade(
        BrainObservation initialObservation,
        Func<BrainAction, BrainExecutionResult> executeAction)
    {
        observation = initialObservation ?? throw new ArgumentNullException(nameof(initialObservation));
        this.executeAction = executeAction ?? throw new ArgumentNullException(nameof(executeAction));
    }

    public BrainObservation GetObservation() => observation;

    public BrainTransition? LastTransition { get; private set; }

    public IReadOnlyList<BrainTransition> TransitionHistory => transitionHistory;

    public bool IsTerminal => terminal;

    public BrainExecutionResult SubmitDecision(BrainAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        BrainExecutionResult result;
        if (terminal)
        {
            result = new BrainExecutionResult(false, "episode is terminal");
        }
        else if (!observation.ActionMask.Allows(action))
        {
            result = new BrainExecutionResult(false, "action is masked");
        }
        else
        {
            result = executeAction(action);
        }

        var transition = new BrainTransition(
            observation,
            action,
            result,
            Array.Empty<string>(),
            new RewardBreakdown(),
            null,
            terminal);
        LastTransition = transition;
        transitionHistory.Add(transition);
        return result;
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
        LastTransition = null;
        transitionHistory.Clear();
    }
}
