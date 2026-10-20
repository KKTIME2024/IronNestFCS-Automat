# 人机协同注册表 + 模式切换原子化 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 issue #3 的两个协作缺陷：① 手动标点开火后，炮弹飞行期（至多约 1 分钟）内雷达可能把同一目标扫描入列，造成重复开火/浪费；② 手动↔全自动切换的原子化语义过粗（forceFire 对所有在飞任务生效，包括尚未装填的）。落地为一个统一的人机协同注册表 `TargetRegistry` + 3b 切割点取消。

**Architecture:** 新增纯数据模块 `TargetRegistry`（任务入队/派发即登记目标，击杀确认或窗口到期解除，手动/自动共用）；`ArtilleryTask` 增加 `Source`/`Canceled`/`Fired`；切换手动时按任务进度分流（未装填→取消，已装填→forceFire，仅限自动任务）；雷达 `Scan` 内做击杀确认（Reconcile）+ 飞行时间日志采集。

**Tech Stack:** .NET 6 / MelonLoader (IL2CPP) / UnityEngine InputSystem。无测试框架（IL2CPP 运行时行为），验证 = 编译 + 运行时清单（见 Task 9）。

**Spec:** 无独立 spec 文件，设计决策记录在 `docs/feasibility-report.md`（数据源）与本文档"设计决策"节。

---

## 背景与问题

### 3a：两条火力链互不知情（架构缺口）

```
手动链: 玩家拖标记 → FireTarget → 入队 → 装填(30-60s) → 击发 → 炮弹飞行(≤60s) → 落地杀伤
自动链: 雷达扫描 → PickTarget → 入队 → ... → 击发 → _firedAt[entityId] 记在飞

冲突: 手动任务 entityId = "" (MapTable.GetMarkTarget 从不解析目标)
     → 手动任务整个生命周期(装填+飞行约 1-2 分钟)对雷达完全隐形
     → 期间雷达扫到该目标仍 IsAlive=true → 照常入列 → 重复开火
```

附带发现：当前自动任务的在飞窗口 `InFlightWindow = 45s`（`FSC.cs:464`，800mm 实测）**短于实际飞行时间**（用户确认至多约 1 分钟）→ 任务退膛结束后（击发后 ~13s）到窗口到期（~45s）之间，目标重新可被拣选，而炮弹尚未落地 → **纯自动模式同样存在重复开火浪费**。注册表统一修复两处。

### 3b：forceFire 语义过粗

现状：`ToggleAutoMode`（`FcsModule.cs:164-165`）对两门炮上**所有**任务无条件置 `forceFire`——包括还在解算（未碰炮弹）的任务，白白浪费一发。物理上只有"弹已上膛"（`Progress.LoadingBullet` 之后）才必须原子化。

## 设计决策（已与仓库主确认）

| 决策点 | 结论 |
|---|---|
| 在飞窗口 | 固定 `FlightWindow = 65s`（覆盖 ≤60s 飞行 + 余量）+ **击杀确认即时释放**（目标 IsAlive=false 即解除，打中后 ≤5s 恢复可拣）+ 飞行时间日志采集（攒 ToF 数据，后续可改距离加权） |
| 手动目标解析 | 点击时**实时查 FireMission.Entities**（不依赖雷达缓存列表——手动模式雷达休眠），标记世界坐标 1km 内最近存活敌目标 → entityId；解析失败退化位置提交（半径 0.3km） |
| forceFire 范围 | 仅**自动**任务；手动任务在炮上保持待击发（玩家切到手动后自己拉扳机；`WaitFire` 会等待玩家手动击发，已有此语义） |
| 实现范围 | 3a + 3b 一起做 |

## 核心语义

| 状态 | 谁持有 | 解除条件 |
|---|---|---|
| 手动任务 入队→击发前 | Registry（entityId 解析成功）或位置禁区（解析失败） | 任务结束（Finished/Failed/Canceled） |
| 击发后 飞行中（手动+自动） | Registry（`MarkFired` 起算 65s） | 击杀确认（下轮 Scan ≤5s）或窗口到期（打偏） |
| 自动任务 派发→击发前 | Registry（TryDispatch 时登记） | 任务结束（未击发） |

**Reconcile 是安全阀**：窗口保守设长无代价——打中就 5s 内解除，只有打偏才等窗口到期。

