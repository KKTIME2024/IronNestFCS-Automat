namespace IronNestFCS.Abstractions.Brain;

/// <summary>
/// Main-thread owner of the Brain runtime. The only thread-safe ingress is the
/// pure DTO queue; every session, knowledge, episode, reward, and game adapter
/// call is made from Tick().
/// </summary>
public sealed class BrainRuntimeCoordinator : IDisposable
{
    private readonly PlayerParityBrainSession session;
    private readonly IBrainRuntimeHost host;
    private readonly BrainPipeRequestQueue pipe;
    private readonly IBrainResetDriver resetDriver;
    private readonly IBrainPolicy? inProcessPolicy;
    private readonly BrainRewardAttributionLedger ledger = new();
    private readonly double policyTimeoutSeconds;
    private readonly double resetTimeoutSeconds;
    private readonly List<BrainRuntimeAuditEvent> audit = new();
    private readonly Queue<BrainRuntimeAuditEvent> recentAudit = new();
    private readonly Dictionary<string, RewardBreakdown> accountedRewards = new(StringComparer.Ordinal);

    private BrainEpisode? episode;
    private BrainRuntimeState state = BrainRuntimeState.Disabled;
    private string? pendingDecisionId;
    private double pendingDecisionWallTime;
    private string? lastDecisionId;
    private bool waitingLogged;
    private double resetStartWallTime;
    private int resetAuditCursor;
    private long runtimeGeneration;
    private readonly HashSet<string> emittedStages = new(StringComparer.Ordinal);

    public BrainRuntimeCoordinator(
        PlayerParityBrainSession session,
        IBrainRuntimeHost host,
        BrainPipeRequestQueue pipe,
        IBrainResetDriver resetDriver,
        double policyTimeoutSeconds = 5.0,
        double resetTimeoutSeconds = 30.0,
        IBrainPolicy? inProcessPolicy = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        this.resetDriver = resetDriver ?? throw new ArgumentNullException(nameof(resetDriver));
        this.inProcessPolicy = inProcessPolicy;
        if (policyTimeoutSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(policyTimeoutSeconds));
        if (resetTimeoutSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(resetTimeoutSeconds));
        this.policyTimeoutSeconds = policyTimeoutSeconds;
        this.resetTimeoutSeconds = resetTimeoutSeconds;
    }

    public BrainRuntimeState State => state;
    public string EpisodeId => session.EpisodeId;
    public long RuntimeGeneration => runtimeGeneration;
    public string? PendingDecisionId => pendingDecisionId;
    public string? LastDecisionId => lastDecisionId;
    public BrainEpisode Episode => episode ?? throw new InvalidOperationException("no active mission episode");
    public BrainRewardAttributionLedger Ledger => ledger;
    public IReadOnlyCollection<BrainRuntimeAuditEvent> RecentAudit => recentAudit;
    public string? LastException { get; private set; }

    public void Start()
    {
        if (state != BrainRuntimeState.Disabled)
            return;

        state = BrainRuntimeState.WaitingForMission;
        Audit("BRAIN_RUNTIME_ENABLED", 0.0, detail: "runtime started");
        AuditStage("BRAIN_BRINGUP_STAGE_01_RUNTIME_STARTED", 0.0);
    }

    /// <summary>Call once per Unity Update from the main thread.</summary>
    public void Tick(double gameTimeSeconds, double wallTimeSeconds)
    {
        if (state == BrainRuntimeState.Disabled)
            return;

        try
        {
            if (state == BrainRuntimeState.Resetting)
            {
                // Keep draining events while the old scene is still owned by
                // the episode. A shell may impact after the mission terminal marker;
                // generation/mission attribution still decides whether it is
                // valid. Once reset succeeds, the next Tick uses the new id.
                DrainGameEvents(gameTimeSeconds, wallTimeSeconds);
                TickReset(gameTimeSeconds, wallTimeSeconds);
                DrainPipeRequests(gameTimeSeconds, wallTimeSeconds);
                return;
            }

            DrainGameEvents(gameTimeSeconds, wallTimeSeconds);

            if (state == BrainRuntimeState.WaitingForMission)
            {
                if (host.MissionReady)
                    BeginMission(gameTimeSeconds, wallTimeSeconds);
                else if (!waitingLogged)
                {
                    waitingLogged = true;
                    Audit("WAITING_FOR_MISSION", gameTimeSeconds);
                }
            }

            DrainPipeRequests(gameTimeSeconds, wallTimeSeconds);

            if (state == BrainRuntimeState.Running
                && pendingDecisionId != null
                && wallTimeSeconds - pendingDecisionWallTime >= policyTimeoutSeconds)
            {
                SubmitHoldForTimeout(gameTimeSeconds, wallTimeSeconds);
            }
        }
        catch (Exception exception)
        {
            EnterSafeFailure(gameTimeSeconds, exception);
        }
    }

