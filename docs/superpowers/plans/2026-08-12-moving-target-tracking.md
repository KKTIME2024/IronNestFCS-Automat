# 移动目标跟踪（方案 2 统一路径）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 移动目标跟踪——识别移动目标后装填，开火前用 ToF + 距离解算 + 射表**实时修正仰角/方向机直到发射**。统一路径：所有任务走公式直算 + 连续瞄准（静态任务速度=0 自然退化）；游戏计算器降级为**纯装饰视觉同步**（每发装填期并行拉一次，不回传）。

**Architecture:** 新 `TargetLeadSolver`（纯数学，aim(t) 解析闭合式，从任务创建时冻结的 `(P0,V)` 快照外推）；`FSC.RunTaskRoutine` 重构——解算移出 desk 锁、计算器视觉同步并行、装填期追方位、装填后追仰角、击发瞬间取当前 aim；`Turret`/`GunSystem` 加非阻塞驱动；`TacticalRadar` 平滑速度估计；装药按预测最远距离选。注册表/InFlight 零改动。

**Tech Stack:** .NET 6 / MelonLoader (IL2CPP) / UnityEngine。无测试框架，验证 = 编译 + 运行时清单（Task 8）。

**前置依赖（已完成）:** issue #3 注册表（entityId 防双发）、`ToFTable` 射表、面板倒计时。`docs/moving-target-tracking.md` 为设计文档（决策记录在案）。

---

## 背景与设计决策（已与仓库主确认）

### 核心物理事实

- **弹道定律线性**：`仰角 = 0.012 × 距离(m) ÷ 装药`（两独立来源交叉验证：ToF.txt 射表 + 社区 FCC 工具，逐格一致）
- **装药有效边界**：仰角 ≤ 60° → `装药 ≥ 距离(km) × 0.2`（即"每 5km 一包药"，与现有 `MinimumCharge` 吻合）
- **ToF**：仅我们的射表（装药×射程二维），无弹种维度

### 决策表

| 决策点 | 结论 |
|---|---|
| 方案 | 方案 2 **统一路径**直接实施；变体 A（装填后单次复算）仅作退化回退 |
| 目标运动模型 | 假设**恒定速率匀速直线**（闭合式，无加速度项） |
| 移动判定 | **创建时一次判定**（快照速度 > 阈值 → 任务标 `IsMoving`）；匀速假设下全程有效 |
| 启用/结束 | 任务开始（速度检测）→ **击发瞬间**；早期结束 = 出射程/失速/装药不足 → 退化 |
| 瞄准来源 | 公式直算（不进 desk 锁、不读计算器） |
| 装药 | 装填时定格，覆盖**全程预测最远距离**；仰角用固定装药重算 |
| 计算器 | **纯装饰视觉同步**：每发装填期并行拉一次（静态=正确数据、移动=大致数据），不回传、不参与瞄准 |
| 跳过计算器 | **已确认允许**（无需 ConfirmElevation 探针）：直接设仰角杆，确认序列照常通过 |
| 手动模式 | **一律按静态目标处理**（不跟踪移动目标；手动任务 `IsMoving=false`） |
| 速度来源 | **创建时冻结 `(P0, V)` 快照**（闭合式外推，匀速假设下自洽）；雷达 Scan 给平滑速度。非匀速敌人证实后再加实时查询 |
| 注册表/InFlight | 零改动（entityId 持有已防双发） |

### 核心语义：aim(t) 闭合式

```
P_t(t)  = P0 + V·t                      // V 世界单位/s, t 秒
aim(t)  = P0 + V·(t + ToF(t))           // t = 候选击发时刻
ToF(t)  = ToFTable(dist_km(P0 + V·t))   // 忽略 V·ToF 项, 无需迭代
bearing = CalcAngle(aim(t))             // 每帧写 DesiredRotation
dist_km = |aim(t) − turret| × 3.8164
elev    = 0.012 × dist_m ÷ loadedCharge // loadedCharge 固定(装填时定格)
```

