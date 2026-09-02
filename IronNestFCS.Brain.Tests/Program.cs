using IronNestFCS.Abstractions.Brain;

static class AssertEx
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }

    public static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}

static class BrainIntelTests
{
    public static void TelegramFactsAreAcceptedAndAuditable()
    {
        var provenance = new IntelProvenance(
            IntelSourceKind.Telegram,
            "teleprinter:hq:42",
            120.0,
            "visible telegram: course 180 degrees at 19.4 knots");
        var track = new TrackIntel("Convoy-1", provenance)
        {
            ReportedGrid = "G6 9:1",
            Motion = new MotionIntel(180.0, 0.010),
        };
        var knowledge = new PlayerParityKnowledge();

        AssertEx.True(knowledge.Apply(track), "telegram fact should be accepted");
        AssertEx.True(knowledge.TryGet("Convoy-1", out var snapshot), "track should be stored");
        AssertEx.Equal("G6 9:1", snapshot!.ReportedGrid, "grid must remain visible fact");
        AssertEx.Equal("teleprinter:hq:42", snapshot.LatestProvenance.SourceId, "provenance must be retained");
        AssertEx.True(snapshot.LatestProvenance.Kind == IntelSourceKind.Telegram, "source kind must remain telegram");
    }

    public static void OmniscientFactsAreRejectedBeforeStorage()
    {
        var debugTrack = new TrackIntel(
            "Hidden-Enemy",
            new IntelProvenance(IntelSourceKind.OmniscientDebug, "FireMission.Entities", 50.0, "hidden Position"))
        {
            ReportedPosition = new MapPoint(99.0, 99.0),
        };
        var knowledge = new PlayerParityKnowledge();

        AssertEx.True(!knowledge.Apply(debugTrack), "omniscient fact must be rejected");
        AssertEx.True(!knowledge.TryGet("Hidden-Enemy", out _), "rejected track must not be stored");
    }

    public static void VesselTelegramProducesOnlyVisibleMotionFacts()
    {
        var text = string.Join("\n", new[]
        {
            "VESSEL DATA",
            "[POINT CargoShip] Spotted:",
            "   Crossing [GRID G6 9:1] at T:10:00:00.",
            "   Steaming a course of 12° at 9.7 knots.",
            "   9.7 knots = 0.10km traveled in 20s.",
        });

        var message = TelegramSemanticParser.Parse("hq:42", 120.0, text);

        AssertEx.Equal(1, message.Tracks.Count, "one visible target should be parsed");
        var track = message.Tracks[0];
        AssertEx.Equal("CargoShip", track.TrackId, "track id must come from visible designation");
        AssertEx.Equal("G6 9:1", track.ReportedGrid, "grid must come from visible text");
        AssertEx.True(track.Motion != null, "explicit course and speed must create motion fact");
        AssertEx.Equal(12.0, track.Motion!.CourseDegrees, "course must be preserved");
        AssertEx.Near(9.7 / 3600.0 * 1.852, track.Motion.SpeedKmPerSecond, 1e-12, "knots must convert to km/s");
        AssertEx.Equal(10 * 3600.0, track.ReportedAtSeconds, "report time must be parsed");
        AssertEx.Equal(IntelTimeBasis.ClockOfDaySeconds, track.ReportedTimeBasis,
            "telegram clock must not be confused with the episode clock");
        AssertEx.True(track.Provenance.IsPlayerParity, "parsed text is a player-parity source");
    }

    public static void RouteTelegramProducesWaypointSchedule()
    {
        var text = string.Join("\n", new[]
        {
            "TRAIN SCHEDULE",
            "[POINT Train] APPROACH:",
            "Arrival station [POINT TrainStation]: [GRID A3].",
            "[POINT WPA] - 3.00km from station: T=10:06:50",
            "[POINT WPB] - 1.00km from station: T=10:10:10",
            "Arrival: T=10:16:50",
        });

        var message = TelegramSemanticParser.Parse("hq:iron-road", 10.0, text);

        AssertEx.Equal(1, message.Tracks.Count, "one train track should be parsed");
        var track = message.Tracks[0];
        AssertEx.Equal("Train", track.TrackId, "train designation must be used as track id");
        AssertEx.Equal(2, track.Route.Count, "two visible waypoints should be parsed");
        AssertEx.Equal("WPA", track.Route[0].WaypointId, "first waypoint must be retained");
        AssertEx.Equal(3.0, track.Route[0].DistanceKm, "waypoint distance must be retained");
        AssertEx.Equal(10 * 3600.0 + 6 * 60.0 + 50.0, track.Route[0].PassageTimeSeconds, "waypoint time must be retained");
        AssertEx.Equal(10 * 3600.0 + 16 * 60.0 + 50.0, track.EstimatedArrivalSeconds, "arrival time must be retained");
    }

