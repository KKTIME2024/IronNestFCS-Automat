using System.Linq;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>任务来源: 雷达自动 or 玩家手动(手动任务不被自动清队列清掉)</summary>
public enum TaskSource { Auto, Manual }

/// <summary>
/// 人机协同注册表: 统一管理"某个目标已被谁承包"。
/// 手动/自动任务共用: 入队或派发时登记, 击杀确认(Reconcile)或窗口到期解除。
/// 纯数据模块, 无 IL2CPP 引用, 热重载安全(Dispose 时 Clear)。
/// </summary>
public sealed class TargetRegistry
{
    /// <summary>击发后在飞窗口: 覆盖至多 ~60s 飞行 + 余量。打中由 Reconcile 提前解除。</summary>
    public const float FlightWindow = 65f;
    /// <summary>手动标记解析半径(km): 标记世界坐标附近该距离内的存活敌目标视为手动开火目标</summary>
    public const float ManualResolveMaxDistance = 1.0f;
    /// <summary>位置提交默认覆盖半径(世界单位, 手动解析失败的位置任务)。集群/爆区任务用所选弹毁伤半径。</summary>
    private const float DefaultCommitRadiusWorld = 0.3f;

    private readonly Dictionary<string, Entry> _byEntity = new();
    private readonly List<Entry> _positions = new();

    private sealed class Entry
    {
        public readonly ArtilleryTask Owner;
        public readonly string? EntityId;
        public readonly Vector3 Pos;
        public bool Fired;
        public float FiredAt;
        /// <summary>覆盖半径(世界单位)。集群/爆区任务 = 毁伤半径; 手动位置任务 = 默认。</summary>
        public float Radius = DefaultCommitRadiusWorld;

        public Entry(ArtilleryTask owner, string? entityId, Vector3 pos)
        {
            Owner = owner;
            EntityId = entityId;
            Pos = pos;
        }

        public bool IsExpired => (Fired && Time.time - FiredAt > FlightWindow)
            || (Owner.FireResultUnknown
                && Owner.FireResultUnknownAt > 0f
                && Time.time - Owner.FireResultUnknownAt > FlightWindow);
    }

    /// <summary>登记目标。自动任务在派发时调用; 手动任务在入队时调用(队列存活不被清)。幂等。
    /// 集群任务派发即登记覆盖成员——装填期+在飞期全程实体屏蔽: 移动成员会移出落点覆盖半径,
    /// 几何屏蔽(IsHandledNear)拦不住, 不按实体登记会在装填期被雷达重复派发(同目标双任务)。</summary>
    public void Commit(ArtilleryTask task)
    {
        // “幂等”也要清掉同一任务旧的几何条目；暂停后重新取位时，
        // 不能让旧位置和新位置同时继续屏蔽雷达。已出膛记录不再重建。
        if (!task.Fired && !task.FireResultUnknown)
            Release(task);
        if (task.entityId is { Length: > 0 } id)
            _byEntity[id] = new Entry(task, id, task.position);
        else
        {
            var e = new Entry(task, null, task.position);
            // 集群任务从派发起就带毁伤半径覆盖(装填期防重复起簇/点射); 手动位置任务用默认
            e.Radius = task.BlastRadiusKm > 0f ? ShellData.KmToWorld(task.BlastRadiusKm) : DefaultCommitRadiusWorld;
            _positions.Add(e);
        }
        if (task.ClusterMembers is { Count: > 0 }) CommitMembers(task, task.ClusterMembers);
    }

    /// <summary>击发后调用: 启动飞行窗口计时。</summary>
    public void MarkFired(ArtilleryTask task)
    {
        foreach (var e in _byEntity.Values.Concat(_positions))
        {
            if (e.Owner != task) continue;
            e.Fired = true;
            e.FiredAt = Time.time;
        }
    }

    /// <summary>按实体登记移动集群覆盖成员(派发时由 Commit 调用): 在飞屏蔽与爆区几何无关
    /// (列车在落点后方, 几何半径兜不住)。死亡由 Reconcile 解除, 任务结束由 Release 一并清理。幂等。</summary>
    public void CommitMembers(ArtilleryTask task, IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (!_byEntity.ContainsKey(id))
                _byEntity[id] = new Entry(task, id, task.position);
    }