误差物理下限 = 目标加速度 × ToF（匀速假设下为 0）。

### 关键约束

`WaitForReloadReady`（GunSystem.cs:145）要求装填时**仰角静止**（`elevationChangeVelocity == 0`），但**不检查炮塔** → 装填期只能追方位、不能追仰角；仰角跟踪从装填完成后开始。

---

## Task 1: TargetLeadSolver（新，纯数学）

**Files:**
- Create: `IronNestFCS.Logic/FCS/TargetLeadSolver.cs`

- [x] **Step 1: 创建文件**

```csharp
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 移动目标提前量解算: aim(t) 解析闭合式。
/// 匀速直线假设。纯数学, 无 IL2CPP 引用。
/// 单位: 位置=世界单位, 距离=km(×3.8164), 速度=世界单位/s(与雷达 TargetInfo.Velocity 一致)。
/// </summary>
public static class TargetLeadSolver
{
    /// <summary>移动速度阈值(km/s): 超过才启用提前量, 否则 aim 退化为静态。</summary>
    public const float MovingThresholdKmS = 0.001f;

    /// <summary>提前点: 匀速目标在 t 时刻的命中位置。</summary>
    public static Vector3 LeadPoint(Vector3 p0, Vector3 velWorldPerSec, float t, float tof)
        => p0 + velWorldPerSec * (t + tof);

    /// <summary>装药覆盖: charge = ceil(dist_km × 0.2)（仰角≤60° 的闭合式）</summary>
    public static int ChargeFor(float distKm)
        => Mathf.CeilToInt(distKm * 0.2f);

    /// <summary>仰角公式: 0.012 × 距离(m) ÷ 装药</summary>
    public static float Elevation(float distKm, int charge)
        => 0.012f * distKm * 1000f / charge;

    /// <summary>速度是否算"移动"（世界单位/s ×3.8164 = km/s）</summary>
    public static bool IsMoving(Vector3 velWorldPerSec)
        => velWorldPerSec.magnitude * 3.8164f > MovingThresholdKmS;
}
```

> 注：`ToFTable.FlightTime(distKm, charge)` 用于 aim 的 ToF 项，distKm 需先换算。

- [x] **Step 2: Commit**

```bash
git add IronNestFCS.Logic/FCS/TargetLeadSolver.cs
git commit -m "feat: moving-target lead solver (closed-form aim)"
```

---

## Task 2: TacticalRadar 平滑速度估计

**Files:**
- Modify: `IronNestFCS.Logic/TacticalRadar.cs`

- [x] **Step 1: Scan 速度估计用更长基线（冻结快照的精度依赖它）**

现 `moveSnapshots` 用 1s 间隔算速度，噪声大。改为保留最近 ~4 个采样点，速度 = 首尾位移 / 首尾时间差（或滑动平均）。移动判定阈值、`TargetInfo.Velocity` 语义（世界单位/s）不变。

> 实施注意：速度估计质量直接决定提前量精度——误差随 `(装填时长 + ToF)` 放大（V 差 0.002 km/s，40s 后 ~80m）。4-5s 基线比 1s 稳得多。后续非匀速敌人证实后，才需要实时状态访问器（本计划不实现）。

- [x] **Step 2: Commit**

```bash
git add IronNestFCS.Logic/TacticalRadar.cs
git commit -m "feat: radar smoother velocity estimate (longer baseline)"
```

---

## Task 3: ArtilleryTask 扩展

**Files:**
- Modify: `IronNestFCS.Logic/FCS/ArtilleryTask.cs`

- [x] **Step 1: 加移动任务字段（冻结快照）**

```csharp
    /// <summary>该任务是否按移动目标处理（创建时由雷达 IsMoving 一次判定）</summary>
    public bool IsMoving;
    /// <summary>装填时定格的装药数（覆盖全程预测最远距离, 仰角重算用它）</summary>
    public int LoadedCharge;
    /// <summary>移动目标冻结快照: 创建时位置(世界单位) + 速度(世界单位/s) + 参考时刻。
    /// aim(t) = AimP0 + AimVel×(t − AimStartTime + ToF), 匀速假设下闭合式自洽。</summary>
    public Vector3 AimP0;
    public Vector3 AimVel;
    public float AimStartTime;
```

