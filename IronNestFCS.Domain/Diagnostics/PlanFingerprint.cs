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

    private const float Tolerance = 0.0001f;

    public static string[] Compare(EngagementPlan expected, EngagementPlan actual)
    {
        var diff = new List<string>();
        if (expected.SnapshotVersion != actual.SnapshotVersion)
            diff.Add($"snapshotVersion:{expected.SnapshotVersion.ToString(CultureInfo.InvariantCulture)}!={actual.SnapshotVersion.ToString(CultureInfo.InvariantCulture)}");
        if (expected.NoPlanReason != actual.NoPlanReason)
            diff.Add($"noPlanReason:{expected.NoPlanReason}!={actual.NoPlanReason}");

        if (expected.Groups.Count != actual.Groups.Count)
            diff.Add($"groups:count:{expected.Groups.Count}!={actual.Groups.Count}");

        // 组顺序按索引处的射击目标验证(GroupId 是规划器内部标识, 不参与验收比较)。
        var groupCount = Math.Min(expected.Groups.Count, actual.Groups.Count);
        for (var i = 0; i < groupCount; i++)
        {
            var e = expected.Groups[i];
            var a = actual.Groups[i];
            if (e.TriggerMode != a.TriggerMode)
                diff.Add($"groups[{i}].triggerMode:{e.TriggerMode}!={a.TriggerMode}");

            if (e.Shots.Count != a.Shots.Count)
                diff.Add($"groups[{i}].shots:count:{e.Shots.Count}!={a.Shots.Count}");
            var shotCount = Math.Min(e.Shots.Count, a.Shots.Count);
            for (var j = 0; j < shotCount; j++)
                CompareShot(diff, i, j, e.Shots[j], a.Shots[j]);
        }
        return diff.ToArray();
    }

    private static void CompareShot(List<string> diff, int i, int j, ShotIntent e, ShotIntent a)
    {
        var p = $"groups[{i}].shots[{j}]";
        if (e.TargetEntityId != a.TargetEntityId)
            diff.Add($"{p}.target:{e.TargetEntityId}!={a.TargetEntityId}");
        if (!PointClose(e.ImpactPoint, a.ImpactPoint))
            diff.Add($"{p}.impact:{Point(e.ImpactPoint)}!={Point(a.ImpactPoint)}");
        if (!SameMemberSet(e.ClusterMembers, a.ClusterMembers))
            diff.Add($"{p}.members:{Members(e.ClusterMembers)}!={Members(a.ClusterMembers)}");
        if (e.Shell != a.Shell)
            diff.Add($"{p}.shell:{e.Shell}!={a.Shell}");
        if (e.ChargePolicy != a.ChargePolicy)
            diff.Add($"{p}.chargePolicy:{e.ChargePolicy}!={a.ChargePolicy}");
        if (e.IsMoving != a.IsMoving)
            diff.Add($"{p}.isMoving:{e.IsMoving}!={a.IsMoving}");
        if (e.VelocityUnknown != a.VelocityUnknown)
            diff.Add($"{p}.velocityKnown:{!e.VelocityUnknown}!={!a.VelocityUnknown}");
        if (!PointClose(e.AimOrigin, a.AimOrigin))
            diff.Add($"{p}.aimOrigin:{Point(e.AimOrigin)}!={Point(a.AimOrigin)}");
        if (!PointClose(e.AimVelocity, a.AimVelocity))
            diff.Add($"{p}.aimVelocity:{Point(e.AimVelocity)}!={Point(a.AimVelocity)}");
        if (Math.Abs(e.AimStartTime - a.AimStartTime) > Tolerance)
            diff.Add($"{p}.aimStartTime:{e.AimStartTime.ToString("F4", CultureInfo.InvariantCulture)}!={a.AimStartTime.ToString("F4", CultureInfo.InvariantCulture)}");
    }

    private static bool PointClose(Geometry.WorldPoint e, Geometry.WorldPoint a) =>
        Math.Abs(e.X - a.X) <= Tolerance && Math.Abs(e.Y - a.Y) <= Tolerance && Math.Abs(e.Z - a.Z) <= Tolerance;

    private static bool SameMemberSet(IReadOnlyList<string> e, IReadOnlyList<string> a)
    {
        if (e.Count != a.Count) return false;
        var set = new HashSet<string>(e, StringComparer.Ordinal);
        return set.SetEquals(a);
    }

    private static string Members(IReadOnlyList<string> members) =>
        members.Count == 0 ? "-" : string.Join("+", members.OrderBy(m => m, StringComparer.Ordinal));

    private static string Point(Geometry.WorldPoint p) =>
        $"{p.X.ToString("F4", CultureInfo.InvariantCulture)},{p.Y.ToString("F4", CultureInfo.InvariantCulture)},{p.Z.ToString("F4", CultureInfo.InvariantCulture)}";
}