    /// <summary>
    /// 击发时注册爆区覆盖: 落点 + 毁伤半径(世界单位), IsHandledNear 覆盖范围内所有目标
    /// （单点 HE 也会连带炸掉邻居 → 邻居不再被重复开火）。单点 entityId 任务新增落点条目;
    /// 位置任务(集群/手动)更新现有条目半径。顺带启动飞行窗口。
    /// </summary>
    public void MarkFiredBlast(ArtilleryTask task, Vector3 impactPos, float blastRadiusKm)
    {
        float rWorld = blastRadiusKm > 0f ? ShellData.KmToWorld(blastRadiusKm) : DefaultCommitRadiusWorld;
        Entry? pos = null;
        foreach (var e in _positions)
            if (e.Owner == task) { pos = e; break; }
        if (pos == null)
        {
            pos = new Entry(task, null, impactPos) { Radius = rWorld };
            _positions.Add(pos);
        }
        else pos.Radius = rWorld;
        pos.Fired = true;
        pos.FiredAt = Time.time;
        MarkFired(task);   // 同步 entityId 条目(单点任务), 幂等
    }

    /// <summary>任务结束(未击发)时解除登记。幂等。
    /// 任务可能登记多个条目(entityId 单点 / 集群位置 / 集群成员)——全部解除,
    /// 否则成员条目 Fired=false 永不过期 → 任务结束后成员被永久屏蔽。</summary>
    public void Release(ArtilleryTask task)
    {
        foreach (var key in _byEntity.Where(kv => kv.Value.Owner == task).Select(kv => kv.Key).ToList())
            _byEntity.Remove(key);
        _positions.RemoveAll(p => p.Owner == task);
    }

    /// <summary>该目标是否已被承包(在飞/排队/炮上)。过期条目懒清理。</summary>
    public bool IsHandled(string entityId)
    {
        if (!_byEntity.TryGetValue(entityId, out var e)) return false;
        if (e.IsExpired) { _byEntity.Remove(entityId); return false; }
        return true;
    }

    /// <summary>该位置附近(含提交半径)是否已有位置提交。过期条目懒清理。</summary>
    public bool IsHandledNear(Vector3 pos, float radius)
    {
        foreach (var e in _positions)
        {
            if (e.IsExpired) continue;
            if (Vector3.Distance(pos, e.Pos) <= radius + e.Radius) return true;
        }
        _positions.RemoveAll(p => p.IsExpired);
        return false;
    }

    /// <summary>
    /// 击杀确认: 每次雷达 Scan 后调用。已确认死亡(不在存活集)的登记立即解除——
    /// 打中的目标下一轮扫描即恢复可拣, 只有打偏才等窗口到期。
    /// 击发过的目标被确认死亡时记录飞行时间日志(实测 ToF 数据采集)。
    /// </summary>
    public void Reconcile(List<string> aliveIds)
    {
        var alive = new HashSet<string>(aliveIds);
        foreach (var kv in _byEntity.ToList())
        {
            var e = kv.Value;
            if (e.IsExpired) { _byEntity.Remove(kv.Key); continue; }
            if (!alive.Contains(kv.Key))
            {
                _byEntity.Remove(kv.Key);
                if (e.Fired)
                    MelonLogger.Msg($"[FCS] Target '{kv.Key}' killed in flight ~{Time.time - e.FiredAt:F0}s (est.)");
            }
        }
        _positions.RemoveAll(p => p.IsExpired);
    }

    /// <summary>热重载/卸载时清空。</summary>
    public void Clear()
    {
        _byEntity.Clear();
        _positions.Clear();
    }

    private Entry? Find(ArtilleryTask task)
    {
        foreach (var e in _byEntity.Values)
            if (e.Owner == task) return e;
        foreach (var e in _positions)
            if (e.Owner == task) return e;
        return null;
    }
}