    /// <summary>
    /// Lets Logic announce a real event boundary (gun idle, mission result,
    /// or newly received legal intel) without polling a policy every frame.
    /// </summary>
    public bool NotifyDecisionPoint(string reason, double gameTimeSeconds, double wallTimeSeconds)
    {
        if (state != BrainRuntimeState.Running)
            return false;
        try
        {
            if (pendingDecisionId != null)
            {
                var superseded = pendingDecisionId;
                if (!session.SupersedePendingDecision())
                    return false;
                Audit("DECISION_SUPERSEDED", gameTimeSeconds, superseded, reason);
                pendingDecisionId = null;
            }
            return RequestDecision(reason, gameTimeSeconds, wallTimeSeconds);
        }
        catch (Exception exception)
        {
            EnterSafeFailure(gameTimeSeconds, exception);
            return false;
        }
    }

    public void RecordIntelEvent(string detail, double gameTimeSeconds)
    {
        if (state is BrainRuntimeState.Disabled or BrainRuntimeState.Failed)
            return;
        Audit("INTEL_EVENT", gameTimeSeconds, detail: detail);
        Audit("TRACK_UPDATE", gameTimeSeconds, detail: "PlayerParityKnowledge refreshed");
        AuditStage("BRAIN_BRINGUP_STAGE_03_INTEL_RECEIVED", gameTimeSeconds);
        if (session.GetValidActions().EngageableTrackIds.Count > 0)
            AuditStage("BRAIN_BRINGUP_STAGE_04_TRACK_ACTIONABLE", gameTimeSeconds);
    }

    public IReadOnlyList<BrainRuntimeAuditEvent> DrainAudit()
    {
        var result = audit.ToArray();
        audit.Clear();
        return result;
    }

    public void Dispose()
    {
        pipe.Stop();
        state = BrainRuntimeState.Disabled;
        pendingDecisionId = null;
    }

    private void BeginMission(double gameTimeSeconds, double wallTimeSeconds)
    {
        if (episode != null && episode.State is not (EpisodeState.Starting or EpisodeState.WaitingForInitialObservation))
            return;

        host.RefreshObservation(gameTimeSeconds);
        if (episode == null)
        {
            runtimeGeneration++;
            episode = new BrainEpisode(session.EpisodeId, seed: null, gameTimeSeconds);
            ledger.StartEpisode(session.EpisodeId);
            accountedRewards.Clear();
        }
        episode.MarkInitialObservation();
        waitingLogged = false;
        state = BrainRuntimeState.Running;
        Audit("MISSION_DETECTED", gameTimeSeconds);
        Audit("EPISODE_START", gameTimeSeconds);
        AuditStage("BRAIN_BRINGUP_STAGE_02_MISSION_BOUND", gameTimeSeconds);
        if (runtimeGeneration >= 2)
            AuditStage("BRAIN_BRINGUP_STAGE_14_EPISODE_2_STARTED", gameTimeSeconds);
        RequestDecision("MISSION_DETECTED", gameTimeSeconds, wallTimeSeconds);
    }

    private bool RequestDecision(string reason, double gameTimeSeconds, double wallTimeSeconds)
    {
        if (state != BrainRuntimeState.Running || pendingDecisionId != null)
            return false;

        host.RefreshObservation(gameTimeSeconds);
        var request = session.BeginDecisionRequest();
        if (request == null)
            return false;

        pendingDecisionId = request.DecisionId;
        pendingDecisionWallTime = wallTimeSeconds;
        Audit("DECISION_REQUEST", gameTimeSeconds, request.DecisionId, reason);
        Audit(
            "OBSERVATION_SUMMARY",
            gameTimeSeconds,
            request.DecisionId,
            $"tracks={request.Observation.Tracks.Count}");
        Audit(
            "VALID_ACTIONS",
            gameTimeSeconds,
            request.DecisionId,
            $"hold={request.Observation.ActionMask.HoldAllowed};" +
            $"continue={request.Observation.ActionMask.ContinueCurrentMissionAllowed};" +
            $"abort={request.Observation.ActionMask.AbortIfSafeAllowed};" +
            $"engage={request.Observation.ActionMask.EngageableTrackIds.Count}");
        AuditStage("BRAIN_BRINGUP_STAGE_05_DECISION_REQUESTED", gameTimeSeconds, request.DecisionId);
        if (inProcessPolicy != null)
            RunInProcessPolicy(request, gameTimeSeconds);
        return true;
    }

