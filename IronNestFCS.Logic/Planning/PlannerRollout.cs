namespace IronNestFCS.Logic.Planning;

/// <summary>自动规划模式: Baseline = BaselinePlanner 为唯一自动决策源;
/// PlanningDisabled = 紧急停用(不创建任何自动任务, 手动 FIFO 与在途/活动任务不受影响)。</summary>
internal enum PlannerAuthority { Baseline, PlanningDisabled }

/// <summary>规划器运行状态: 当前模式 + 最近一次无效扫描原因。
/// 模块初始化与每次热重载后默认 Baseline。仅 Logic 程序集内使用。</summary>
internal sealed class PlannerRollout
{
    public PlannerAuthority Authority { get; private set; } = PlannerAuthority.Baseline;
    public string? LastInvalidReason { get; private set; }

    public void SetAuthority(PlannerAuthority authority) => Authority = authority;

    /// <summary>Ctrl+Numpad7: Baseline ↔ PlanningDisabled 紧急停用开关。
    /// 只停自动规划: 不清队列、不取消活动/在飞任务, 手动 T1–T4 FIFO 照常。</summary>
    public void ToggleAuthority() =>
        Authority = Authority == PlannerAuthority.Baseline ? PlannerAuthority.PlanningDisabled : PlannerAuthority.Baseline;

    public void RecordInvalid(string reason) => LastInvalidReason = reason;
}