    public static void RouteTelegramDoesNotInventTrainFromDestinationPoint()
    {
        // The real Iron Road/Final Harvest shape names the station and waypoints,
        // but does not necessarily print a separate moving-unit point designation.
        // A destination marker must not silently become the train's identity.
        var text = string.Join("\n", new[]
        {
            "TRAIN SCHEDULE",
            "Arrival station [POINT TrainStation]: [GRID A3].",
            "[POINT WPA] - 3.00km from station: T=10:06:50",
            "[POINT WPB] - 1.00km from station: T=10:10:10",
            "Arrival: T=10:16:50",
        });

        var message = TelegramSemanticParser.Parse("hq:iron-road-real", 10.0, text);

        AssertEx.Equal(1, message.Tracks.Count, "route-only telegram should still produce one route fact");
        AssertEx.Equal("route:hq:iron-road-real", message.Tracks[0].TrackId,
            "station/waypoint markers must not be mislabelled as the moving unit");
        AssertEx.Equal(2, message.Tracks[0].Route.Count, "route waypoints must be retained");
        AssertEx.Equal("A3", message.Tracks[0].ReportedGrid, "station grid remains visible route context");
    }

    public static void MissingMotionTextDoesNotCreateVelocity()
    {
        var message = TelegramSemanticParser.Parse(
            "hq:plain",
            5.0,
            "[POINT Artillery] spotted at [GRID B2]. Distance approximately 12 km.");

        AssertEx.Equal(1, message.Tracks.Count, "designation alone should still create a track");
        AssertEx.True(message.Tracks[0].Motion == null, "no explicit motion must remain unknown");
    }

    public static void HighTideTelegramPreservesFutureInterceptTime()
    {
        var text = string.Join("\n", new[]
        {
            "[POINT Vessel-7] SPOTTED:",
            "CROSSING [GRID H4 2:3] AT T:10:00:00.",
            "STEAMING AT A COURSE OF 180° AT 19.4 KNOTS.",
            "INTERCEPT [POINT Vessel-7] ON COURSE OR PREPARE FOR BEACH LANDING AT:",
            "T:10:08:20 5KM & 180° FROM [GRID H4 2:3]",
        });

        var message = TelegramSemanticParser.Parse("hq:high-tide", 100.0, text);

        var track = message.Tracks.Single();
        AssertEx.True(track.Motion != null, "high tide course/speed must be parsed");
        AssertEx.Equal(180.0, track.Motion!.CourseDegrees, "high tide course must be retained");
        AssertEx.Near(19.4 * 1.852 / 3600.0, track.Motion.SpeedKmPerSecond, 1e-12, "knots must convert");
        AssertEx.Equal(5.0, track.ReportedDistanceKm,
            "the explicit future intercept distance must remain available");
        AssertEx.Equal(10 * 3600.0 + 8 * 60.0 + 20.0, track.EstimatedArrivalSeconds,
            "future intercept time must remain available to a high-level planner");
    }

    public static void RouteTelegramCapturesRailBearingWithoutInventingSpeed()
    {
        var text = string.Join("\n", new[]
        {
            "[POINT TrainStation]: [GRID A3]",
            "Rail line runs straight. Bearing 270° from [POINT TrainStation].",
            "[POINT WPA] - 3.00km from station: T=10:06:50",
            "[POINT WPB] - 1.00km from station: T=10:10:10",
        });

        var message = TelegramSemanticParser.Parse("hq:rail-bearing", 10.0, text);

        var track = message.Tracks.Single();
        AssertEx.Equal(270.0, track.ReportedBearingDegrees, "visible rail bearing must be retained");
        AssertEx.True(track.Motion == null, "route timing alone must not be labelled as vessel speed");
    }