---

## Task 1: 新建 TargetRegistry（核心模块）

**Files:**
- Create: `IronNestFCS.Logic/FCS/TargetRegistry.cs`

- [x] **Step 1: 创建文件**

```csharp
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
    /// <summary>位置提交(解析失败)的屏蔽半径(km)</summary>
    private const float PositionCommitRadius = 0.3f;

    private readonly Dictionary<string, Entry> _byEntity = new();
    private readonly List<Entry> _positions = new();

    private sealed class Entry
    {
        public readonly ArtilleryTask Owner;
        public readonly string? EntityId;
        public readonly Vector3 Pos;
        public bool Fired;
        public float FiredAt;

        public Entry(ArtilleryTask owner, string? entityId, Vector3 pos)
        {
            Owner = owner;
            EntityId = entityId;
            Pos = pos;
        }

        public bool IsExpired => Fired && Time.time - FiredAt > FlightWindow;
    }

    /// <summary>登记目标。自动任务在派发时调用; 手动任务在入队时调用(队列存活不被清)。幂等。</summary>
    public void Commit(ArtilleryTask task)
    {
        if (task.entityId is { Length: > 0 } id)
            _byEntity[id] = new Entry(task, id, task.position);
        else
            _positions.Add(new Entry(task, null, task.position));
    }

    /// <summary>击发后调用: 启动飞行窗口计时。</summary>
    public void MarkFired(ArtilleryTask task)
    {
        Find(task)?.Fired = true;
        if (Find(task) is { } e) e.FiredAt = Time.time;
    }

    /// <summary>任务结束(未击发)时解除登记。幂等。</summary>
    public void Release(ArtilleryTask task)
    {
        var e = Find(task);
        if (e == null) return;
        if (e.EntityId is { Length: > 0 } id) _byEntity.Remove(id);
        else _positions.Remove(e);
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
            if (Vector3.Distance(pos, e.Pos) <= radius + PositionCommitRadius) return true;
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
```

> 注意 `MarkFired` 写法：先 `Find` 两次不优雅，合并为一次（见 Task 4 检查点，实施时修正为单次查找）。

- [x] **Step 2: Commit**

```bash
git add IronNestFCS.Logic/FCS/TargetRegistry.cs
git commit -m "feat: human-machine collaboration target registry (issue #3a)"
```

---

## Task 2: ArtilleryTask 扩展字段 + Progress.Canceled

**Files:**
- Modify: `IronNestFCS.Logic/FCS/ArtilleryTask.cs`

- [x] **Step 1: 扩展字段（文件末尾 `forceFire` 后）**

```csharp
    /// <summary>任务来源: 雷达自动 or 玩家手动(手动任务不被自动清队列清掉)</summary>
    public TaskSource Source = TaskSource.Auto;
    /// <summary>切手动时置位: 未开始装填的自动任务干净放弃, 不碰炮膛</summary>
    public bool Canceled;
    /// <summary>已击发(Registry 飞行窗口计时依据; 未击发的任务结束时 Release 登记)</summary>
    public bool Fired;
```

- [x] **Step 2: Progress 枚举加 Canceled（`Failed` 之后）**

```csharp
    Failed,
    Canceled,
```

> 注意：`FcsModule` 的进度分流判断用 `task.progress < Progress.LoadingBullet`，`Canceled` 排在枚举末尾不影响该比较（取消后的任务立即结束）。

- [x] **Step 3: Commit**

```bash
git add IronNestFCS.Logic/FCS/ArtilleryTask.cs
git commit -m "feat: task Source/Canceled/Fired + Canceled progress state"
```

---

## Task 3: MapTable 增加标记世界坐标访问器

**Files:**
- Modify: `IronNestFCS.Logic/FCS/MapTable.cs`

- [x] **Step 1: `ResetMarker` 后新增**

```csharp
    /// <summary>指定编号标记的世界坐标(手动任务目标解析与位置提交用)</summary>
    public Vector3 GetMarkerWorldPos(int index)
    {
        if (artilleries.TryGetValue(index, out var marker)) return marker.position;
        return Vector3.zero;
    }
```

- [x] **Step 2: Commit**

```bash
git add IronNestFCS.Logic/FCS/MapTable.cs
git commit -m "feat: MapTable.GetMarkerWorldPos accessor"
```