> 全程不查雷达——所有 aim 计算从这三个字段外推。`entityId` 仍保留（注册表/日志用），但 aim 循环不依赖它。

- [x] **Step 2: Commit**

```bash
git add IronNestFCS.Logic/FCS/ArtilleryTask.cs
git commit -m "feat: task moving flag + loaded charge field"
```

---

## Task 4: 机构非阻塞驱动

**Files:**
- Modify: `IronNestFCS.Logic/FCS/Turret.cs`
- Modify: `IronNestFCS.Logic/FCS/GunSystem.cs`

- [x] **Step 1: Turret 加非阻塞旋转**（`SetRotation` 旁）

```csharp
    /// <summary>非阻塞: 直接设目标方位角, 不等待转完。连续跟踪每帧调用。</summary>
    public void SetDesiredRotation(float angle) {
        if (_turret == null) return;
        _turret.DesiredRotation = -angle;
        LastSetAngle = angle;
    }

    /// <summary>当前方位角与目标角度差(绝对差, 0-180 化)。就绪判定用。</summary>
    public float AngleError(float targetAngle) {
        if (_turret == null) return 0f;
        float d = Mathf.Abs(_turret.DesiredRotation + targetAngle) % 360f;  // 注意 -angle 约定
        return d > 180f ? 360f - d : d;
    }
```

- [x] **Step 2: GunSystem 加非阻塞仰角 + 误差读取**

```csharp
    /// <summary>非阻塞: 设仰角杆目标值, 不等待。连续跟踪每帧调用。</summary>
    public void SetElevationTarget(float elevation) {
        if (elevationLever == null) return;
        elevationLever.SetSliderValue(elevation);
    }

    /// <summary>当前仰角（度）与目标差。就绪判定用。</summary>
    public float ElevationError(float target) {
        if (gunController == null) return 0f;
        return Mathf.Abs(gunController.CurrentElevation - target);
    }

    /// <summary>是否已处于击发/待击发状态（玩家拉扳机后为 true）。手动等待判定用。</summary>
    public bool IsPendingReload() => gunController != null && gunController.pendingReload;
```

> 单位注意：`DesiredRotation` 用 `-angle`（与 `SetRotation` 一致）；`ElevationError` 用 `gunController.CurrentElevation`（`SetElevation` 收敛判断同一来源）。`IsPendingReload` 与现有 `WaitFire` 内部读同一个 `gunController.pendingReload`。

- [x] **Step 3: Commit**

```bash
git add IronNestFCS.Logic/FCS/Turret.cs IronNestFCS.Logic/FCS/GunSystem.cs
git commit -m "feat: non-blocking turret/elevation drive for continuous tracking"
```

---

## Task 5: FSC.RunTaskRoutine 重构（核心）

**Files:**
- Modify: `IronNestFCS.Logic/FSC.cs`

> 本任务改动最大。目标：统一路径——所有任务走公式 + 连续瞄准；解算移出 desk 锁；计算器视觉同步并行。

- [x] **Step 1: 字段 + 就绪容差**

```csharp
    /// <summary>机构就绪容差(度): 双机构与 aim 差小于此值且 CanFire 才进入确认。
    /// 1° 已够——毁伤半径兜底(10km 处 1° ≈ 175m), 高装药压低漂移率(~0.3°/s)保证收敛。</summary>
    private const float AimToleranceDeg = 1f;
```

- [x] **Step 2: 装药决策改为预测最远距离**

`RunTaskRoutine` 内（现 :508 `powderCount` 处）替换：

