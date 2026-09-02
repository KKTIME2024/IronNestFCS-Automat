using IronNestFCS.Abstractions.Brain;

internal sealed record TelegramCorpusFixture(string SourceId, string VisibleText, bool ContainsTacticalSemantics = true);

/// <summary>
/// A small checked-in corpus copied from the game's visible mission templates
/// after replacing runtime placeholders with the values a player would see.
/// It intentionally includes status/order messages that the parser must not
/// turn into invented target facts.
/// </summary>
internal static class TelegramCorpus
{
    private static readonly IReadOnlyList<TelegramCorpusFixture> Fixtures = new[]
    {
        new TelegramCorpusFixture("asset:high-tide-4", string.Join("\n", new[]
        {
            "[POINT Vessel-7] SPOTTED:",
            "CROSSING [GRID H4 2:3] AT T:10:00:00.",
            "STEAMING AT A COURSE OF 180° AT 19.4 KNOTS.",
            "19.4 KNOTS = 3.00KM TRAVELED IN 5M.",
            "INTERCEPT [POINT Vessel-7] ON COURSE OR PREPARE FOR BEACH LANDING AT:",
            "T:10:08:20 5KM & 180° FROM [GRID H4 2:3]",
        })),
        new TelegramCorpusFixture("asset:high-tide-5", "[POINT Vessel-7] LANDED AT [GRID H4 2:3]. [TIMER 00:10:00]: MARINES DISEMBARKING."),
        new TelegramCorpusFixture("asset:high-tide-6", "[POINT Infantry-2] OVERRUN AND DESTROYED AT [GRID H5] BY [POINT Vessel-7]."),
        new TelegramCorpusFixture("asset:high-tide-2", "Initial Field Intelligence. This channel will send constant reports throughout the invasion. Target priority is yours to set."),
        new TelegramCorpusFixture("asset:high-tide-1", "Prevent enemy landing forces from reaching your Defensive Line until reinforcements arrive. First contact expected by T:00:03:00."),
        new TelegramCorpusFixture("asset:iron-road-1", "The train will reach Valle de Mula [POINT TrainStation] at approximately T=10:16:50. Intercept before arrival."),
        new TelegramCorpusFixture("asset:iron-road-2", string.Join("\n", new[]
        {
            "ARRIVAL STATION: Valle de Mula [POINT TrainStation]: [GRID A3]",
            "Estimated arrival: T=10:16:50",
            "TRACK ALIGNMENT: Rail line runs straight. Bearing 270° from [POINT TrainStation].",
            "[POINT WPA] - 3.00km from station: T=10:06:50",
            "[POINT WPB] - 1.00km from station: T=10:10:10",
            "[POINT WPC] - 0.50km from station: T=10:13:30",
        })),
        new TelegramCorpusFixture("asset:iron-road-6", string.Join("\n", new[]
        {
            "ARMOURED EMPLACEMENTS:",
            "[POINT Pillbox1]: Bearing 120° from [POINT AllySpotter1]. Distance 4.50 km from [POINT AllySpotter2].",
            "[POINT Pillbox2]: Bearing 135° from [POINT AllySpotter2]. Distance 5.25 km from [POINT AllySpotter1].",
        })),
        new TelegramCorpusFixture("asset:iron-road-5", "Two armoured emplacements block the assault trajectory. ORDNANCE: The armoured emplacements require AP Shells to destroy."),
        new TelegramCorpusFixture("asset:iron-road-15", "SIGNAL RECEIVED. COMMENCE ASSAULT! Counter-battery timer will commence once your first shell lands."),
        new TelegramCorpusFixture("asset:high-tide-3", "Gyro reconnaissance has been diverted to your position. Expect overflights as the landing develops."),
        new TelegramCorpusFixture("asset:high-tide-9", "[POINT Vessel-7] REPORTS ROUNDS ON TARGET. [POINT LandingCraft-2] DESTROYED."),
        new TelegramCorpusFixture("asset:iron-road-18", "TRAIN ARRIVED. - T:10:16:50 [DIST 0.00 km] FROM [POINT TrainStation]."),
        new TelegramCorpusFixture("fixture:motion-no-position", "[POINT Vessel-9] STEAMING AT A COURSE OF 90° AT 12.0 KNOTS."),
    };

    public static TelegramCorpusReport Analyze()
    {
        var results = Fixtures.Select(fixture =>
        {
            var message = TelegramSemanticParser.Parse(fixture.SourceId, 100.0, fixture.VisibleText);
            var hasStructuredFact = message.Tracks.Count > 0 && message.Tracks.Any(track =>
                track.ReportedGrid != null
                || track.ReportedPosition != null
                || track.ReportedDistanceKm.HasValue
                || track.ReportedBearingDegrees.HasValue
                || track.ReportedAtSeconds.HasValue
                || track.Motion != null
                || track.Route.Count > 0
                || track.EstimatedArrivalSeconds.HasValue);
            return (fixture, message, hasStructuredFact);
        }).ToArray();

        var fullyParsed = results.Count(x => x.hasStructuredFact && x.message.UnsupportedSemantics.Count == 0);
        var partiallyParsed = results.Count(x => x.hasStructuredFact && x.message.UnsupportedSemantics.Count > 0);
        var unsupported = results.Length - fullyParsed - partiallyParsed;
        return new TelegramCorpusReport(
            results.Length,
            results.Count(x => x.fixture.ContainsTacticalSemantics),
            fullyParsed,
            partiallyParsed,
            unsupported,
            results.Select(x => x.message).ToArray());
    }
}
