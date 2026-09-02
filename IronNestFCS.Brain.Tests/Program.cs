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
}

public static class Program
{
    public static void Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            (nameof(BrainIntelTests.TelegramFactsAreAcceptedAndAuditable), BrainIntelTests.TelegramFactsAreAcceptedAndAuditable),
            (nameof(BrainIntelTests.OmniscientFactsAreRejectedBeforeStorage), BrainIntelTests.OmniscientFactsAreRejectedBeforeStorage),
        };

        foreach (var test in tests)
        {
            test.Run();
            Console.WriteLine($"PASS {test.Name}");
        }

        Console.WriteLine($"PASS {tests.Length} tests");
    }
}
