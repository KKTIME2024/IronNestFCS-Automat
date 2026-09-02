namespace IronNestFCS.Abstractions.Brain;

/// <summary>
/// Player-parity session boundary used by the future Logic adapter.
/// 
/// The session accepts only visible telegram text and caller-supplied legal
/// action masks. It deliberately has no API that accepts GameObject, MapEntity,
/// radar or hidden entity state, so a runtime integration cannot accidentally
/// turn the formal Brain into an omniscient consumer.
/// </summary>
public sealed class PlayerParityBrainSession : IBrainFacade
{
    private readonly BrainFacade facade;
    private readonly Func<BrainAction, BrainExecutionResult> executeAction;
    private readonly double initialGameTimeSeconds;
    private readonly ActionMask initialActionMask;
    private ActionMask currentActionMask;

    public PlayerParityBrainSession(
        double initialGameTimeSeconds,
        ActionMask initialActionMask,
        Func<BrainAction, BrainExecutionResult> executeAction)
    {
        ArgumentNullException.ThrowIfNull(initialActionMask);
        ArgumentNullException.ThrowIfNull(executeAction);

        this.initialGameTimeSeconds = initialGameTimeSeconds;
        this.initialActionMask = initialActionMask;
        currentActionMask = initialActionMask;
        this.executeAction = executeAction;
        Knowledge = new PlayerParityKnowledge();
        facade = new BrainFacade(
            new BrainObservation(initialGameTimeSeconds, Knowledge.Snapshot(), initialActionMask),
            executeAction);
    }

    /// <summary>正式 Brain 的唯一战场知识库；它只接受 PlayerParity provenance。</summary>
    public PlayerParityKnowledge Knowledge { get; }

    public BrainObservation GetObservation() => facade.GetObservation();

    /// <summary>当前决策点的合法动作掩码。</summary>
    public ActionMask GetValidActions() => facade.GetObservation().ActionMask;

    public BrainTransition? LastTransition => facade.LastTransition;

    public IReadOnlyList<BrainTransition> TransitionHistory => facade.TransitionHistory;

    public bool IsTerminal => facade.IsTerminal;

    public BrainExecutionResult SubmitDecision(BrainAction action) => facade.SubmitDecision(action);

    /// <summary>
    /// 将最终可见的电报文本转成语义事实，再刷新 observation。
    /// 解析器不会读取任务实体或补全未显示的身份/位置/速度。
    /// </summary>
    public TelegramSemanticEvent IngestTelegram(
        string sourceId,
        double receivedAtSeconds,
        string visibleText)
    {
        var message = TelegramSemanticParser.Parse(sourceId, receivedAtSeconds, visibleText);
        Knowledge.Apply(message);
        SetDecisionContext(receivedAtSeconds, currentActionMask);
        return message;
    }

    /// <summary>
    /// Logic 层在合法事件决策点更新游戏时钟和动作掩码。
    /// </summary>
    public void SetDecisionContext(double gameTimeSeconds, ActionMask actionMask)
    {
        ArgumentNullException.ThrowIfNull(actionMask);
        currentActionMask = actionMask;
        facade.SetObservation(new BrainObservation(
            gameTimeSeconds,
            Knowledge.Snapshot(),
            actionMask));
    }

    /// <summary>由 Logic 层在动作完成后发布原生事件、reward 和下一 observation。</summary>
    public void PublishTransition(
        BrainObservation? nextObservation,
        IReadOnlyList<string>? gameEvents,
        RewardBreakdown reward,
        bool terminal)
    {
        facade.PublishNextObservation(nextObservation, gameEvents, reward, terminal);
    }

    /// <summary>
    /// 场景/任务重载后的 episode reset：清除上一局全部合法情报，
    /// 并恢复到没有目标的初始 observation。
    /// </summary>
    public void ResetEpisode()
    {
        Knowledge.Clear();
        currentActionMask = initialActionMask;
        facade.ResetEpisode();
        facade.SetObservation(new BrainObservation(
            initialGameTimeSeconds,
            Knowledge.Snapshot(),
            initialActionMask));
    }
}
