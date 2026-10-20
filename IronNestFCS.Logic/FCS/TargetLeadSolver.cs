using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 移动目标提前量解算: aim(t) 解析闭合式。匀速直线假设。
/// 纯数学, 无 IL2CPP 引用。
/// 单位: 位置=世界单位, 距离=km(×3.8164), 速度=世界单位/s(与雷达 TargetInfo.Velocity 一致)。
/// </summary>
public static class TargetLeadSolver
{
    /// <summary>移动速度阈值(km/s): 超过才启用提前量, 否则 aim 退化为静态。</summary>
    public const float MovingThresholdKmS = 0.001f;

    /// <summary>提前点: 匀速目标在 t 时刻的命中位置(t 相对快照参考时刻)。</summary>
    public static Vector3 LeadPoint(Vector3 p0, Vector3 velWorldPerSec, float t, float tof)
        => p0 + velWorldPerSec * (t + tof);

    /// <summary>装药覆盖: charge = ceil(dist_km × 0.2)（仰角≤60° 的闭合式）</summary>
    public static int ChargeFor(float distKm)
        => Mathf.CeilToInt(distKm * 0.2f);

    /// <summary>仰角公式: 0.012 × 距离(m) ÷ 装药</summary>
    public static float Elevation(float distKm, int charge)
        => 0.012f * distKm * 1000f / charge;

    /// <summary>速度是否算"移动"（世界单位/s ×3.8164 = km/s）</summary>
    public static bool IsMoving(Vector3 velWorldPerSec)
        => velWorldPerSec.magnitude * 3.8164f > MovingThresholdKmS;
}