```csharp
        // 统一路径: 装药按预测最远距离定格（覆盖装填+飞行期目标位移）。
        // 静态任务 = ChargeFor(当前距离)。maxCharge 覆盖。
        var distNowKm = task.distance;
        var distLeadMaxKm = distNowKm;
        if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel)) {
            // 保守预测: 装填期(估 60s)后目标位置的距离; 从冻结快照外推
            var tof = ToFTable.FlightTime(distNowKm, TargetLeadSolver.ChargeFor(distNowKm));
            var far = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel,
                Time.time - task.AimStartTime + 60f, tof);
            distLeadMaxKm = Mathf.Max(distNowKm, DistKm(far));
        }
        var powderCount = task.useMaxCharge || _sceneInteractor.maxCharge
            ? 6
            : Mathf.Min(6, TargetLeadSolver.ChargeFor(distLeadMaxKm + MovingDistanceMarginKm));
        task.LoadedCharge = powderCount;
```

> `DistKm(worldPos)` = 世界坐标 → 距离 km 的共享几何辅助（Task 5 一并抽，见 Step 4）。`MovingDistanceMarginKm`（约 1.5km）补装填时长不确定性。若任务中途不再移动，powderCount 仍有效（高装药=更平更短 ToF，无害）。

- [x] **Step 3: 解算临界区 1 收缩为纯采购 + 启动视觉同步**

现临界区 1（desk 锁内 SetDistance/Direction/Charge/ShellType/Calculate/GetElevation）删除；保留装药/弹种采购（仍锁内）。临界区 1 结束后启动**并行**视觉同步协程：

```csharp
        // 临界区 1 后: 计算器纯装饰视觉同步, 与装填并行（不进任务关键路径）
        _runningCoroutines.Add(MelonCoroutines.Start(SyncCalculatorVisual(task, powderCount)));
```

新增协程：

```csharp
    /// <summary>纯装饰: 装填期并行驱动一次游戏计算器（静态=正确数据, 移动=大致数据）。
    /// 不回传仰角, 不参与瞄准。desk 锁短持有, 与采购/另一门炮互斥。</summary>
    private IEnumerator SyncCalculatorVisual(ArtilleryTask task, int powderCount) {
        yield return _deskLock.Acquire();
        try {
            yield return BallisticCalculator.SetDistance(task.distance);
            yield return BallisticCalculator.SetDirection(task.angel);
            yield return BallisticCalculator.SetCharge(powderCount);
            yield return BallisticCalculator.SetShellType(task.bulletType);
            yield return BallisticCalculator.Calculate();
        } finally {
            _deskLock.Release();
        }
    }
```

- [x] **Step 4: 删除旧 elevation 求解**（`elevation = BallisticCalculator.GetElevation()` 及依赖它的 SetElevation 段），改为 aim 跟踪循环

先把几何辅助抽出来（本任务内新增，供 Step 2 与 Step 4 共用；提取自 `TacticalRadar.CalcAngle`/`CalcDistance`，缓存 transform 避免每帧 `GameObject.Find`）：

```csharp
    // 缓存 Draggable Surface + turret 引用（TryBind 时取一次）
    private Transform? _mapSurface;
    private Transform? _turretXf;
    private void CacheAimGeometry() {
        _mapSurface = GameObject.Find("Draggable Surface")?.transform;
        _turretXf = fcs.MapTable.Turret != null ? fcs.MapTable.Turret.transform : null;
    }
    private float DistKm(Vector3 worldPos) {
        if (_mapSurface == null || _turretXf == null) return 0f;
        var target = _mapSurface.InverseTransformPoint(worldPos) - _turretXf.localPosition;
        return target.magnitude * 3.8164f;
    }
    private float Bearing(Vector3 worldPos) {
        if (_mapSurface == null || _turretXf == null) return 0f;
        var target = _mapSurface.InverseTransformPoint(worldPos) - _turretXf.localPosition;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        return angle < 0 ? angle + 360f : angle;
    }
```

装填完成后（现 `task.progress = Progress.Aiming` 处），替换为：

先抽共享 aim 计算辅助（Step 5 复检复用同一来源）：

