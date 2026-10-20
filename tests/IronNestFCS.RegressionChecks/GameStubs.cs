// Only geometry, clock and logging stand-ins for running the linked state logic without IL2CPP.
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public float magnitude => MathF.Sqrt(x * x + y * y + z * z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(Vector3 a, float value) => new(a.x * value, a.y * value, a.z * value);
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static float Distance(Vector3 a, Vector3 b) =>
            MathF.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));
    }
    public static class Mathf { public static int CeilToInt(float value) => (int)MathF.Ceiling(value); }
    public static class Time { public static float time { get; set; } }
}
namespace MelonLoader
{
    public static class MelonLogger { public static void Msg(string message) { } }
}
namespace IronNestFCS.Logic.FCS
{
    public enum BulletType { AP = 1, DRIL = 6, HCHE = 9, HE = 10, LE = 12, STAR = 17 }
}

namespace IronNestFCS.Logic
{
    public enum LeftRight { Left, Right }
}