    private void RunInProcessPolicy(BrainDecisionRequest request, double gameTimeSeconds)
    {
        BrainAction action;
        try
        {
            action = inProcessPolicy!.Choose(request.Observation);
        }
        catch (Exception exception)
        {
            Audit("RUNTIME_ERROR", gameTimeSeconds, request.DecisionId,
                $"in-process policy failed: {exception.GetType().Name}; default Hold");
            action = new BrainAction(BrainActionKind.Hold);
        }

        Audit("ACTION_RECEIVED", gameTimeSeconds, request.DecisionId,
            $"inprocess:{action.Kind}");
        if (!session.GetValidActions().Allows(action))
        {
            Audit("ACTION_REJECTED", gameTimeSeconds, request.DecisionId,
                "in-process policy produced a masked action; default Hold");
            action = new BrainAction(BrainActionKind.Hold);
            if (!session.GetValidActions().Allows(action))
            {
                session.SupersedePendingDecision();
                pendingDecisionId = null;
                return;
            }
        }

        SubmitAction(action, request.DecisionId, gameTimeSeconds);
    }

    private void DrainPipeRequests(double gameTimeSeconds, double wallTimeSeconds)
    {
        while (pipe.TryDequeue(out var pending))
        {
            BrainPipeResponse response;
            try
            {
                response = HandlePipeRequest(pending.Request, gameTimeSeconds, wallTimeSeconds);
            }
            catch (Exception exception)
            {
                Audit("RUNTIME_ERROR", gameTimeSeconds, detail: exception.GetType().Name);
                response = BrainPipeResponse.HoldFallback("runtime handler failed");
            }
            pending.Complete(response);
        }
    }

    private BrainPipeResponse HandlePipeRequest(
        BrainPipeRequest request,
        double gameTimeSeconds,
        double wallTimeSeconds)
    {
        var type = request.Type?.Trim().ToLowerInvariant();
        return type switch
        {
            "ping" => BrainPipeResponse.Pong(),
            "hello" => new BrainPipeResponse(
                "hello",
                BrainPipeProtocol.CurrentVersion,
                EpisodeId: EpisodeId,
                RuntimeGeneration: RuntimeGeneration,
                Payload: BrainWireCodec.EncodeStatus(state.ToString(), EpisodeId, RuntimeGeneration)),
            "observation" => HandleObservationRequest(request, gameTimeSeconds),
            "action" => HandleActionRequest(request, gameTimeSeconds, wallTimeSeconds),
            "reset" => BrainPipeResponse.ErrorResponse("reset is game-terminal driven"),
            _ => BrainPipeResponse.ErrorResponse($"unsupported request type: {request.Type}"),
        };
    }