```csharp
    /// <summary>当前 aim 目标值。移动目标从冻结快照外推; 静态回退固定值。返回是否算移动。</summary>
    private bool TryComputeAimTargets(ArtilleryTask task, out float bearing, out float elev) {
        bearing = task.angel;
        elev = TargetLeadSolver.Elevation(task.distance, task.LoadedCharge);
        if (!task.IsMoving || !TargetLeadSolver.IsMoving(task.AimVel)) return false;
        var tof = ToFTable.FlightTime(task.distance, task.LoadedCharge);
        var aim = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel, Time.time - task.AimStartTime, tof);
        bearing = Bearing(aim);
        elev = TargetLeadSolver.Elevation(DistKm(aim), task.LoadedCharge);
        return true;
    }
```

装填完成后（现 `task.progress = Progress.Aiming` 处），替换为：

```csharp
            // ===== 锁外: 连续瞄准跟踪 =====
            // 装填已完成 → 仰角可动。每帧重算 aim(t) 并驱动双机构, 直到收敛 + CanFire。
            // 移动目标走提前量; 速度≈0/无状态退化为静态 aim（同一循环, 无分类边界）。
            task.progress = Progress.Aiming;
            var aimTrackTimeout = 0;
            while (true) {
                TryComputeAimTargets(task, out var bearingTarget, out var elevTarget);
                // 出装药覆盖检查: 移动目标的提前点距离超过装药射程 → 退化
                if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel)) {
                    var tof = ToFTable.FlightTime(task.distance, task.LoadedCharge);
                    var leadDist = DistKm(TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel,
                        Time.time - task.AimStartTime, tof));
                    if (leadDist * 0.2f > task.LoadedCharge + 0.1f) {
                        task.progress = Progress.Failed;
                        yield break;
                    }
                }
                Turret.SetDesiredRotation(bearingTarget);
                gunSys.SetElevationTarget(elevTarget);
                // 收敛判定对"当前帧目标值"比较（移动时 aim 每帧漂移, 机构追上即收敛）
                if (gunSys.ElevationError(elevTarget) < AimToleranceDeg
                    && Turret.AngleError(bearingTarget) < AimToleranceDeg
                    && gunSys.CanFire())
                    break;
                if (++aimTrackTimeout >= 240) { task.progress = Progress.Failed; yield break; }  // 4 分钟兜底
                yield return null;
            }
```

> 静态任务走同一循环：`IsMoving=false` → 目标值恒定，机构收敛一次即 break。装填期仰角由后台协程驱动（Step 6），此循环只负责装填完成后。若确认期间 aim 继续漂移，误差 = 机构滞后（slew ≫ 漂移率），可接受。

- [x] **Step 5: 开火条件 = 收敛检查 + 击发前复检 + AutoFire 闸门**

开火前对齐复检（aim 计算复用 Step 4 的 `TryComputeAimTargets`）：

```csharp
    /// <summary>开火前对齐复检: 双机构对当前 aim(t) 都在容差内。</summary>
    private bool AlignOk(ArtilleryTask task, float tol) {
        TryComputeAimTargets(task, out var bearing, out var elev);
        return gunSys.ElevationError(elev) < tol && Turret.AngleError(bearing) < tol;
    }
```

击发段（turret 锁内）改：

```csharp
            // Step 4 循环已收敛(机构 < 容差 且 CanFire) → 进入确认
            try {
                yield return TriggerConsole.ConfirmTask();
                yield return TriggerConsole.ConfirmBullet();
                yield return TriggerConsole.ConfirmRotation();
                yield return TriggerConsole.ConfirmElevation();
                yield return TriggerConsole.ReadyToFire();
                yield return TriggerConsole.Arm(leftRight);
                if (_sceneInteractor.AutoFire || task.forceFire) {
                    // 确认序列 ~3-4s 期间 aim 漂移(机构追着走); 复检对齐后再打
                    while (!AlignOk(task, AimToleranceDeg) && ++aimTrackTimeout < 60) yield return null;
                    TriggerConsole.Fire();
                    yield return gunSys.WaitFire();
                } else {
                    // AutoFire 关: 持续跟踪 aim(t) 直到玩家扳机——否则炮管停在旧点, 移动目标必偏。
                    // 静态任务此处目标值恒定, 循环即无操作等待。
                    while (!gunSys.IsPendingReload()) {
                        TryComputeAimTargets(task, out var b, out var e);
                        Turret.SetDesiredRotation(b);
                        gunSys.SetElevationTarget(e);
                        yield return null;
                    }
                }
                task.Fired = true;
                task.FiredAt = Time.time;
                Registry.MarkFired(task);
                InFlight.Add(task);
            }
```

