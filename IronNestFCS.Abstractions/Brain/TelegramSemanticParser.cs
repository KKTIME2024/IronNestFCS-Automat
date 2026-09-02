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
        @"\[POINT\s+<?(?<id>[A-Za-z0-9_:#-]+)>?\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GridRegex = new(
        @"\[GRID\s+(?<grid>[^\]\r\n]+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClockRegex = new(
        @"\bT(?:IMER)?\s*[:=]\s*(?<time>\d{1,2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MotionRegex = new(
        @"\bcourse(?:\s+of)?\s+(?<course>\d+(?:\.\d+)?)\s*[°º]\s+at\s+(?<speed>\d+(?:\.\d+)?)\s+knots\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WaypointRegex = new(
        @"\[POINT\s+<?(?<id>WP[A-Z]|Waypoint-[A-Za-z0-9-]+)>?\].*?(?<distance>\d+(?:\.\d+)?)\s*km.*?\bT\s*=\s*(?<time>\d{1,2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ArrivalRegex = new(
        @"(?im)^\s*(?:estimated\s+)?arrival\s*:?[^\r\n]*?\bT\s*=\s*(?<time>\d{1,2}:\d{2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex KnotRegex = new(
        @"(?<speed>\d+(?:\.\d+)?)\s+knots\s*=\s*(?<distance>\d+(?:\.\d+)?)\s*km\s+traveled\s+in\s+(?<duration>\d+(?:\.\d+)?)\s*s",
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

        var pointMatches = PointRegex.Matches(visibleText);
        var trackId = pointMatches.Count == 0 ? null : Clean(pointMatches[0].Groups["id"].Value);
        if (string.IsNullOrWhiteSpace(trackId))
            return new TelegramSemanticEvent(sourceId, receivedAtSeconds, Array.Empty<TrackIntel>(), visibleText);

        var grid = GridRegex.Match(visibleText);
        var clock = ClockRegex.Match(visibleText);
        var motion = ParseMotion(visibleText);
        var route = ParseRoute(visibleText);
        var arrival = ParseArrival(visibleText);

        var track = new TrackIntel(trackId, provenance)
        {
            ReportedGrid = grid.Success ? Clean(grid.Groups["grid"].Value) : null,
            ReportedAtSeconds = clock.Success ? ParseClock(clock.Groups["time"].Value) : null,
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
            || !double.TryParse(match.Groups["course"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var course)
            || !double.TryParse(match.Groups["speed"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speedKnots)
            || speedKnots < 0.0)
            return null;

        course %= 360.0;
        if (course < 0.0) course += 360.0;

        // 1 knot = one nautical mile/hour; use the same physical conversion for offline and runtime logic.
        var speedKmPerSecond = speedKnots * 1.852 / 3600.0;
        return new MotionIntel(course, speedKmPerSecond);
    }

    private static IReadOnlyList<RouteWaypointIntel> ParseRoute(string text)
    {
        var route = new List<RouteWaypointIntel>();
        foreach (Match match in WaypointRegex.Matches(text))
        {
            if (!double.TryParse(match.Groups["distance"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance)
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

    private static string Clean(string value)
    {
        return value.Trim().Trim('<', '>').Trim();
    }
}