    private BrainPipeResponse HandleObservationRequest(BrainPipeRequest request, double gameTimeSeconds)
    {
        if (string.IsNullOrWhiteSpace(request.EpisodeId))
            return new BrainPipeResponse(
                "observation",
                BrainPipeProtocol.CurrentVersion,
                EpisodeId: EpisodeId,
                Accepted: false,
                Error: "episode_id is required");
        if (!EpisodeMatches(request.EpisodeId))
            return new BrainPipeResponse(
                "observation",
                BrainPipeProtocol.CurrentVersion,
                EpisodeId: EpisodeId,
                Accepted: false,
                Error: "episode_id mismatch");
        if (request.RuntimeGeneration != RuntimeGeneration)
            return new BrainPipeResponse(
                "observation",
                BrainPipeProtocol.CurrentVersion,
                EpisodeId: EpisodeId,
                Accepted: false,
                Error: "runtime_generation mismatch",
                RuntimeGeneration: RuntimeGeneration,
                Payload: BrainWireCodec.EncodeStatus(state.ToString(), EpisodeId, RuntimeGeneration));

        if (state == BrainRuntimeState.WaitingForMission)
            return new BrainPipeResponse(
                "waiting",
                BrainPipeProtocol.CurrentVersion,
                EpisodeId: EpisodeId,
                RuntimeGeneration: RuntimeGeneration,
                Payload: BrainWireCodec.EncodeStatus(state.ToString(), EpisodeId, RuntimeGeneration));
        if (state is BrainRuntimeState.Terminal or BrainRuntimeState.Resetting)
            return new BrainPipeResponse(
                "terminal",
                BrainPipeProtocol.CurrentVersion,
                EpisodeId: EpisodeId,
                Accepted: false,
                Error: state.ToString(),
                RuntimeGeneration: RuntimeGeneration,
                Payload: BrainWireCodec.EncodeStatus(state.ToString(), EpisodeId, RuntimeGeneration));
        if (state == BrainRuntimeState.Failed)
            return BrainPipeResponse.ErrorResponse("runtime is in safe failed state");
        if (pendingDecisionId == null)
            return new BrainPipeResponse(
                "waiting",
                BrainPipeProtocol.CurrentVersion,
                EpisodeId: EpisodeId,
                RuntimeGeneration: RuntimeGeneration,
                Payload: BrainWireCodec.EncodeStatus("NoDecisionPoint", EpisodeId, RuntimeGeneration));

        var decision = session.BeginDecisionRequest()!;
        return new BrainPipeResponse(
            "observation",
            BrainPipeProtocol.CurrentVersion,
            EpisodeId: EpisodeId,
            DecisionId: decision.DecisionId,
            RuntimeGeneration: RuntimeGeneration,
            Payload: BrainWireCodec.EncodeObservation(EpisodeId, RuntimeGeneration, decision, session.EpisodeState));
    }

    private BrainPipeResponse HandleActionRequest(
        BrainPipeRequest request,
        double gameTimeSeconds,
        double wallTimeSeconds)
    {
        Audit("ACTION_RECEIVED", gameTimeSeconds, request.DecisionId, request.ActionKind?.ToString());
        if (state != BrainRuntimeState.Running)
            return Reject(request, $"runtime state is {state}", gameTimeSeconds);
        if (string.IsNullOrWhiteSpace(request.EpisodeId))
            return Reject(request, "episode_id is required", gameTimeSeconds);
        if (!EpisodeMatches(request.EpisodeId))
            return Reject(request, "episode_id mismatch", gameTimeSeconds);
        if (request.RuntimeGeneration != RuntimeGeneration)
            return Reject(request, "runtime_generation mismatch", gameTimeSeconds);
        if (pendingDecisionId == null
            || !string.Equals(pendingDecisionId, request.DecisionId, StringComparison.Ordinal))
            return Reject(request, "decision_id is stale", gameTimeSeconds);
        if (request.ActionKind == null)
            return Reject(request, "action_kind is required", gameTimeSeconds);

        var action = new BrainAction(request.ActionKind.Value, request.TrackId);
        if (!session.GetValidActions().Allows(action))
            return Reject(request, "action is masked", gameTimeSeconds);

        var (result, reward) = SubmitAction(action, request.DecisionId!, gameTimeSeconds);
        return new BrainPipeResponse(
            "action",
            BrainPipeProtocol.CurrentVersion,
            EpisodeId: EpisodeId,
            DecisionId: request.DecisionId,
            RuntimeGeneration: RuntimeGeneration,
            Accepted: result.Accepted,
            ActionKind: request.ActionKind,
            TrackId: request.TrackId,
            Error: result.Accepted ? null : result.Reason,
            Payload: BrainWireCodec.EncodeTransition(session.LastTransition, state, reward));
    }

    private (BrainExecutionResult Result, RewardBreakdown Reward) SubmitAction(
        BrainAction action,
        string decisionId,
        double gameTimeSeconds)
    {
        var result = session.SubmitDecision(action, decisionId);
        pendingDecisionId = null;
        lastDecisionId = decisionId;
        host.RecordDecisionResult(decisionId, action, result);
        if (episode != null && result.Accepted)
            episode.RegisterDecision();
        if (result.Accepted)
        {
            ledger.Apply(new BrainGameEvent(
                BrainEventType.BrainDecision,
                EpisodeId,
                gameTimeSeconds,
                decisionId,
                result.MissionId,
                RoundId: null));
            Audit("ACTION_ACCEPTED", gameTimeSeconds, decisionId, result.Reason);
            AuditStage("BRAIN_BRINGUP_STAGE_06_ACTION_ACCEPTED", gameTimeSeconds, decisionId);
            if (result.MissionId != null)
            {
                Audit("MISSION_ENQUEUED", gameTimeSeconds, decisionId, result.MissionId);
                AuditStage("BRAIN_BRINGUP_STAGE_07_FSC_MISSION_CREATED", gameTimeSeconds, decisionId);
            }
        }
        else
        {
            Audit("ACTION_REJECTED", gameTimeSeconds, decisionId, result.Reason);
        }

        return (result, result.Accepted ? ledger.GetReward(decisionId) : new RewardBreakdown());
    }