    public static void RouteTelegramAcceptsColonAndBareArrivalClock()
    {
        var text = string.Join("\n", new[]
        {
            "TRAIN SCHEDULE",
            "[POINT Train] APPROACH:",
            "[POINT WPA] - 3.00km from station: T:10:06:50",
            "Arrival 10:16:50",
        });

        var track = TelegramSemanticParser.Parse("hq:iron-road-colon", 10.0, text).Tracks.Single();
        AssertEx.Equal(10 * 3600.0 + 6 * 60.0 + 50.0, track.Route.Single().PassageTimeSeconds,
            "waypoint parser must accept T: clock notation");
        AssertEx.Equal(10 * 3600.0 + 16 * 60.0 + 50.0, track.EstimatedArrivalSeconds,
            "arrival parser must accept a bare visible clock");
    }

    public static void ChineseVesselTelegramParsesVisibleCourseAndSpeed()
    {
        var text = string.Join("\n", new[]
        {
            "[POINT Vessel-7]已发现：",
            "在 T:10:00:00 时经过 [GRID H4 2:3]。",
            "以19.4节速度航行180°航向。",
        });

        var message = TelegramSemanticParser.Parse("hq:high-tide-zh", 100.0, text);

        var track = message.Tracks.Single();
        AssertEx.True(track.Motion != null, "Chinese vessel text must expose motion facts");
        AssertEx.Equal(180.0, track.Motion!.CourseDegrees, "Chinese course must be normalized");
        AssertEx.Near(19.4 * 1.852 / 3600.0, track.Motion.SpeedKmPerSecond, 1e-12, "Chinese knots must convert");
    }

    public static void ChineseHighTideTelegramPreservesFutureTime()
    {
        var text = string.Join("\n", new[]
        {
            "[POINT Vessel-7]已发现：",
            "在 T:10:00:00 时经过 [GRID H4 2:3]。",
            "以19.4节速度航行180°航向。",
            "拦截航行中的[POINT Vessel-7]，或准备在以下地点应对滩头登陆：",
            "10:08:20 5千米，方位角180°。",
        });

        var message = TelegramSemanticParser.Parse("hq:high-tide-zh", 100.0, text);

        AssertEx.Equal(10 * 3600.0 + 8 * 60.0 + 20.0,
            message.Tracks.Single().EstimatedArrivalSeconds,
            "Chinese future landing time must remain available");
        AssertEx.Equal(5.0, message.Tracks.Single().ReportedDistanceKm,
            "Chinese future landing distance must remain available");
    }

    public static void UnresolvedLocalizationTemplateIsNotTreatedAsIntel()
    {
        var message = TelegramSemanticParser.Parse(
            "template:raw",
            1.0,
            "[POINT <{ID}>] SPOTTED at [GRID <{ID}>] T:[TIMER <MissionTime>].");

        AssertEx.Equal(0, message.Tracks.Count, "unresolved placeholders are not player-visible facts");
    }

    public static void TelegramClockDoesNotFreezeEpisodeAge()
    {
        var message = TelegramSemanticParser.Parse(
            "hq:clock-domain",
            120.0,
            "[POINT Vessel-7] at [GRID H4] T:10:00:00; course 180° at 10 knots.");
        var knowledge = new PlayerParityKnowledge();
        knowledge.Apply(message);

        AssertEx.True(knowledge.TryEstimateAt("Vessel-7", 600.0, out var estimate),
            "parsed telegram should create an estimable track");
        AssertEx.Equal(480.0, estimate!.AgeSeconds,
            "absolute telegram clock must not be subtracted from episode seconds");
    }

    public static void MovingTrackExtrapolatesFromLastLegalReport()
    {
        var provenance = new IntelProvenance(IntelSourceKind.Telegram, "hq:high-tide", 100.0, "visible course report");
        var track = new TrackIntel("Ship", provenance)
        {
            ReportedPosition = new MapPoint(0.0, 0.0),
            ReportedAtSeconds = 100.0,
            Motion = new MotionIntel(180.0, 0.010),
        };
        var knowledge = new PlayerParityKnowledge();
        AssertEx.True(knowledge.Apply(track), "legal report should be accepted");

        AssertEx.True(knowledge.TryEstimate("Ship", 600.0, out var estimate), "reported motion should be estimable");
        AssertEx.Near(0.0, estimate!.EstimatedPosition!.Value.XKm, 1e-12, "southbound track should not drift east/west");
        AssertEx.Near(-5.0, estimate.EstimatedPosition.Value.YKm, 1e-12, "southbound track should move five km");
        AssertEx.Equal(500.0, estimate.AgeSeconds, "estimate age must use report time");
        AssertEx.Near(0.55, estimate.UncertaintyKm, 1e-12, "uncertainty must grow with stale age");
    }

