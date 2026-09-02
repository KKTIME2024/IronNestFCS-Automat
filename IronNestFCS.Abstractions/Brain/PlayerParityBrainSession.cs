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

    public string EpisodeId => facade.EpisodeId;
    public string? PendingDecisionId => facade.PendingDecisionId;

    public BrainDecisionRequest? BeginDecisionRequest() => facade.BeginDecisionRequest();

    public bool SupersedePendingDecision() => facade.SupersedePendingDecision();

    /// <summary>当前决策点的合法动作掩码。</summary>
    public ActionMask GetValidActions() => facade.GetObservation().ActionMask;

    public BrainTransition? LastTransition => facade.LastTransition;

    public IReadOnlyList<BrainTransition> TransitionHistory => facade.TransitionHistory;

    public bool IsTerminal => facade.IsTerminal;

    public BrainExecutionResult SubmitDecision(BrainAction action) => facade.SubmitDecision(action);

    public BrainExecutionResult SubmitDecision(BrainAction action, string decisionId) =>
        facade.SubmitDecision(action, decisionId);

    public IReadOnlyList<BrainEvent> DrainEvents() => facade.DrainEvents();

    public EpisodeState EpisodeState => facade.EpisodeState;

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
    /// Accept a player-visible map reveal/marker position. The Logic adapter
    /// must obtain the position from the same map surface a player can inspect;
    /// this method does not accept entities, health, velocity, or hidden state.
    /// </summary>
    public bool IngestMapReveal(
        string trackId,
        string sourceId,
        double receivedAtSeconds,
        MapPoint position,
        string evidence,
        string? reportedGrid = null,
        double? reportedAtSeconds = null,
        IntelTimeBasis reportedTimeBasis = IntelTimeBasis.SessionSeconds)
    {
        ArgumentNullException.ThrowIfNull(trackId);
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(evidence);

        var accepted = Knowledge.Apply(new TrackIntel(
            trackId,
            new IntelProvenance(
                IntelSourceKind.MapReveal,
                sourceId,
                receivedAtSeconds,
                evidence))
        {
            ReportedPosition = position,
            ReportedGrid = reportedGrid,
            ReportedAtSeconds = reportedAtSeconds ?? receivedAtSeconds,
            ReportedTimeBasis = reportedTimeBasis,
        });
        SetDecisionContext(receivedAtSeconds, currentActionMask);
        return accepted;
    }

    /// <summary>
    /// Merge a fact produced by a Logic-side player-parity adapter (for example,
    /// converting an explicitly printed grid through the public map geometry).
    /// OmniscientDebug remains rejected by <see cref="PlayerParityKnowledge"/>.
    /// </summary>
    public bool IngestLegalIntel(TrackIntel intel)
    {
        ArgumentNullException.ThrowIfNull(intel);
        var accepted = Knowledge.Apply(intel);
        SetDecisionContext(intel.Provenance.ReceivedAtSeconds, currentActionMask);
        return accepted;
    }

    /// <summary>
    /// Rebuild the high-level engage mask from facts that have an explicit
    /// player-parity position. A course/speed-only telegram remains visible in
    /// the observation but cannot become an Engage action until a legal map
    /// reveal supplies a position.
    /// </summary>
    public void RefreshEngageableTracks(
        double? gameTimeSeconds = null,
        IEnumerable<string>? excludedTrackIds = null)
    {
        var excluded = excludedTrackIds == null
            ? null
            : new HashSet<string>(excludedTrackIds, StringComparer.Ordinal);
        var engageable = Knowledge.Snapshot()
            .Where(x => x.ReportedPosition.HasValue
                && !x.TrackId.StartsWith("route:", StringComparison.Ordinal)
                && (excluded == null || !excluded.Contains(x.TrackId)))
            .Select(x => x.TrackId);
        var mask = new ActionMask(
            currentActionMask.HoldAllowed,
            engageable,
            currentActionMask.ContinueCurrentMissionAllowed,
            currentActionMask.AbortIfSafeAllowed);
        SetDecisionContext(gameTimeSeconds ?? facade.GetObservation().GameTimeSeconds, mask);
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
