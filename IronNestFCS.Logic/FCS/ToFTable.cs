namespace IronNestFCS.Logic.FCS;

/// <summary>实测弹道飞行时间：固定装药下，飞行时间与射程近似线性。</summary>
public static class ToFTable
{
    private static readonly float[] SecondsPerKm =
    {
        4.758869f,
        3.830061f,
        2.613011f,
        1.894451f,
        1.540442f,
        1.427168f,
    };

    /// <summary>按射程和装药档估计飞行时间；charge 越界时沿用旧行为夹到 1..6。</summary>
    public static float FlightTime(float distanceKm, int charge) =>
        Math.Max(0f, distanceKm) * SecondsPerKm[Math.Clamp(charge, 1, 6) - 1];
}
