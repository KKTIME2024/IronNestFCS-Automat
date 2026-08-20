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
}