---

## Task 4: FSC 接线（核心调度改动）

**Files:**
- Modify: `IronNestFCS.Logic/FSC.cs`

- [x] **Step 1: 字段**

`_taskQueue` 声明（第 51 行）附近新增：

```csharp
    /// <summary>人机协同注册表: 在飞/排队/炮上目标的统一承包登记(手动+自动共用)</summary>
    public readonly TargetRegistry Registry = new();

    /// <summary>手动任务目标解析器(FcsModule 创建雷达后注入)</summary>
    public TacticalRadar? EntityLocator { get; set; }
```

- [x] **Step 2: 删除旧在飞窗口**

删除第 461-467 行的 `InFlightWindow` / `_firedAt` / `InFlight()`。

- [x] **Step 3: EnqueueTask 手动解析 + 入队登记**

```csharp
    public void EnqueueTask(ArtilleryTask task) {
        task.progress = Progress.Pending;
        // 手动任务: 解析标记位置的存活敌目标 → entityId(注册表按目标屏蔽);
        // 解析失败保持空 → 按位置提交(小半径屏蔽)。
        if (task.Source == TaskSource.Manual && task.entityId is not { Length: > 0 } && EntityLocator != null)
            task.entityId = EntityLocator.FindNearestHostileId(task.position, TargetRegistry.ManualResolveMaxDistance) ?? "";
        _taskQueue.Enqueue(task);
        // 手动任务入队即登记(队列在自动清队列中存活); 自动任务在派发时登记。
        if (task.Source == TaskSource.Manual) Registry.Commit(task);
        TryDispatch();
    }
```

- [x] **Step 4: TryDispatch 派发时登记**

```csharp
            var task = _taskQueue.Dequeue();
            if (slot == LeftRight.Left) LeftTask = task;
            else RightTask = task;
            Registry.Commit(task);   // 手动任务幂等(入队已登记)
            StartTaskRoutine(slot, task);
```

- [x] **Step 5: ClearPendingTasks 只清自动任务**

```csharp
    /// <summary>清空队列中雷达自动入队的任务, 保留玩家手动入队的任务。</summary>
    public void ClearPendingTasks() {
        var kept = _taskQueue.Where(t => t.Source == TaskSource.Manual).ToList();
        _taskQueue.Clear();
        foreach (var t in kept) _taskQueue.Enqueue(t);
    }
```

- [x] **Step 6: RunTaskRoutine 取消检查点（desk 锁释放后、viable 判断前）**

```csharp
        try {
            // 3b 切割点: 切手动时置 Canceled 的自动任务在此干净放弃——
            // 还没碰炮膛(未 LoadBullet), 无卡膛风险。复用 viable 分支的 abort 模式。
            if (task.Canceled) {
                task.progress = Progress.Canceled;
                turret.Canceled = true;
                ReleaseTurretOnce(turret);
                Registry.Release(task);
                ReleaseSlot(leftRight);
                slotReleased = true;
                yield break;
            }
            if (!viable) {
                // ... 现有逻辑不变
```

- [x] **Step 7: 击发段替换 `_firedAt` 登记**

`_firedAt[task.entityId] = Time.time;`（第 617 行）替换为：

```csharp
                task.Fired = true;
                Registry.MarkFired(task);
```

- [x] **Step 8: 外层 finally 释放未击发任务的登记**

外层 finally（第 631 行）开头加：

```csharp
        finally {
            // 未击发的任务结束时解除登记(击发过的留给击杀确认/窗口到期)。
            // Canceled/Failed 分支幂等。
            if (!task.Fired) Registry.Release(task);
            if (!slotReleased) {
                ...
```

- [x] **Step 9: Dispose 清理**

`_firedAt.Clear();`（第 371 行）替换为 `Registry.Clear();`，同时 `_firedAt` 引用全部清除。

- [x] **Step 10: Commit**

```bash
git add IronNestFCS.Logic/FSC.cs
git commit -m "feat: wire target registry + 3b cancel checkpoint + keep-manual queue clear"
```

---

## Task 5: TacticalRadar 实体枚举重构 + 目标解析 + Reconcile

**Files:**
- Modify: `IronNestFCS.Logic/TacticalRadar.cs`

- [x] **Step 1: 提取实体枚举辅助方法**

