using IronNestFCS.Domain.Geometry;
using IronNestFCS.Domain.Observation;
using IronNestFCS.Domain.Planning;
using IronNestFCS.Logic.FCS;
using UnityEngine;

namespace IronNestFCS.Logic.Planning;

/// <summary>适配结果: 有序任务列表, 或类型化拒绝。适配器内部绝不入队。</summary>
public sealed record AdapterResult(bool Accepted, PlanRejection Rejection, string? Detail, IReadOnlyList<ArtilleryTask> Tasks)
{
    public static AdapterResult Reject(PlanRejection rejection, string? detail) =>
        new(false, rejection, detail, Array.Empty<ArtilleryTask>());

    public static AdapterResult Ok(IReadOnlyList<ArtilleryTask> tasks) =>
        new(true, PlanRejection.None, null, tasks);
}

/// <summary>把校验通过的领域计划转换为现有 ArtilleryTask 对象(全有或全无)。
/// 不分配炮管、不登记注册表、不入队 —— 副作用全部由调用方在转换完成后执行。</summary>
public sealed class LegacySchedulerAdapter
{
    public AdapterResult TryConvert(EngagementPlan plan, long currentSnapshotVersion, float gameTime)
    {
        if (float.IsNaN(gameTime) || float.IsInfinity(gameTime))
            return AdapterResult.Reject(PlanRejection.None, "gameTime not finite");

        var validation = PlanContractValidator.Validate(plan, currentSnapshotVersion, legacyAdapter: true);
        if (!validation.Accepted)
            return AdapterResult.Reject(validation.Rejection, null);

        var tasks = new List<ArtilleryTask>(plan.Groups.Sum(g => g.Shots.Count));
        var nextTargetId = 1;
        foreach (var group in plan.Groups)
        {
            foreach (var shot in group.Shots)
            {
                var task = TryMapShot(shot, nextTargetId, out var detail);
                if (task is null)
                    return AdapterResult.Reject(PlanRejection.None, detail);
                tasks.Add(task);
                nextTargetId++;
            }
        }
        return AdapterResult.Ok(tasks);
    }

    private static ArtilleryTask? TryMapShot(ShotIntent shot, int targetId, out string? detail)
    {
        detail = null;
        var position = ToVector3(shot.ImpactPoint, out detail);
        if (position is null) return null;
        var aimP0 = ToVector3(shot.AimOrigin, out detail);
        if (aimP0 is null) return null;
        var aimVel = ToVector3(shot.AimVelocity, out detail);
        if (aimVel is null) return null;
        var clusterOffset = ToVector3(shot.ClusterOffset, out detail);
        if (clusterOffset is null) return null;
        if (!Finite(shot.BearingDeg, out detail)) return null;
        if (!Finite(shot.DistanceKm, out detail)) return null;
        if (!Finite(shot.AimStartTime, out detail)) return null;
        if (!Finite(shot.BlastRadiusKm, out detail)) return null;

        return new ArtilleryTask
        {
            targetId = targetId,
            entityId = shot.TargetEntityId,
            entityName = shot.DisplayName,
            angel = shot.BearingDeg,
            distance = shot.DistanceKm,
            position = position.Value,
            bulletType = MapShell(shot.Shell),
            useMaxCharge = shot.ChargePolicy == ChargePolicy.Maximum,
            Source = TaskSource.Auto,
            IsMoving = shot.IsMoving,
            VelocityUnknown = shot.VelocityUnknown,
            AimP0 = aimP0.Value,
            AimVel = aimVel.Value,
            AimStartTime = shot.AimStartTime,
            BlastRadiusKm = shot.BlastRadiusKm,
            // 移动集群按实体登记成员; 静态任务保持 master 的位置-only 注册(null)。
            ClusterMembers = shot.IsMoving && shot.ClusterMembers.Count > 0
                ? shot.ClusterMembers.ToList()
                : null,
            TrackEntityId = shot.TrackEntityId ?? "",
            ClusterOffset = clusterOffset.Value
        };
    }

    private static Vector3? ToVector3(WorldPoint p, out string? detail)
    {
        detail = null;
        if (!Finite(p.X, out detail) || !Finite(p.Y, out detail) || !Finite(p.Z, out detail))
            return null;
        return new Vector3(p.X, p.Y, p.Z);
    }

    private static bool Finite(float value, out string? detail)
    {
        detail = null;
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            detail = $"non-finite value {value}";
            return false;
        }
        return true;
    }

    private static BulletType MapShell(ShellKind kind) => kind switch
    {
        ShellKind.AP => BulletType.AP,
        ShellKind.DRIL => BulletType.DRIL,
        ShellKind.LE => BulletType.LE,
        ShellKind.HE => BulletType.HE,
        ShellKind.HCHE => BulletType.HCHE,
        ShellKind.STAR => BulletType.STAR,
        _ => throw new ArgumentException($"Shell not mappable to legacy: {kind}")
    };
}
