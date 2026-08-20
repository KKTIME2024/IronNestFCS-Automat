using IronNestFCS.Domain.Geometry;

namespace IronNestFCS.Domain.Planning;

public readonly record struct ClusterResult(WorldPoint Impact, int Count, float RadiusKm);

public static class ClusterSolver
{
    private const int MaxCandidates = 8;

    public static ClusterResult? Best(
        WorldPoint leader, IReadOnlyList<WorldPoint> candidates, float blastRadiusKm,
        IReadOnlyList<WorldPoint> friendlies, float friendlySafeKm)
    {
        var r = blastRadiusKm / ShellRules.KmPerWorldUnit;
        var rFriendly = friendlySafeKm / ShellRules.KmPerWorldUnit;

        var cand = new List<WorldPoint> { leader };
        foreach (var p in candidates)
            if (WorldPoint.DistanceSquared(p, leader) <= 4f * r * r)
                cand.Add(p);
        if (cand.Count < 2) return null;
        if (cand.Count > MaxCandidates)
        {
            cand.Sort((a, b) => WorldPoint.DistanceSquared(a, leader).CompareTo(WorldPoint.DistanceSquared(b, leader)));
            cand.RemoveRange(MaxCandidates, cand.Count - MaxCandidates);
        }

        var n = cand.Count;
        var bestCount = 1;
        WorldPoint? bestImpact = null;
        var bestRadiusKm = 0f;
        var subsetCount = 1 << n;
        for (var mask = 1; mask < subsetCount; mask++)
        {
            if ((mask & 1) == 0) continue;
            var set = new List<WorldPoint>();
            for (var i = 0; i < n; i++)
                if ((mask & (1 << i)) != 0) set.Add(cand[i]);
            var count = set.Count;
            if (count <= bestCount) continue;
            if (!TryMinEnclosingCircle(set, out var center, out var radius)) continue;
            if (radius > r + 1e-4f) continue;
            if (HasFriendlyNear(center, rFriendly, friendlies)) continue;
            bestCount = count;
            bestImpact = center;
            bestRadiusKm = radius * ShellRules.KmPerWorldUnit;
        }

        return bestCount >= 2 && bestImpact is not null
            ? new ClusterResult(bestImpact.Value, bestCount, bestRadiusKm)
            : null;
    }

    private static bool HasFriendlyNear(WorldPoint center, float r, IReadOnlyList<WorldPoint> friendlies)
    {
        foreach (var f in friendlies)
            if (WorldPoint.DistanceSquared(f, center) <= r * r) return true;
        return false;
    }

    private static bool TryMinEnclosingCircle(List<WorldPoint> pts, out WorldPoint center, out float radius)
    {
        center = pts[0];
        radius = float.MaxValue;
        var n = pts.Count;

        for (var i = 0; i < n; i++)
            if (ContainsAll(pts, pts[i], 0f, radius)) { center = pts[i]; radius = 0f; }

        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                var c = (pts[i] + pts[j]) * 0.5f;
                var r = WorldPoint.Distance(pts[i], pts[j]) * 0.5f;
                if (ContainsAll(pts, c, r, radius)) { center = c; radius = r; }
            }

        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                for (var k = j + 1; k < n; k++)
                    if (Circumcenter(pts[i], pts[j], pts[k], out var c, out var r))
                        if (ContainsAll(pts, c, r, radius)) { center = c; radius = r; }

        return radius < float.MaxValue;
    }

    private static bool ContainsAll(List<WorldPoint> pts, WorldPoint c, float r, float bestR)
    {
        if (r >= bestR) return false;
        var tol = r + 1e-4f;
        var tolSq = tol * tol;
        foreach (var p in pts)
            if (WorldPoint.DistanceSquared(p, c) > tolSq) return false;
        return true;
    }

    private static bool Circumcenter(WorldPoint a, WorldPoint b, WorldPoint c, out WorldPoint center, out float radius)
    {
        center = WorldPoint.Zero;
        radius = 0f;
        var A = b - a;
        var B = c - a;
        var N = WorldPoint.Cross(A, B);
        var n2 = N.LengthSquared;
        if (n2 < 1e-8f) return false;
        var crossA = WorldPoint.Cross(B, N);
        var crossB = WorldPoint.Cross(N, A);
        center = a + (WorldPoint.Dot(A, A) * crossA + WorldPoint.Dot(B, B) * crossB) / (2f * n2);
        radius = WorldPoint.Distance(center, a);
        return true;
    }
}
