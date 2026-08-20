using System.Globalization;
using IronNestFCS.Domain.Planning;

namespace IronNestFCS.Domain.Diagnostics;

public static class PlanFingerprint
{
    public static string Format(EngagementPlan plan)
    {
        var parts = new List<string>();
        parts.Add($"v={plan.SnapshotVersion.ToString(CultureInfo.InvariantCulture)}");
        parts.Add($"reason={plan.NoPlanReason}");
        for (var i = 0; i < plan.Groups.Count; i++)
        {
            var group = plan.Groups[i];
            parts.Add($"g[{i}]={group.GroupId}:{group.TriggerMode}");
            for (var j = 0; j < group.Shots.Count; j++)
            {
                var s = group.Shots[j];
                parts.Add($"s[{i}.{j}]=" + string.Join(",",
                    s.TargetEntityId,
                    Point(s.ImpactPoint),
                    s.BearingDeg.ToString("F4", CultureInfo.InvariantCulture),
                    s.DistanceKm.ToString("F4", CultureInfo.InvariantCulture),
                    s.Shell,
                    s.ChargePolicy,
                    s.IsMoving ? "mv" : "st",
                    s.VelocityUnknown ? "vu" : "vk",
                    string.Join("+", s.ClusterMembers),
                    s.TrackEntityId ?? "-",
                    Point(s.ClusterOffset)));
            }
        }
        return string.Join("|", parts);
    }

    private static string Point(Geometry.WorldPoint p) =>
        $"{p.X.ToString("F4", CultureInfo.InvariantCulture)},{p.Y.ToString("F4", CultureInfo.InvariantCulture)},{p.Z.ToString("F4", CultureInfo.InvariantCulture)}";
}