    private void SubmitHoldForTimeout(double gameTimeSeconds, double wallTimeSeconds)
    {
        var decisionId = pendingDecisionId;
        if (decisionId == null || state != BrainRuntimeState.Running)
            return;

        Audit("POLICY_TIMEOUT", gameTimeSeconds, decisionId, "default Hold");
        SubmitAction(
            new BrainAction(BrainActionKind.Hold),
            decisionId,
            gameTimeSeconds);
    }

    /// <summary>Called by Logic after the accepted action has reached FSC.</summary>
    public void RecordGunCommitted(double gameTimeSeconds)
    {
        if (state != BrainRuntimeState.Running)
            return;
        AuditStage("BRAIN_BRINGUP_STAGE_08_GUN_COMMITTED", gameTimeSeconds,
            pendingDecisionId ?? lastDecisionId);
    }

    private void DrainGameEvents(double gameTimeSeconds, double wallTimeSeconds)
    {
        foreach (var gameEvent in host.DrainEvents() ?? Array.Empty<BrainGameEvent>())
        {
            if (!string.Equals(gameEvent.EpisodeId, EpisodeId, StringComparison.Ordinal))
            {
                Audit("STALE_EVENT_REJECTED", gameTimeSeconds, detail: gameEvent.EpisodeId);
                continue;
            }

            // A terminal callback is a one-way ownership boundary. Once the
            // first callback moved the episode to Resetting, later duplicate
            // terminal notifications must not add a second success/failed
            // reward or start another reset flight. Non-terminal impacts are
            // still drained below until reset succeeds.
            if (state != BrainRuntimeState.Running
                && gameEvent.Type is (BrainEventType.MissionCompleted or BrainEventType.MissionFailed))
            {
                Audit("DUPLICATE_TERMINAL_IGNORED", gameTimeSeconds, gameEvent.DecisionId,
                    gameEvent.Type.ToString());
                continue;
            }

            Audit(ToAuditType(gameEvent.Type), gameEvent.GameTimeSeconds, gameEvent.DecisionId,
                gameEvent.MissionId ?? gameEvent.PublicTrackId);
            if (gameEvent.Type == BrainEventType.ShotFired)
                AuditStage("BRAIN_BRINGUP_STAGE_09_SHOT_FIRED", gameEvent.GameTimeSeconds, gameEvent.DecisionId);
            else if (gameEvent.Type == BrainEventType.ShellImpact)
                AuditStage("BRAIN_BRINGUP_STAGE_10_IMPACT_RECEIVED", gameEvent.GameTimeSeconds, gameEvent.DecisionId);
            if (ledger.Apply(gameEvent)
                && ledger.TryResolveDecisionId(gameEvent, out var decisionId))
            {
                var current = ledger.GetReward(decisionId);
                var previous = accountedRewards.TryGetValue(decisionId, out var known)
                    ? known
                    : new RewardBreakdown();
                var delta = Subtract(current, previous);
                if (delta != new RewardBreakdown())
                {
                    accountedRewards[decisionId] = current;
                    if (episode?.State == EpisodeState.Running)
                        episode.RegisterTransition(delta);
                    else if (episode?.State == EpisodeState.Resetting)
                    {
                        episode.RegisterDelayedReward(delta);
                        session.PublishTransition(
                            nextObservation: null,
                            gameEvents: new[] { "delayed-impact-before-reset" },
                            reward: episode.RewardTotal,
                            terminal: true);
                    }
                    Audit("REWARD_COMPONENT", gameEvent.GameTimeSeconds, decisionId, BrainWireCodec.EncodeTransition(null, state, delta));
                }
            }

            if (state == BrainRuntimeState.Running
                && gameEvent.Type is (BrainEventType.MissionCompleted or BrainEventType.MissionFailed))
                EndEpisode(gameEvent.Type == BrainEventType.MissionFailed ? "mission-failed" : "mission-completed",
                    gameEvent.Type == BrainEventType.MissionFailed,
                    gameTimeSeconds,
                    wallTimeSeconds);
            if (state == BrainRuntimeState.Failed)
                break;
        }
    }

