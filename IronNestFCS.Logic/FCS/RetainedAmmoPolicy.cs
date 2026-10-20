namespace IronNestFCS.Logic.FCS;

/// <summary>自动推荐可迁就实物；手动指定弹种仍由执行器严格匹配。</summary>
public static class RetainedAmmoPolicy
{
    public static bool CanUse(BulletType shell, int charge, float distanceKm,
        bool armored, bool underground, ISet<string>? immuneShells)
    {
        // 仅使用已有常规弹种毁伤资料；不把照明、化学或特殊弹当作通用清膛弹。
        if (shell is not (BulletType.AP or BulletType.DRIL or BulletType.LE or BulletType.HE or BulletType.HCHE))
            return false;
        if (immuneShells?.Contains(shell.ToString()) == true) return false;
        if ((armored || underground) && shell != BulletType.AP) return false;
        return CanReach(distanceKm, charge);
    }

    public static bool CanReach(float distanceKm, int charge) =>
        float.IsFinite(distanceKm) && distanceKm > 0f && charge is >= 1 and <= 6
        // 装药决定可达射程；多装药不排除近目标，仰角和 ToF 始终按实际装药计算。
        && distanceKm * 0.2f <= charge;
}