    public static void MovingTrackCanPredictAtFutureImpactTime()
    {
        var knowledge = new PlayerParityKnowledge();
        knowledge.Apply(new TrackIntel(
            "Ship",
            new IntelProvenance(IntelSourceKind.Telegram, "hq:future", 100.0, "visible report"))
        {
            ReportedPosition = new MapPoint(0.0, 0.0),
            ReportedAtSeconds = 100.0,
            Motion = new MotionIntel(90.0, 0.01),
        });

        // A policy can ask for the predicted position at fire time + shell TOF
        // without reading the live target after the report.
        AssertEx.True(knowledge.TryEstimateAt("Ship", 600.0, out var estimate),
            "future impact prediction should use the legal report");
        AssertEx.Near(5.0, estimate!.EstimatedPosition!.Value.XKm, 1e-12,
            "eastbound track should be projected to future impact time");
        AssertEx.Near(0.55, estimate.UncertaintyKm, 1e-12,
            "future projection uncertainty must use forecast age");
    }

    public static void BrainActionsAreSemanticAndStrictlyMasked()
    {
        var mask = new ActionMask(
            holdAllowed: true,
            engageableTrackIds: new[] { "Ship" },
            continueCurrentMissionAllowed: false,
            abortIfSafeAllowed: false);

        AssertEx.True(mask.Allows(new BrainAction(BrainActionKind.Hold)), "hold should be legal");
        AssertEx.True(mask.Allows(new BrainAction(BrainActionKind.Engage, "Ship")), "known target should be engageable");
        AssertEx.True(!mask.Allows(new BrainAction(BrainActionKind.Engage, "Hidden-Enemy")), "unknown target must be masked");
        AssertEx.True(!mask.Allows(new BrainAction(BrainActionKind.ContinueCurrentMission)), "disabled action must be masked");
        AssertEx.True(!mask.Allows(new BrainAction(BrainActionKind.AbortIfSafe)), "unsafe abort must be masked");
    }

    public static void ObservationContainsOnlyKnowledgeSnapshots()
    {
        var knowledge = new PlayerParityKnowledge();
        knowledge.Apply(new TrackIntel(
            "Visible-1",
            new IntelProvenance(IntelSourceKind.ScoutRecon, "scout:1", 10.0, "map reveal"))
        {
            ReportedGrid = "B2",
        });

        var observation = new BrainObservation(10.0, knowledge.Snapshot(), new ActionMask(
            holdAllowed: true,
            engageableTrackIds: new[] { "Visible-1" },
            continueCurrentMissionAllowed: true,
            abortIfSafeAllowed: false));

        AssertEx.Equal(1, observation.Tracks.Count, "observation should contain known track only");
        AssertEx.Equal("Visible-1", observation.Tracks[0].TrackId, "observation must preserve track identity");
        AssertEx.True(observation.ActionMask.Allows(new BrainAction(BrainActionKind.Engage, "Visible-1")), "observation must expose legal engage action");
    }

    public static void FacadeMasksBeforeCallingActuator()
    {
        var observation = new BrainObservation(10.0, Array.Empty<TrackSnapshot>(), new ActionMask(
            holdAllowed: true,
            engageableTrackIds: Array.Empty<string>(),
            continueCurrentMissionAllowed: false,
            abortIfSafeAllowed: false));
        var actuatorCalls = 0;
        var facade = new BrainFacade(observation, _ =>
        {
            actuatorCalls++;
            return new BrainExecutionResult(true, "accepted by test sink");
        });

        var rejected = facade.SubmitDecision(new BrainAction(BrainActionKind.Engage, "Hidden-Enemy"));
        AssertEx.True(!rejected.Accepted, "masked action must be rejected");
        AssertEx.Equal(0, actuatorCalls, "masked action must never reach actuator");

        var accepted = facade.SubmitDecision(new BrainAction(BrainActionKind.Hold));
        AssertEx.True(accepted.Accepted, "legal action should reach actuator");
        AssertEx.Equal(1, actuatorCalls, "legal action should be submitted exactly once");
        AssertEx.True(facade.LastTransition != null, "facade must retain an audit transition");
        AssertEx.Equal(BrainActionKind.Hold, facade.LastTransition!.Action.Kind, "audit must retain chosen action");
        AssertEx.Equal(2, facade.TransitionHistory.Count, "every decision, including a masked one, must be auditable");
    }