    private void EndEpisode(string reason, bool failed, double gameTimeSeconds, double wallTimeSeconds)
    {
        if (state != BrainRuntimeState.Running || episode == null || !episode.TryTerminate(reason, failed))
            return;

        state = BrainRuntimeState.Terminal;
        session.PublishTransition(
            nextObservation: null,
            gameEvents: new[] { reason },
            reward: episode.RewardTotal,
            terminal: true);
        Audit("EPISODE_TERMINAL", gameTimeSeconds, lastDecisionId, reason);
        AuditStage("BRAIN_BRINGUP_STAGE_11_EPISODE_TERMINAL", gameTimeSeconds, lastDecisionId, reason);
        if (!episode.BeginReset())
        {
            state = BrainRuntimeState.Failed;
            Audit("RESET_FAILED", gameTimeSeconds, detail: "episode could not enter Resetting");
            return;
        }

        state = BrainRuntimeState.Resetting;
        pendingDecisionId = null;
        resetStartWallTime = wallTimeSeconds;
        Audit("RESET_START", gameTimeSeconds, detail: EpisodeId);
        AuditStage("BRAIN_BRINGUP_STAGE_12_RESET_STARTED", gameTimeSeconds);
        if (!resetDriver.RequestReset(wallTimeSeconds))
        {
            state = BrainRuntimeState.Failed;
            Audit("RESET_FAILED", gameTimeSeconds, detail: "reset driver rejected request");
        }
    }

    private void TickReset(double gameTimeSeconds, double wallTimeSeconds)
    {
        if (wallTimeSeconds - resetStartWallTime > resetTimeoutSeconds)
        {
            state = BrainRuntimeState.Failed;
            Audit("RESET_FAILED", gameTimeSeconds, detail: "runtime reset watchdog timeout");
            return;
        }

        resetDriver.Tick(wallTimeSeconds);
        FlushResetAudit(gameTimeSeconds);
        if (resetDriver.Phase == BrainResetPhase.Failed)
        {
            state = BrainRuntimeState.Failed;
            Audit("RESET_FAILED", gameTimeSeconds, detail: "reset watchdog failed");
            return;
        }
        if (resetDriver.Phase != BrainResetPhase.Succeeded || episode == null)
            return;

        // A real adapter normally clears the session in its clearKnowledge
        // callback. The fallback keeps fake drivers and future adapters safe.
        if (string.Equals(session.EpisodeId, episode.EpisodeId, StringComparison.Ordinal))
            session.ResetEpisode();
        ledger.StartEpisode(session.EpisodeId);
        accountedRewards.Clear();
        pendingDecisionId = null;
        lastDecisionId = null;
        runtimeGeneration++;
        episode.CompleteReset(session.EpisodeId, gameTimeSeconds);
        state = BrainRuntimeState.WaitingForMission;
        waitingLogged = false;
        Audit("FSC_REBOUND", gameTimeSeconds, detail: resetDriver.EpisodeId);
        Audit("KNOWLEDGE_CLEARED", gameTimeSeconds, detail: session.EpisodeId);
        Audit("FIRST_OBSERVATION", gameTimeSeconds);
        AuditStage("BRAIN_BRINGUP_STAGE_13_RESET_SUCCESS", gameTimeSeconds);
        if (host.MissionReady)
            BeginMission(gameTimeSeconds, wallTimeSeconds);
    }

    private void FlushResetAudit(double gameTimeSeconds)
    {
        var events = resetDriver.AuditEvents;
        if (events == null)
            return;
        // BrainResetHarness clears its list when a new reset starts. Treat a
        // shorter list as a new generation rather than replaying old entries.
        if (events.Count < resetAuditCursor)
            resetAuditCursor = 0;
        for (; resetAuditCursor < events.Count; resetAuditCursor++)
        {
            var resetEvent = events[resetAuditCursor];
            var auditType = resetEvent.Type switch
            {
                "OBJECT_TEARDOWN" => "OLD_MISSION_GONE",
                "NEW_MISSION_DETECTED" => "NEW_MISSION_FOUND",
                _ => resetEvent.Type,
            };
            // RESET_START is emitted by EndEpisode at the exact game-side
            // decision boundary; avoid duplicating it from the harness.
            if (string.Equals(auditType, "RESET_START", StringComparison.Ordinal))
                continue;
            Audit(auditType, gameTimeSeconds, detail: resetEvent.Detail ?? resetEvent.EpisodeId);
        }
    }

