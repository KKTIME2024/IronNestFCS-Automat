using System.Linq;
using IronNestFCS.Domain.Diagnostics;
using IronNestFCS.Domain.Observation;
using IronNestFCS.Domain.Planning;

namespace IronNestFCS.Logic.Planning;

/// <summary>影子比较结果: 是否一致、精确差异字段、双方指纹。纯数据, 无运行时引用。</summary>
public sealed record ShadowComparison(
    bool Match,
    string[] Diffs,
    string LegacyFingerprint,
    string NewFingerprint)
{
    public string ToLine(long scanVersion) =>
        $"[PLAN-SHADOW] scan={scanVersion}|match={(Match ? 1 : 0)}|legacy={LegacyFingerprint}|new={NewFingerprint}|diff={string.Join(";", Diffs)}";
}

/// <summary>纯比较: 旧路径决策(已映射为 ShotIntent) vs 领域规划器输出。
/// 不引用 FSC/TargetRegistry —— 结构上无法触碰调度器或硬件。</summary>
public static class ShadowPlanComparer
{
    public static ShadowComparison Compare(
        BattleSnapshot snapshot,
        IReadOnlyList<ShotIntent> legacyDispatched,
        EngagementPlan newPlan)
    {
        var legacyPlan = BuildPlan(snapshot.Version, DeriveNoPlanReason(snapshot, legacyDispatched), legacyDispatched);
        var diffs = PlanFingerprint.Compare(legacyPlan, newPlan);
        return new ShadowComparison(
            diffs.Length == 0,
            diffs,
            PlanFingerprint.Format(legacyPlan),
            PlanFingerprint.Format(newPlan));
    }

    public static EngagementPlan BuildPlan(long version, NoPlanReason reason, IReadOnlyList<ShotIntent> shots) =>
        new(version, PlanSource.Autonomous,
            shots.Select(s => new FireGroup($"legacy-{s.ShotId}", TriggerMode.Independent, new[] { s }, false)).ToArray(),
            Array.Empty<GroupRelation>(), reason);

    /// <summary>旧路径本周期未开火时, 按与领域规划器相同的检查顺序
    /// (手动队列 → 空闲炮管 → 可选目标)推导同一未开火原因, 使"都不开火"公平判等。</summary>
    private static NoPlanReason DeriveNoPlanReason(BattleSnapshot snapshot, IReadOnlyList<ShotIntent> legacyDispatched)
    {
        if (legacyDispatched.Count > 0) return NoPlanReason.None;
        if (snapshot.PendingManualTaskCount > 0) return NoPlanReason.ManualQueuePending;
        if (snapshot.LeftBarrel.HasActiveTask && snapshot.RightBarrel.HasActiveTask) return NoPlanReason.NoFreeBarrel;
        return NoPlanReason.NoEligibleTarget;
    }
}
