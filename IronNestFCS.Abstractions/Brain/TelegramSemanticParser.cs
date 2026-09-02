using System.Globalization;
using System.Text.RegularExpressions;

namespace IronNestFCS.Abstractions.Brain;

/// <summary>
/// 把玩家最终看到的电报文字转换为有限的语义事实。
/// 这个适配器没有 GameObject、MapEntity 或任务字典参数，因而不能从隐藏状态补字段。
/// </summary>
public static class TelegramSemanticParser
{
    private static readonly Regex PointRegex = new(
        @"\[POINT\s+(?:[<{])?(?<id>[A-Za-z0-9_:#\-\s]+?)(?:[>}]?)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GridRegex = new(
        @"\[GRID\s+(?<grid>[^\]\r\n]+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClockRegex = new(
        @"\bT(?:IMER)?\s*[:=]\s*(?<time>\d{1,2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MotionRegex = new(
        @"(?:\bcourse(?:\s+of)?\s+(?<course>\d+(?:[.,]\d+)?)\s*[°º]\s+at\s+(?<speed>\d+(?:[.,]\d+)?)\s+knots\b|(?<speed>\d+(?:[.,]\d+)?)\s*节[^\r\n]{0,24}?(?<course>\d+(?:[.,]\d+)?)\s*[°º]\s*航向)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BearingRegex = new(
        @"(?:\bbearing\b|\bazimuth\b|\bpeilung\b|方位角)\s*(?:is\s*)?(?<bearing>\d+(?:[.,]\d+)?)\s*[°º]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DistanceRegex = new(
        @"(?<![\w.])(?<distance>\d+(?:[.,]\d+)?)\s*(?:km\b|公里|千米)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WaypointRegex = new(
        @"\[POINT\s+(?:[<{])?(?<id>WP[A-Z]|Waypoint-[A-Za-z0-9-]+)(?:[>}]?)\].*?(?<distance>\d+(?:[.,]\d+)?)\s*km.*?\bT\s*[:=]\s*(?<time>\d{1,2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ArrivalRegex = new(
        @"(?im)^\s*(?:estimated\s+)?(?:arrival|到达|抵达|到站)\s*[:：]?[^\r\n]*?(?:\bT\s*[:=]\s*)?(?<time>\d{1,2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FutureEventRegex = new(
        @"(?is)(?:intercept|beach\s+landing|landing|拦截|登陆|滩头).{0,240}?(?:\bT\s*[:=]\s*)?(?<time>\d{1,2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析玩家可见文本。sourceId 仅用于审计，不用于推断未显示的事实。
    /// </summary>
    public static TelegramSemanticEvent Parse(string sourceId, double receivedAtSeconds, string visibleText)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(visibleText);

        var provenance = new IntelProvenance(
            IntelSourceKind.Telegram,
            sourceId,
            receivedAtSeconds,
            visibleText);

        var grid = GridRegex.Match(visibleText);
        var clock = ClockRegex.Match(visibleText);
        var motion = ParseMotion(visibleText);
        var route = ParseRoute(visibleText);
        var arrival = ParseArrival(visibleText) ?? ParseFutureEvent(visibleText);
        var bearing = ParseBearing(visibleText);
        var reportedDistance = motion != null ? ParseDistance(visibleText) : null;

        // POINT is overloaded by the mission text: stations and route waypoints
        // are visible map context, not the moving unit. Prefer an explicit unit
        // designation; for a route-only report retain the schedule under a
        // synthetic, auditable source-scoped id rather than inventing identity.
        var pointMatches = PointRegex.Matches(visibleText);
        var trackId = pointMatches
            .Select(x => Clean(x.Groups["id"].Value))
            .FirstOrDefault(x => !IsInfrastructurePoint(x));
        if (string.IsNullOrWhiteSpace(trackId)
            && route.Count == 0
            && arrival is null)
            return new TelegramSemanticEvent(sourceId, receivedAtSeconds, Array.Empty<TrackIntel>(), visibleText);
        trackId ??= $"route:{sourceId}";

        var track = new TrackIntel(trackId, provenance)
        {
            ReportedGrid = grid.Success ? Clean(grid.Groups["grid"].Value) : null,
            ReportedDistanceKm = reportedDistance,
            ReportedBearingDegrees = bearing,
            ReportedAtSeconds = clock.Success ? ParseClock(clock.Groups["time"].Value) : null,
            ReportedTimeBasis = clock.Success ? IntelTimeBasis.ClockOfDaySeconds : IntelTimeBasis.Unknown,
            Motion = motion,
            Route = route,
            EstimatedArrivalSeconds = arrival,
        };

        return new TelegramSemanticEvent(
            sourceId,
            receivedAtSeconds,
            new[] { track },
            visibleText);
    }