在 `Scan()` 前新增（把 Scan 里 69-98 行的枚举器样板搬入）：

```csharp
    /// <summary>反射枚举 FireMission.Entities, 对每个 MapEntity 回调 (key, MapEntity)。</summary>
    private void ForEachEntity(Action<string, object> action)
    {
        var (fm, entities) = GetEntitiesDict();
        if (entities == null) return;
        var getEnum = entities.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
        if (getEnum == null) return;
        var enumerator = getEnum.Invoke(entities, null);
        if (enumerator == null) return;
        var enumType = enumerator.GetType();
        var moveNext = enumType.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
        var currentProp = enumType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
        if (moveNext == null || currentProp == null) return;
        while ((bool)moveNext.Invoke(enumerator, null)!)
        {
            var kvp = currentProp.GetValue(enumerator);
            if (kvp == null) continue;
            var kvpType = kvp.GetType();
            var keyProp = kvpType.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
            var valueProp = kvpType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (keyProp == null || valueProp == null) continue;
            var key = keyProp.GetValue(kvp)?.ToString() ?? "";
            var me = valueProp.GetValue(kvp);
            if (me == null) continue;
            action(key, me);
        }
    }

    private static bool IsAlive(object me)
    {
        var aliveProp = me.GetType().GetProperty("IsAlive", BindingFlags.Public | BindingFlags.Instance);
        return aliveProp?.GetValue(me) is bool b && b;
    }

    private static int GetRole(object me)
    {
        var roleProp = me.GetType().GetProperty("Role", BindingFlags.Public | BindingFlags.Instance);
        var roleVal = roleProp?.GetValue(me);
        if (roleVal is int ri) return ri;
        if (roleVal is Enum e) return Convert.ToInt32(e);
        return -1;
    }

    /// <summary>敌对 = 带 Enemy/Target 位且非 Reference(Ally-only 已天然排除)</summary>
    private static bool IsHostileRole(int role) =>
        role >= 0 && (role & (RoleEnemy | RoleTarget)) != 0 && (role & RoleReference) == 0;

    /// <summary>世界坐标: 优先 Location.transform.position, 兜底 coordinateRoot.TransformPoint</summary>
    private bool TryGetWorldPos(object me, out Vector3 worldPos)
    {
        var locProp = me.GetType().GetProperty("Location", BindingFlags.Public | BindingFlags.Instance);
        if (locProp?.GetValue(me) is EntityLocation location)
        {
            worldPos = location.transform.position;
            return true;
        }
        if (_coordinateRoot != null)
        {
            var posProp = me.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
            if (posProp?.GetValue(me) is Vector3 mp)
            {
                worldPos = _coordinateRoot.TransformPoint(mp);
                return true;
            }
        }
        worldPos = Vector3.zero;
        return false;
    }
```

- [x] **Step 2: Scan 改用 ForEachEntity + Reconcile**

Scan 开头的枚举样板（69-98 行）替换为：

```csharp
    public void Scan()
    {
        AliveHostiles.Clear();
        var targets = new List<TacticalDecider.TargetInfo>();
        var aliveIds = new List<string>();

        ForEachEntity((key, me) =>
        {
            if (IsAlive(me)) aliveIds.Add(key);
            var t = BuildTargetInfo(key, me);
            if (t != null) targets.Add(t.Value);
        });

        // 击杀确认: 死亡目标的登记立即解除(打中的目标下轮扫描恢复可拣)
        fcs.Registry.Reconcile(aliveIds);
```

Scan 内其余逻辑（SortTargets/日志/标记放置）不动。原实体读取主体（102-215 行）抽取为私有方法 `BuildTargetInfo(string key, object me) -> TacticalDecider.TargetInfo?`（IsAlive/Role 过滤改用 Step 1 辅助方法，世界坐标块改用 `TryGetWorldPos`）。

> 注意：原 Scan 的世界坐标块使用 `locProp.GetValue(me) as EntityLocation` 与 `_coordinateRoot.TransformPoint`，抽取时保持行为一致。

- [x] **Step 3: 新增 FindNearestHostileId**