    public static void PlayerParitySessionIngestsTelegramAndResetsKnowledge()
    {
        var session = new PlayerParityBrainSession(
            initialGameTimeSeconds: 0.0,
            initialActionMask: new ActionMask(true, Array.Empty<string>(), false, false),
            executeAction: _ => new BrainExecutionResult(true, "test sink"));

        session.IngestTelegram(
            "hq:session",
            10.0,
            "[POINT Ship] crossing [GRID G6 9:1] at T:10:00:00; course 180° at 10 knots.");

        AssertEx.Equal(1, session.Knowledge.Count, "visible telegram should enter session knowledge");
        AssertEx.Equal(1, session.GetObservation().Tracks.Count, "observation should refresh after telegram");
        AssertEx.Equal(10.0, session.GetObservation().GameTimeSeconds, "telegram receipt time should advance context");

        session.ResetEpisode();

        AssertEx.Equal(0, session.Knowledge.Count, "episode reset must clear legal knowledge");
        AssertEx.Equal(0, session.GetObservation().Tracks.Count, "episode reset must hide prior tracks");
        AssertEx.True(!session.IsTerminal, "episode reset must clear terminal state");
    }

    public static void PlayerParitySessionAcceptsExplicitMapRevealPosition()
    {
        var session = new PlayerParityBrainSession(
            initialGameTimeSeconds: 0.0,
            initialActionMask: new ActionMask(true, Array.Empty<string>(), false, false),
            executeAction: _ => new BrainExecutionResult(true, "test sink"));

        AssertEx.True(session.IngestMapReveal(
                trackId: "Ship",
                sourceId: "map-marker:1",
                receivedAtSeconds: 20.0,
                position: new MapPoint(3.0, 4.0),
                evidence: "player-visible marker at G6 9:1",
                reportedGrid: "G6 9:1"),
            "explicit player-visible map position should be accepted");
        AssertEx.True(session.Knowledge.TryGet("Ship", out var snapshot), "map reveal should create a track");
        AssertEx.Equal(IntelSourceKind.MapReveal, snapshot!.LatestProvenance.Kind,
            "map reveal provenance must remain auditable");
        AssertEx.Equal(new MapPoint(3.0, 4.0), snapshot.ReportedPosition,
            "map reveal position must be preserved exactly");

        session.RefreshEngageableTracks();
        AssertEx.True(session.GetValidActions().Allows(new BrainAction(BrainActionKind.Engage, "Ship")),
            "only tracks with legal positions should become engageable");
    }

    public static void PlayerParitySessionCanMaskReservedTracks()
    {
        var session = new PlayerParityBrainSession(
            initialGameTimeSeconds: 0.0,
            initialActionMask: new ActionMask(true, Array.Empty<string>(), false, false),
            executeAction: _ => new BrainExecutionResult(true, "test sink"));

        session.IngestMapReveal(
            trackId: "Ship",
            sourceId: "map-marker:reserved",
            receivedAtSeconds: 5.0,
            position: new MapPoint(1.0, 2.0),
            evidence: "visible marker");
        session.RefreshEngageableTracks(5.0, new[] { "Ship" });

        AssertEx.True(!session.GetValidActions().Allows(new BrainAction(BrainActionKind.Engage, "Ship")),
            "a reserved track must be removed from the engage action mask");
    }

    public static void TransitionPublishesRewardAndTerminalState()
    {
        var observation = new BrainObservation(
            0.0,
            Array.Empty<TrackSnapshot>(),
            new ActionMask(true, Array.Empty<string>(), false, false));
        var facade = new BrainFacade(observation,
            _ => new BrainExecutionResult(true, "accepted"));

        facade.SubmitDecision(new BrainAction(BrainActionKind.Hold));
        var reward = new RewardBreakdown(DamageReward: 2.0, TargetDestroyedReward: 5.0);
        var nextObservation = new BrainObservation(
            1.0,
            Array.Empty<TrackSnapshot>(),
            observation.ActionMask);
        facade.PublishNextObservation(nextObservation, new[] { "shot-fired" }, reward, terminal: true);

        AssertEx.True(facade.IsTerminal, "terminal publication must close the episode");
        AssertEx.Equal(7.0, facade.LastTransition!.Reward.Total, "reward components must aggregate");
        AssertEx.Equal("shot-fired", facade.LastTransition.GameEvents.Single(), "game events must be audited");
        AssertEx.Equal(1.0, facade.TransitionHistory.Single().NextObservation!.GameTimeSeconds,
            "next observation must be part of the audited transition");
    }
}

