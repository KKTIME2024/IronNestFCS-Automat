using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IronNestFCS.Abstractions.Brain;

/// <summary>
/// Deterministic audit projection for the formal PlayerParity boundary.
///
/// This type accepts only Brain DTOs. It intentionally has no overload that
/// accepts a Unity object, a radar collection, or an internal entity handle.
/// The result is useful for regression tests and audit logs: a runtime adapter
/// can prove that changes to hidden game state did not alter what the policy
/// was allowed to see.
/// </summary>
public static class PlayerParityObservationProjection
{
    public static string Compute(BrainObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var builder = new StringBuilder();
        Append(builder, observation.GameTimeSeconds);
        foreach (var track in observation.Tracks.OrderBy(x => x.TrackId, StringComparer.Ordinal))
        {
            Append(builder, track.TrackId);
            Append(builder, track.LatestProvenance.Kind);
            Append(builder, track.LatestProvenance.SourceId);
            Append(builder, track.LatestProvenance.ReceivedAtSeconds);
            Append(builder, track.LatestProvenance.Evidence);
            Append(builder, track.DisplayName);
            Append(builder, track.ReportedGrid);
            Append(builder, track.ReportedPosition?.XKm);
            Append(builder, track.ReportedPosition?.YKm);
            Append(builder, track.ReportedDistanceKm);
            Append(builder, track.ReportedBearingDegrees);
            Append(builder, track.ReportedAtSeconds);
            Append(builder, track.ReportedTimeBasis);
            if (track.Motion is { } motion)
            {
                Append(builder, motion.CourseDegrees);
                Append(builder, motion.SpeedKmPerSecond);
            }
            else
            {
                Append(builder, "no-motion");
            }

            foreach (var waypoint in track.Route)
            {
                Append(builder, waypoint.WaypointId);
                Append(builder, waypoint.DistanceKm);
                Append(builder, waypoint.PassageTimeSeconds);
            }

            Append(builder, track.EstimatedArrivalSeconds);
            Append(builder, track.LastUpdatedAtSeconds);
            Append(builder, track.LifecycleState);
            Append(builder, track.DestroyedConfirmed);
            foreach (var conflict in track.Conflicts)
            {
                Append(builder, conflict.Field);
                Append(builder, conflict.PreviousValue);
                Append(builder, conflict.NewValue);
            }
        }

        Append(builder, observation.ActionMask.HoldAllowed);
        Append(builder, observation.ActionMask.ContinueCurrentMissionAllowed);
        Append(builder, observation.ActionMask.AbortIfSafeAllowed);
        foreach (var trackId in observation.ActionMask.EngageableTrackIds.OrderBy(x => x, StringComparer.Ordinal))
            Append(builder, trackId);

        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Append(StringBuilder builder, object? value)
    {
        builder.Append(value switch
        {
            null => "<null>",
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        });
        builder.Append('\u001f');
    }
}
