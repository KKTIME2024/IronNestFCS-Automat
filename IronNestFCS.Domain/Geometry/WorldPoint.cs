namespace IronNestFCS.Domain.Geometry;

public readonly record struct WorldPoint(float X, float Y, float Z)
{
    public static WorldPoint Zero => new(0f, 0f, 0f);

    public static WorldPoint operator +(WorldPoint a, WorldPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static WorldPoint operator -(WorldPoint a, WorldPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static WorldPoint operator *(WorldPoint a, float k) => new(a.X * k, a.Y * k, a.Z * k);

    public static WorldPoint operator *(float k, WorldPoint a) => new(a.X * k, a.Y * k, a.Z * k);

    public static WorldPoint operator /(WorldPoint a, float k) => new(a.X / k, a.Y / k, a.Z / k);

    public float LengthSquared => X * X + Y * Y + Z * Z;

    public float Length => MathF.Sqrt(LengthSquared);

    public static float Distance(WorldPoint a, WorldPoint b) => (a - b).Length;

    public static float DistanceSquared(WorldPoint a, WorldPoint b) => (a - b).LengthSquared;

    public static float Dot(WorldPoint a, WorldPoint b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static WorldPoint Cross(WorldPoint a, WorldPoint b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