public static class Program
{
    public static void Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            (nameof(BrainIntelTests.TelegramFactsAreAcceptedAndAuditable), BrainIntelTests.TelegramFactsAreAcceptedAndAuditable),
            (nameof(BrainIntelTests.OmniscientFactsAreRejectedBeforeStorage), BrainIntelTests.OmniscientFactsAreRejectedBeforeStorage),
            (nameof(BrainIntelTests.VesselTelegramProducesOnlyVisibleMotionFacts), BrainIntelTests.VesselTelegramProducesOnlyVisibleMotionFacts),
            (nameof(BrainIntelTests.RouteTelegramProducesWaypointSchedule), BrainIntelTests.RouteTelegramProducesWaypointSchedule),
            (nameof(BrainIntelTests.RouteTelegramDoesNotInventTrainFromDestinationPoint), BrainIntelTests.RouteTelegramDoesNotInventTrainFromDestinationPoint),
            (nameof(BrainIntelTests.MissingMotionTextDoesNotCreateVelocity), BrainIntelTests.MissingMotionTextDoesNotCreateVelocity),
            (nameof(BrainIntelTests.HighTideTelegramPreservesFutureInterceptTime), BrainIntelTests.HighTideTelegramPreservesFutureInterceptTime),
            (nameof(BrainIntelTests.RouteTelegramCapturesRailBearingWithoutInventingSpeed), BrainIntelTests.RouteTelegramCapturesRailBearingWithoutInventingSpeed),
            (nameof(BrainIntelTests.RouteTelegramAcceptsColonAndBareArrivalClock), BrainIntelTests.RouteTelegramAcceptsColonAndBareArrivalClock),
            (nameof(BrainIntelTests.ChineseVesselTelegramParsesVisibleCourseAndSpeed), BrainIntelTests.ChineseVesselTelegramParsesVisibleCourseAndSpeed),
            (nameof(BrainIntelTests.ChineseHighTideTelegramPreservesFutureTime), BrainIntelTests.ChineseHighTideTelegramPreservesFutureTime),
            (nameof(BrainIntelTests.UnresolvedLocalizationTemplateIsNotTreatedAsIntel), BrainIntelTests.UnresolvedLocalizationTemplateIsNotTreatedAsIntel),
            (nameof(BrainIntelTests.TelegramClockDoesNotFreezeEpisodeAge), BrainIntelTests.TelegramClockDoesNotFreezeEpisodeAge),
            (nameof(BrainIntelTests.MovingTrackExtrapolatesFromLastLegalReport), BrainIntelTests.MovingTrackExtrapolatesFromLastLegalReport),
            (nameof(BrainIntelTests.MovingTrackCanPredictAtFutureImpactTime), BrainIntelTests.MovingTrackCanPredictAtFutureImpactTime),
            (nameof(BrainIntelTests.BrainActionsAreSemanticAndStrictlyMasked), BrainIntelTests.BrainActionsAreSemanticAndStrictlyMasked),
            (nameof(BrainIntelTests.ObservationContainsOnlyKnowledgeSnapshots), BrainIntelTests.ObservationContainsOnlyKnowledgeSnapshots),
            (nameof(BrainIntelTests.FacadeMasksBeforeCallingActuator), BrainIntelTests.FacadeMasksBeforeCallingActuator),
            (nameof(BrainIntelTests.PlayerParitySessionIngestsTelegramAndResetsKnowledge), BrainIntelTests.PlayerParitySessionIngestsTelegramAndResetsKnowledge),
            (nameof(BrainIntelTests.PlayerParitySessionAcceptsExplicitMapRevealPosition), BrainIntelTests.PlayerParitySessionAcceptsExplicitMapRevealPosition),
            (nameof(BrainIntelTests.PlayerParitySessionCanMaskReservedTracks), BrainIntelTests.PlayerParitySessionCanMaskReservedTracks),
            (nameof(BrainIntelTests.TransitionPublishesRewardAndTerminalState), BrainIntelTests.TransitionPublishesRewardAndTerminalState),
        };

        foreach (var test in tests)
        {
            test.Run();
            Console.WriteLine($"PASS {test.Name}");
        }

        Console.WriteLine($"PASS {tests.Length} tests");
    }
}
