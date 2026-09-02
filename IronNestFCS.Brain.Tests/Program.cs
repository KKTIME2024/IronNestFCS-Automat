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
            new IntelProvenance(IntelSourceKind.OmniscientDebug, "FireMission.Entities", 50.0, "hidden Position + Health"))
        {
            ReportedPosition = new MapPoint(99.0, 99.0),
            KnownHealth = 1,
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
        AssertEx.True(track.KnownHealth == null, "unmentioned health must stay unknown");
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

    public static void MissingMotionTextDoesNotCreateVelocity()
    {
        var message = TelegramSemanticParser.Parse(
            "hq:plain",
            5.0,
            "[POINT Artillery] spotted at [GRID B2]. Distance approximately 12 km.");

        AssertEx.Equal(1, message.Tracks.Count, "designation alone should still create a track");
        AssertEx.True(message.Tracks[0].Motion == null, "no explicit motion must remain unknown");
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
            (nameof(BrainIntelTests.MissingMotionTextDoesNotCreateVelocity), BrainIntelTests.MissingMotionTextDoesNotCreateVelocity),
        };

        foreach (var test in tests)
        {
            test.Run();
            Console.WriteLine($"PASS {test.Name}");
        }

        Console.WriteLine($"PASS {tests.Length} tests");
    }
}
