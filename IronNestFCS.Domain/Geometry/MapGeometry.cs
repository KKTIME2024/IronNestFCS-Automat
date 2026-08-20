namespace IronNestFCS.Domain.Geometry;

public readonly record struct MapAffineTransform(
    float M11, float M12, float M13, float M14,
    float M21, float M22, float M23, float M24,
    float M31, float M32, float M33, float M34)
{
    public WorldPoint TransformPoint(WorldPoint p) => new(
        M11 * p.X + M12 * p.Y + M13 * p.Z + M14,
        M21 * p.X + M22 * p.Y + M23 * p.Z + M24,
        M31 * p.X + M32 * p.Y + M33 * p.Z + M34);
}

public readonly record struct BearingDistance(float BearingDeg, float DistanceKm);

public sealed record MapGeometry(MapAffineTransform WorldToMap, WorldPoint TurretMapLocal)
{
    public const float KmPerWorldUnit = 3.8164f;

    public static MapAffineTransform IdentityTransform => new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);

    public BearingDistance Locate(WorldPoint world)
    {
        var d = WorldToMap.TransformPoint(world) - TurretMapLocal;
        var angle = MathF.Atan2(d.X, d.Y) * (180f / MathF.PI);
        if (angle < 0f) angle += 360f;
        return new(angle, d.Length * KmPerWorldUnit);
    }
}