```csharp
    /// <summary>
    /// 实时目标解析(手动开火用): 标记世界坐标附近最近的存活敌目标。
    /// 直接查 FireMission.Entities, 不依赖 AliveHostiles 缓存(手动模式雷达休眠)。
    /// </summary>
    public string? FindNearestHostileId(Vector3 worldPos, float maxDistanceKm)
    {
        string? nearest = null;
        float best = maxDistanceKm;
        ForEachEntity((key, me) =>
        {
            if (!IsAlive(me)) return;
            if (!IsHostileRole(GetRole(me))) return;
            if (!TryGetWorldPos(me, out var wp)) return;
            var d = Vector3.Distance(worldPos, wp) * 3.8164f;
            if (d < best) { best = d; nearest = key; }
        });
        return nearest;
    }
```

- [x] **Step 4: Commit**

```bash
git add IronNestFCS.Logic/TacticalRadar.cs
git commit -m "feat: entity enumeration refactor + manual target resolution + kill reconciliation"
```

---

## Task 6: FcsSceneInteractor 手动任务标记

**Files:**
- Modify: `IronNestFCS.Logic/FcsSceneInteractor.cs`

- [x] **Step 1: 两处手动入队点标记 Source + 世界坐标**

`FireTarget`（第 137-144 行）与 T1-T4 按钮 lambda（第 105-119 行）中，`fcs.EnqueueTask(task)` 前加：

```csharp
        task.Source = TaskSource.Manual;
        task.position = fcs.MapTable.GetMarkerWorldPos(targetId);
```

> `GetMarkTarget` 返回的 `position` 是旧表格系伪坐标, 覆盖为标记世界坐标, 供目标解析与位置提交使用(显示转换 ConvertPosition 也一并对齐)。

- [x] **Step 2: Commit**

```bash
git add IronNestFCS.Logic/FcsSceneInteractor.cs
git commit -m "feat: tag manual tasks as Manual source + world position"
```

---

## Task 7: FcsModule 装配与切换分流

**Files:**
- Modify: `IronNestFCS.Logic/FcsModule.cs`

- [x] **Step 1: Initialize 注入 EntityLocator**

```csharp
        window = new FcsWindow(fcs);
        radar = new TacticalRadar(fcs);
        fcs.EntityLocator = radar;   // 手动任务目标解析
```

- [x] **Step 2: OnGunIdle 去 busyIds + 标 Source**

删除 busyIds/HashSet 块（47-49 行）与 `PickTarget(busyIds)` 传参；创建任务加 `Source = TaskSource.Auto`：

```csharp
        fcs.ClearPendingTasks();   // 只清自动任务, 手动任务保留

        int nextTargetId = 1;
        foreach (var barrel in new[] { LeftRight.Left, LeftRight.Right })
        {
            if (barrel == LeftRight.Left ? fcs.LeftTask != null : fcs.RightTask != null) continue;

            var t = PickTarget();
            if (t == null) continue;
            var ti = t.Value;

            var task = new ArtilleryTask
            {
                targetId = nextTargetId++,
                entityId = ti.EntityId,
                angel = ti.Angle,
                distance = ti.Distance,
                position = ti.WorldPos,
                bulletType = TacticalDecider.ChooseShellType(ti),
                useMaxCharge = TacticalDecider.ShouldUseMaxCharge(ti),
                Source = TaskSource.Auto
            };
            fcs.EnqueueTask(task);
        }
```

- [x] **Step 3: PickTarget 改用注册表（busyIds 由派发时登记覆盖）**

```csharp
    /// <summary>挑目标: 注册表(炮上/排队/在飞/手动提交)中的目标跳过, 返回最高优先级可选目标。</summary>
    private TacticalDecider.TargetInfo? PickTarget()
    {
        foreach (var t in radar.AliveHostiles)
        {
            if (fcs.Registry.IsHandled(t.EntityId)) continue;
            if (fcs.Registry.IsHandledNear(t.WorldPos, 0f)) continue;
            return t;
        }
        return null;
    }
```

> 派发即登记(Registry.Commit 在 TryDispatch), 原 busyIds 的"同帧双炮去重"由注册表天然覆盖。

- [x] **Step 4: ToggleAutoMode 按进度分流**

```csharp
        if (!autoMode)
        {
            fcs.ClearPendingTasks();   // 只清自动入队的队列, 手动任务保留
            CancelOrForceFire(fcs.LeftTask);
            CancelOrForceFire(fcs.RightTask);
            MelonLogger.Msg("[FCS] 手动模式:雷达休眠,手动标点 T1-T4 接管");
        }
```

