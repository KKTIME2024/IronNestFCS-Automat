using IronNestFCS.Domain.Geometry;
using IronNestFCS.Domain.Observation;
using IronNestFCS.Domain.Planning;
using IronNestFCS.Logic.FCS;
using UnityEngine;

namespace IronNestFCS.Logic.Planning;

/// <summary>一次观测捕获: 当前快照(可能无效) + 最近一次可信快照(可能没有)。</summary>
public sealed record ObservationCapture(BattleSnapshot Current, BattleSnapshot? LastTrusted);

/// <summary>唯一把当前运行时状态转成领域快照的入口。
/// 全部读取在 Unity 主线程; 对游戏零交互(不 CleanState/RegressState/退壳/入队/登记)。
/// 雷达扫描失败 → 当前快照 Invalid 且不带目标, LastTrusted 保持不变; 两者都不得用于规划。</summary>
public sealed class ObservationAdapter
{
    private readonly FSC _fcs;
    private readonly TacticalRadar _radar;
    private BattleSnapshot? _lastTrusted;

    public ObservationAdapter(FSC fcs, TacticalRadar radar)
    {
        _fcs = fcs;
        _radar = radar;
    }

    public ObservationCapture Capture()
    {
        var gameTime = Time.time;
        var scan = _radar.Scan();

        string? failure = null;
        var targets = Array.Empty<TargetSnapshot>();
        var friendlies = Array.Empty<WorldPoint>();
        var geometry = FallbackGeometry();

        if (!scan.Valid)
        {
            failure = scan.Failure;
        }
        else
        {
            var geo = CaptureGeometry();
            if (geo is null)
            {
                failure = "MapSurfaceMissing";
            }
            else
            {
                geometry = geo;
                targets = MapTargets();
                friendlies = _radar.AllyPositions.Select(ToWorldPoint).ToArray();
            }
        }

        var snapshot = new BattleSnapshot(
            scan.Version, gameTime,
            failure is null ? ObservationStatus.Valid : ObservationStatus.Invalid,
            failure,
            _fcs.Turret.LastSetAngle, geometry,
            ObserveBarrel(BarrelId.Left, _fcs.LeftGun, _fcs.LeftTask, scan.Version),
            ObserveBarrel(BarrelId.Right, _fcs.RightGun, _fcs.RightTask, scan.Version),
            targets,
            friendlies,
            CountPendingManual(), false);

        if (failure is null) _lastTrusted = snapshot;
        return new ObservationCapture(snapshot, _lastTrusted);
    }

    private TargetSnapshot[] MapTargets()
    {
        var list = new List<TargetSnapshot>(_radar.ObservedHostiles.Count);
        foreach (var t in _radar.ObservedHostiles)
        {
            var immune = new List<ShellKind>();
            foreach (var name in t.ImmuneShells ?? new HashSet<string>())
                if (MapShellKind(name) is { } kind) immune.Add(kind);
            list.Add(new TargetSnapshot(
                EntityId: t.EntityId,
                Name: t.Name,
                Position: ToWorldPoint(t.WorldPos),
                BearingDeg: t.Angle,
                DistanceKm: t.Distance,
                Priority: t.Priority,
                Stars: t.Stars,
                IsArmored: t.IsArmored,
                IsUnderground: t.IsUnderground,
                IsFdc: t.IsFdc,
                IsMoving: t.IsMoving,
                VelocityKnown: t.VelocityKnown,
                VelocityWorldPerSecond: ToWorldPoint(t.Velocity),
                ImmuneShells: immune.ToArray(),
                IsEntityReserved: _fcs.Registry.IsHandled(t.EntityId),
                IsPositionReserved: _fcs.Registry.IsHandledNear(t.WorldPos, 0f)));
        }
        return list.ToArray();
    }

    /// <summary>地图几何: 与雷达同源的 Draggable Surface + 真实炮塔基准。
    /// 缺失时返回 null(观测按 Invalid 处理, 不产出退化几何)。</summary>
    private MapGeometry? CaptureGeometry()
    {
        var surface = GameObject.Find("Draggable Surface")?.transform;
        var turret = _fcs.MapTable.TurretLocation ?? _fcs.MapTable.Turret;
        if (surface == null || turret == null) return null;
        var m = surface.worldToLocalMatrix;
        var transform = new MapAffineTransform(
            m.m00, m.m01, m.m02, m.m03,
            m.m10, m.m11, m.m12, m.m13,
            m.m20, m.m21, m.m22, m.m23);
        var local = m.MultiplyPoint3x4(turret.position);
        return new MapGeometry(transform, ToWorldPoint(local));
    }

    private static MapGeometry FallbackGeometry() => new(MapGeometry.IdentityTransform, WorldPoint.Zero);

    private static BarrelSnapshot ObserveBarrel(BarrelId id, GunSystem gun, ArtilleryTask? activeTask, long stateVersion)
    {
        if (!gun.ObservationBound)
        {
            return new BarrelSnapshot(id, stateVersion, BarrelMechanicalState.Unknown,
                null, null, null, null, null, null,
                activeTask != null, LeaseId(id, activeTask));
        }

        var pending = gun.ObservePendingReload();
        var working = gun.ObserveReloadWorking();
        var canFire = gun.ObserveCanFire();
        var shell = MapShellKind(gun.BulletInChamber() ?? "");
        var rawCharges = gun.ChamberCharges();
        var charges = rawCharges < 0 ? (int?)null : rawCharges;

        var state = pending == true
            ? BarrelMechanicalState.Recovering
            : working == true
                ? BarrelMechanicalState.Transitioning
                : canFire == true
                    ? BarrelMechanicalState.Committed
                    : shell is not null
                        ? BarrelMechanicalState.ShellLoaded
                        : BarrelMechanicalState.Empty;

        return new BarrelSnapshot(id, stateVersion, state,
            shell, charges, canFire, pending, gun.ObserveBreechLocked(), null,
            activeTask != null, LeaseId(id, activeTask));
    }

    private static string? LeaseId(BarrelId id, ArtilleryTask? task) =>
        task is null ? null : $"legacy:{(id == BarrelId.Left ? "left" : "right")}:{task.targetId}";

    private int CountPendingManual()
    {
        var count = 0;
        foreach (var t in _fcs.QueueCan)
            if (t.Source == TaskSource.Manual) count++;
        return count;
    }

    private static ShellKind? MapShellKind(string name) => name switch
    {
        "AP" => ShellKind.AP,
        "DRIL" => ShellKind.DRIL,
        "LE" => ShellKind.LE,
        "HE" => ShellKind.HE,
        "HCHE" => ShellKind.HCHE,
        "STAR" => ShellKind.STAR,
        _ => null
    };

    private static WorldPoint ToWorldPoint(Vector3 v) => new(v.x, v.y, v.z);
}