> **AutoFire 开**：确认 + Arm → 复检对齐 → `Fire()` → `WaitFire()`。
> **AutoFire 关**：确认 + Arm 后就绪，持续跟踪 aim(t) 等玩家扳机（`IsPendingReload`）；玩家随时拉扳机打的都是当前提前点。手动任务一律静态，此循环退化为无操作等待。
> **容差兜底**：高装药压低漂移率（~0.3°/s），确认 ~4s 漂移 ~1°；10km 处 1° ≈ 175m < 毁伤半径 → `AimToleranceDeg ≈ 1°` 即可，不必卡严。

- [x] **Step 6: ReserveTurretAndRotate 改为连续跟踪**

装填期主流程被 LoadBullet/LoadPowder 阻塞，炮塔跟踪必须由后台协程承担。现"抢锁→转一次→Ready"改为"抢锁→装填期每帧追实时方位→主流程接棒"：

```csharp
    /// <summary>后台预约炮塔: 抢锁后每帧追实时目标方位（装填期仰角必须静止, 只转方位）。
    /// PostLoad 由主流程进入瞄准循环时置位; 之后主流程驱动双机构, 此处只持锁。</summary>
    private IEnumerator ReserveTurretAndRotate(ArtilleryTask task, TurretReservation res) {
        yield return _turretLock.Acquire();
        res.Acquired = true;
        if (res.Canceled) { ReleaseTurretOnce(res); yield break; }
        while (!res.Released) {
            if (!res.PostLoad && TryGetMovingBearing(task, out var bearing))
                Turret.SetDesiredRotation(bearing);   // 装填期追方位
            yield return null;
        }
    }
```

辅助：

```csharp
    /// <summary>移动目标当前方位（提前点方向, 冻结快照外推）。非移动返回 false。</summary>
    private bool TryGetMovingBearing(ArtilleryTask task, out float bearing) {
        bearing = task.angel;
        if (!task.IsMoving || !TargetLeadSolver.IsMoving(task.AimVel)) return false;
        var tof = ToFTable.FlightTime(task.distance, task.LoadedCharge);
        bearing = Bearing(TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel,
            Time.time - task.AimStartTime, tof));
        return true;
    }
```

`TurretReservation` 加 `PostLoad` 标志（Step 4 主循环进入时置 `res.PostLoad = true`；`Ready` 语义删除，收敛判定归主循环）。锁持有/释放语义（`ReleaseTurretOnce` 幂等）不变。

- [x] **Step 7: Commit**

```bash
git add IronNestFCS.Logic/FSC.cs
git commit -m "feat: unified formula aiming + continuous tracking + parallel calculator sync"
```

---

## Task 6: 退化路径 + 出射程/失速处理

**Files:**
- Modify: `IronNestFCS.Logic/FSC.cs`

- [x] **Step 1: 跟踪循环内加退化检查**

```csharp
                // 退化: 目标超出装药覆盖射程 → 落回变体A(单次静态解)或中止
                if (aimDistKm * 0.2f > task.LoadedCharge + 0.1f) {
                    // 装药不足 → 中止(避免打空炮)
                    task.progress = Progress.Failed;
                    yield break;
                }
                if (!gunSys.CanFire()) {
                    // 失速/目标丢失: 静态退化已由 IsMoving 判定覆盖; CanFire 超时走兜底
                }
```