    private static MotionIntel? ParseMotion(string text)
    {
        var match = MotionRegex.Match(text);
        if (!match.Success
            || !TryParseNumber(match.Groups["course"].Value, out var course)
            || !TryParseNumber(match.Groups["speed"].Value, out var speedKnots)
            || speedKnots < 0.0)
            return null;

        course = NormalizeDegrees(course);

        // 1 knot = one nautical mile/hour; use the same physical conversion for offline and runtime logic.
        var speedKmPerSecond = speedKnots * 1.852 / 3600.0;
        return new MotionIntel(course, speedKmPerSecond);
    }

    private static IReadOnlyList<RouteWaypointIntel> ParseRoute(string text)
    {
        var route = new List<RouteWaypointIntel>();
        foreach (Match match in WaypointRegex.Matches(text))
        {
            if (!TryParseNumber(match.Groups["distance"].Value, out var distance)
                || !TryParseClock(match.Groups["time"].Value, out var passageTime))
                continue;

            route.Add(new RouteWaypointIntel(
                Clean(match.Groups["id"].Value),
                distance,
                passageTime));
        }

        return route;
    }

    private static double? ParseArrival(string text)
    {
        var matches = ArrivalRegex.Matches(text);
        if (matches.Count == 0 || !TryParseClock(matches[^1].Groups["time"].Value, out var seconds))
            return null;
        return seconds;
    }

    private static double? ParseFutureEvent(string text)
    {
        var matches = FutureEventRegex.Matches(text);
        if (matches.Count == 0
            || !TryParseClock(matches[^1].Groups["time"].Value, out var seconds))
            return null;
        return seconds;
    }

    private static double? ParseBearing(string text)
    {
        var match = BearingRegex.Match(text);
        return match.Success && TryParseNumber(match.Groups["bearing"].Value, out var bearing)
            ? NormalizeDegrees(bearing)
            : null;
    }

    private static double? ParseDistance(string text)
    {
        // Prefer the last explicit distance: moving-vessel telegrams commonly
        // end with the future intercept line (for example "5KM ...").
        var matches = DistanceRegex.Matches(text);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            if (TryParseNumber(matches[i].Groups["distance"].Value, out var distance)
                && distance >= 0.0)
                return distance;
        }

        return null;
    }

    private static double ParseClock(string value)
    {
        return TryParseClock(value, out var seconds) ? seconds : 0.0;
    }

    private static bool TryParseClock(string value, out double seconds)
    {
        if (TimeSpan.TryParseExact(value, @"h\:mm\:ss", CultureInfo.InvariantCulture, out var parsed)
            || TimeSpan.TryParseExact(value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out parsed))
        {
            seconds = parsed.TotalSeconds;
            return true;
        }

        seconds = 0.0;
        return false;
    }

    private static bool TryParseNumber(string value, out double number)
    {
        return double.TryParse(
            value.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static double NormalizeDegrees(double value)
    {
        value %= 360.0;
        return value < 0.0 ? value + 360.0 : value;
    }

    private static string Clean(string value)
    {
        return value.Trim().Trim('<', '>').Trim();
    }

    private static bool IsInfrastructurePoint(string id)
    {
        return id.Equals("TrainStation", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("WP", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("Waypoint-", StringComparison.OrdinalIgnoreCase);
    }
}