    private bool EpisodeMatches(string? requestedEpisodeId)
    {
        return requestedEpisodeId == null
            || string.Equals(requestedEpisodeId, EpisodeId, StringComparison.Ordinal);
    }

    private BrainPipeResponse Reject(BrainPipeRequest request, string reason, double gameTimeSeconds)
    {
        Audit("ACTION_REJECTED", gameTimeSeconds, request.DecisionId, reason);
        return new BrainPipeResponse(
            "action",
            BrainPipeProtocol.CurrentVersion,
            EpisodeId: EpisodeId,
            DecisionId: request.DecisionId,
            RuntimeGeneration: RuntimeGeneration,
            Accepted: false,
            ActionKind: request.ActionKind,
            TrackId: request.TrackId,
            Error: reason);
    }

    private void Audit(string type, double gameTimeSeconds, string? decisionId = null, string? detail = null)
    {
        var entry = new BrainRuntimeAuditEvent(type, EpisodeId, gameTimeSeconds, decisionId, detail);
        audit.Add(entry);
        recentAudit.Enqueue(entry);
        while (recentAudit.Count > 64)
            recentAudit.Dequeue();
    }

    private void EnterSafeFailure(double gameTimeSeconds, Exception exception)
    {
        if (state == BrainRuntimeState.Disabled)
            return;
        var recent = string.Join(
            " <- ",
            recentAudit.Reverse().Take(8).Select(x =>
                $"{x.Type}[{x.EpisodeId}]{(x.DecisionId == null ? string.Empty : $"/{x.DecisionId}")}"));
        state = BrainRuntimeState.Failed;
        pendingDecisionId = null;
        LastException = $"{exception.GetType().Name}: {exception.Message}";
        Audit(
            "RUNTIME_ERROR",
            gameTimeSeconds,
            detail: $"{exception.GetType().Name}: {exception.Message}; recent={recent}");
    }

    private void AuditStage(string stage, double gameTimeSeconds, string? decisionId = null, string? detail = null)
    {
        // Stage 01 is a process milestone. All later milestones are scoped to
        // the runtime generation so a second episode proves the reset path.
        var key = stage == "BRAIN_BRINGUP_STAGE_01_RUNTIME_STARTED"
            ? stage
            : $"{stage}@{runtimeGeneration}";
        if (emittedStages.Add(key))
            Audit(stage, gameTimeSeconds, decisionId, detail);
    }

    private static string ToAuditType(BrainEventType type) => type switch
    {
        BrainEventType.ShotFired => "SHOT_FIRED",
        BrainEventType.ShellImpact => "IMPACT",
        BrainEventType.TargetDamaged or BrainEventType.TargetDestroyed => "REWARD_COMPONENT",
        BrainEventType.MissionCreated => "MISSION_ENQUEUED",
        BrainEventType.MissionAssigned => "MISSION_ASSIGNED",
        BrainEventType.MissionCompleted or BrainEventType.MissionFailed => "MISSION_TERMINAL_EVENT",
        BrainEventType.AmmoConsumed => "REWARD_COMPONENT",
        BrainEventType.IronNestDamaged => "REWARD_COMPONENT",
        _ => type.ToString().ToUpperInvariant(),
    };

    private static RewardBreakdown Subtract(RewardBreakdown current, RewardBreakdown previous)
    {
        return new RewardBreakdown(
            current.DamageReward - previous.DamageReward,
            current.TargetDestroyedReward - previous.TargetDestroyedReward,
            current.MissionSuccessReward - previous.MissionSuccessReward,
            current.MissionFailedPenalty - previous.MissionFailedPenalty,
            current.IronNestDamagePenalty - previous.IronNestDamagePenalty,
            current.AmmoCostPenalty - previous.AmmoCostPenalty,
            current.ElapsedTimePenalty - previous.ElapsedTimePenalty,
            current.MissPenalty - previous.MissPenalty,
            current.ImmuneHitPenalty - previous.ImmuneHitPenalty);
    }
}