新增：

```csharp
    /// <summary>
    /// 切手动: 自动任务按进度分流——未开始装填(未碰炮膛)的干净取消, 不浪费弹;
    /// 已进入装填(弹已上膛)的强制击发, 原子化防卡膛。
    /// 手动任务保持待击发, 由玩家自己拉扳机(WaitFire 等待玩家手动击发)。
    /// </summary>
    private static void CancelOrForceFire(ArtilleryTask? task)
    {
        if (task == null || task.Source != TaskSource.Auto) return;
        if (task.progress < Progress.LoadingBullet) task.Canceled = true;
        else task.forceFire = true;
    }
```

- [x] **Step 5: Commit**

```bash
git add IronNestFCS.Logic/FcsModule.cs
git commit -m "feat: mode-switch progress-based cancel/forceFire + registry-based target pick"
```

---

## Task 8: FcsWindow Canceled 显示

**Files:**
- Modify: `IronNestFCS.Logic/FcsWindow.cs`

- [x] **Step 1: stateColor switch 加分支**

```csharp
        Color stateColor = task.progress switch
        {
            Progress.Failed => ClrFailed,
            Progress.Finished => ClrGreen,
            Progress.Pending => ClrLabel,
            Progress.Canceled => ClrLabel,
            _ => ClrActive
        };
```

- [x] **Step 2: Commit**

```bash
git add IronNestFCS.Logic/FcsWindow.cs
git commit -m "feat: show Canceled task state in status panel"
```

---

## Task 9: 编译验证 + 运行时验证清单（有环境的机器）

- [x] **Step 1: 全量构建**

```bash
dotnet build IronNestFCS.sln -c Release
```

Expected: BUILD SUCCEEDED（GameDir 需指向游戏安装路径）。

- [x] **Step 2: 场景 A — 手动开火不被雷达抢（3a 核心）**

全自动模式下拖标记对准某敌目标 → 按 Numpad 1 手动开火 → 观察：装填+飞行全期（≥60s）雷达不重复拣该目标（`[Radar]` 日志无该 entityId）；落地击杀后 ≤5s 日志出现 `killed in flight ~Xs (est.)`。

- [x] **Step 3: 场景 B — 纯自动在飞窗口修复**

纯自动模式下单目标：击发后观察全飞行期（旧 45s 窗口外）不重复派发；击杀后恢复可拣。

- [x] **Step 4: 场景 C — 3b 切割点（切手动）**

全自动运行中，趁任务在解算阶段按 Numpad 0：面板该任务显示 Canceled、炮位回 Idle、不浪费弹；任务已装填时切换：正常击发不卡膛。

- [x] **Step 5: 场景 D — 手动队列存活**

手动模式 Numpad 1 入队（双炮忙时排队）→ 切自动：队列保留、任务继续执行，雷达围绕手动任务补位。

- [x] **Step 6: 场景 E — 打偏恢复**

命中确认日志缺失（目标未死）：窗口 65s 到期后目标重新被拣选。

- [x] **Step 7: 场景 F — 热重载回归**

F9 重载：无 ALC 泄漏、无协程残留、注册表清零。

---

## 风险与已知事项

- **验证结果（2026-08-12 收尾）**：Tasks 1–8 已全部提交（`d8fc64b`→`0340d98`）；场景 A–F 已于 2026-08-11 在有编译环境的机器上运行通过。Task 1 `MarkFired` 的双次 `Find` 已合并为单次；`FSC.cs` 的 `using System.Linq;` 已有。
- **击杀确认时机**：雷达只在自动模式扫描——手动模式下飞行中的炮弹击杀要到切回自动才解除登记（无害：雷达休眠期无冲突）。
- **手动任务长时间排队**：双炮持续忙碌时手动任务会在队列等待，其登记持续屏蔽雷达（符合"玩家意图优先"）。
- **重复登记冲突**：同一 entityId 被两个任务同时登记时后者覆盖（可接受，最后登记方承包）。
- **协作模块是 #2 的地基**：注册表 + 位置半径天然支持"目标附近有友军/已提交目标就跳过或换弹种"，后续在 `TacticalDecider` 扩展。