> 目标中途失速：`TargetLeadSolver.IsMoving(vel)` 变 false → aim 退化为静态（p0 不变）→ 正常收敛击发，无需额外分支。

- [x] **Step 2: Commit**

```bash
git add IronNestFCS.Logic/FSC.cs
git commit -m "feat: moving-target degradation (out-of-range/coast-to-static)"
```

---

## Task 7: FcsModule 装配 + 面板移动标记

**Files:**
- Modify: `IronNestFCS.Logic/FcsModule.cs`
- Modify: `IronNestFCS.Logic/FcsWindow.cs`

- [x] **Step 1: PickTarget/OnGunIdle（自动）创建任务时拷移动快照**

`FcsModule.OnGunIdle` 的自动任务创建处加：

```csharp
        task.IsMoving = ti.IsMoving;
        task.AimP0 = ti.WorldPos;      // 世界单位
        task.AimVel = ti.Velocity;     // 世界单位/s（Task 2 平滑后）
        task.AimStartTime = Time.time;
```

（`TargetInfo.IsMoving/WorldPos/Velocity` 已有）

> **手动任务**（`FcsSceneInteractor` T1-T4/小键盘路径）**一律 `IsMoving=false`**（手动模式默认静态目标，不跟踪移动）。快照字段可留 0。

- [x] **Step 2: 面板炮位行显示移动标记**

`DrawGunRow` 首行追加 `task.IsMoving ? " [MOV]" : ""`。

- [x] **Step 3: Commit**

```bash
git add IronNestFCS.Logic/FcsModule.cs IronNestFCS.Logic/FcsWindow.cs
git commit -m "feat: mark moving tasks + panel indicator"
```

---

## Task 8: 编译验证 + 运行时验证清单（构建机）

- [ ] **Step 1: 全量构建**：`.\build.ps1` → BUILD SUCCEEDED

- [ ] **Step 2: 公式对拍（静态）**：静态目标若干距离，装饰驱动计算器时顺带读回输出，log 公式仰角 vs 计算器读数，误差 < 1°（只用于校验，不用于瞄准）

- [ ] **Step 3: 跳过计算器冒烟验证（已确认允许）**：装填完成后直接设仰角杆（不碰计算器），5 步确认 + 击发正常发射、落点正确

- [ ] **Step 4: 静态目标回归**：静态目标正常击发（公式仰角 vs 原计算器路径等效），落点正确

- [ ] **Step 5: 移动目标（匀速直线）**：雷达标移动目标 → 装填期方向机跟随 → 装填后仰角跟随 → 击发命中提前点，日志 `killed in flight` 出现、注册表 5s 内释放

- [ ] **Step 6: 双发防重**：炮 A 打移动目标期间，炮 B `PickTarget` 不选同一 entityId（注册表持有验证）

- [ ] **Step 7: 退化**：移动目标加速出射程 → 任务 Failed/中止，无卡死；目标中途失速 → 静态退化正常击发

- [ ] **Step 8: 计算器装饰回归**：每发装填期计算器拉一次（视觉正常），不读回、不参与瞄准

- [ ] **Step 9: 热重载回归**：F9 无残留、无协程泄漏（视觉同步协程在 `_runningCoroutines` 登记）

---

## 风险与已知事项

- **跳过计算器**：已确认允许（直接设仰角杆、不碰计算器），不再是最风险项——落点正确性仍靠 Task 8 Step 2/4 兜底
- **0.012 系数**：来自当前版本两源交叉验证，仍建议对拍（Task 8 Step 2）
- **几何提取**：`CalcAngle`/`CalcDistance` 的 `GameObject.Find` 每帧调用开销——抽共享辅助缓存 transform
- **单位约定**：位置=世界单位、距离=km（×3.8164）、速度=世界单位/s——代码注释已标，实施时严格一致
- **手动模式**：一律按静态目标处理（`IsMoving=false`），不跟踪移动——移动目标跟踪仅自动模式
- **装药定格**：`LoadedCharge` 装填后不可改；目标大幅加速超出覆盖 → 退化路径（Task 6）
