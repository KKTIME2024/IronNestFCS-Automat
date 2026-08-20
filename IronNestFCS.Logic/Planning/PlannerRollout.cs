namespace IronNestFCS.Logic.Planning;

internal enum PlannerAuthority { Legacy, Baseline }

/// <summary>规划器推进状态: 权威模式 + 影子匹配/失配计数 + 回退/无效原因。
/// 模块初始化与每次热重载后默认 Legacy。仅 Logic 程序集内使用。</summary>
internal sealed class PlannerRollout
{
    public PlannerAuthority Authority { get; private set; } = PlannerAuthority.Legacy;

    public int ShadowMatches { get; private set; }
    public int ShadowMismatches { get; private set; }
    public string? LastMismatchDetail { get; private set; }
    public string? LastFallbackReason { get; private set; }
    public string? LastInvalidReason { get; private set; }

    public void SetAuthority(PlannerAuthority authority) => Authority = authority;

    public void RecordShadow(bool match, string? diff)
    {
        if (match)
        {
            ShadowMatches++;
        }
        else
        {
            ShadowMismatches++;
            LastMismatchDetail = diff;
        }
    }

    public void RecordFallback(string reason) => LastFallbackReason = reason;

    public void RecordInvalid(string reason) => LastInvalidReason = reason;

    private (int QueueCount, object? LeftTask, object? RightTask)? _shadowGuard;

    /// <summary>影子计算前快照只读基线: 队列长度 + 双炮槽对象身份。</summary>
    public void BeginShadowGuard(int queueCount, object? leftTask, object? rightTask) =>
        _shadowGuard = (queueCount, leftTask, rightTask);

    /// <summary>校验影子窗口未触碰调度器; 任一值变化 → 强制 legacy-only 并返回 false。</summary>
    public bool AssertShadowReadOnly(int queueCount, object? leftTask, object? rightTask)
    {
        var guard = _shadowGuard;
        _shadowGuard = null;
        if (guard is null) return true;
        var ok = guard.Value.QueueCount == queueCount
            && ReferenceEquals(guard.Value.LeftTask, leftTask)
            && ReferenceEquals(guard.Value.RightTask, rightTask);
        if (!ok)
        {
            Authority = PlannerAuthority.Legacy;
            LastFallbackReason = "shadow-read-only-violation";
        }
        return ok;
    }
}
