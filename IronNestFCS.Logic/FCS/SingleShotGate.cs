namespace IronNestFCS.Logic.FCS;

/// <summary>全局扳机的单发提交边界；回调之间不让出主线程。</summary>
public static class SingleShotGate
{
    public static bool TrySubmit(bool ownerReady, bool otherKnown, bool otherArmed,
        Func<bool> authorized, Action recordSubmission, Action trigger)
    {
        if (!ownerReady || !otherKnown || otherArmed || !authorized()) return false;
        // 先记提交，再调用可能抛异常的原作接口；异常不能当作“肯定没发”。
        recordSubmission();
        trigger();
        return true;
    }
}
