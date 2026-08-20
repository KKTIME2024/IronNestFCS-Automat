using System.Linq;
using IronNestFCS.Domain.Diagnostics;
using IronNestFCS.Domain.Geometry;
using IronNestFCS.Domain.Observation;
using IronNestFCS.Domain.Planning;
using IronNestFCS.Logic.FCS;
using UnityEngine;

namespace IronNestFCS.Logic.Planning;

/// <summary>记录权威旧路径决策而不改变它: 每个旧 ArtilleryTask 创建后立即映射为与
/// ShotIntent 同字段的比较表示, 供影子比较。不 enqueue/commit/release/修改任务。</summary>
public sealed class LegacyDecisionRecorder
{
    private readonly FSC _fcs;
    private readonly List<(ArtilleryTask Task, ShotIntent Intent)> _decisions = new();

    public LegacyDecisionRecorder(FSC fcs) => _fcs = fcs;

    public void Reset() => _decisions.Clear();

    public int Count => _decisions.Count;

    public void Record(ArtilleryTask task, IReadOnlyList<TacticalDecider.TargetInfo> scanned)
        => _decisions.Add((task, ToIntent(task, scanned)));

    /// <summary>本周期真正占位开火的决策(对象身份 == 当前炮槽任务)。
    /// 留在队列里的任务(如手动 FIFO 压队)不算本周期开火决策。</summary>
    public ShotIntent[] DispatchedDecisions(ArtilleryTask? left, ArtilleryTask? right) =>
        _decisions.Where(d => ReferenceEquals(d.Task, left) || ReferenceEquals(d.Task, right))
                  .Select(d => d.Intent).ToArray();

    /// <summary>每扫描一条不变式结构化行(不含 IL2CPP 对象 ToString 原文)。</summary>
    public string BuildGoldenLine(long scanVersion, bool valid, int freeBarrels, int manualCount, string? failure)
    {
        var plan = ShadowPlanComparer.BuildPlan(scanVersion, NoPlanReason.None,
            _decisions.Select(d => d.Intent).ToArray());
        return $"[PLAN-GOLDEN] scan={scanVersion}|valid={(valid ? 1 : 0)}|free={freeBarrels}|manual={manualCount}|legacy={PlanFingerprint.Format(plan)}|reason={failure ?? "none"}";
    }

    private ShotIntent ToIntent(ArtilleryTask task, IReadOnlyList<TacticalDecider.TargetInfo> scanned)
    {
        string[] members;
        if (task.entityId.Length > 0)
            members = Array.Empty<string>();
        else if (task.ClusterMembers is { Count: > 0 } m)
            members = m.ToArray();
        else
            members = DeriveStaticMembers(task, scanned).ToArray();

        return new ShotIntent(
            ShotId: $"legacy-{task.targetId}",
            TargetEntityId: task.entityId,
            DisplayName: task.entityName,
            ImpactPoint: ToWorldPoint(task.position),
            BearingDeg: task.angel,
            DistanceKm: task.distance,
            Shell: MapShell(task.bulletType),
            ChargePolicy: task.useMaxCharge ? ChargePolicy.Maximum : ChargePolicy.Minimum,
            IsMoving: task.IsMoving,
            VelocityUnknown: task.VelocityUnknown,
            AimOrigin: ToWorldPoint(task.AimP0),
            AimVelocity: ToWorldPoint(task.AimVel),
            AimStartTime: task.AimStartTime,
            BlastRadiusKm: task.BlastRadiusKm,
            ClusterMembers: members,
            TrackEntityId: task.TrackEntityId.Length > 0 ? task.TrackEntityId : null,
            ClusterOffset: ToWorldPoint(task.ClusterOffset));
    }

    /// <summary>静态集群任务(ClusterMembers=null): 用与领域规划器相同的规则
    /// (静态、软目标、未承包、落在爆区内)从成功扫描列表推导比较成员, 按实体 ID 排序。</summary>
    private List<string> DeriveStaticMembers(ArtilleryTask task, IReadOnlyList<TacticalDecider.TargetInfo> scanned)
    {
        var result = new List<string>();
        if (task.BlastRadiusKm <= 0f) return result;
        var blastWorld = task.BlastRadiusKm / ShellData.KmPerWorldUnit;
        var blastSq = blastWorld * blastWorld;
        foreach (var t in scanned)
        {
            if (t.IsArmored || t.IsUnderground || t.IsMoving) continue;
            if (_fcs.Registry.IsHandled(t.EntityId)) continue;
            if (_fcs.Registry.IsHandledNear(t.WorldPos, 0f)) continue;
            var d = t.WorldPos - task.position;
            if (d.sqrMagnitude <= blastSq) result.Add(t.EntityId);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static ShellKind MapShell(BulletType type) => type switch
    {
        BulletType.AP => ShellKind.AP,
        BulletType.DRIL => ShellKind.DRIL,
        BulletType.LE => ShellKind.LE,
        BulletType.HE => ShellKind.HE,
        BulletType.HCHE => ShellKind.HCHE,
        BulletType.STAR => ShellKind.STAR,
        _ => throw new ArgumentException($"Legacy shell not mappable to domain: {type}")
    };

    private static WorldPoint ToWorldPoint(Vector3 v) => new(v.x, v.y, v.z);
}
