using System;
using System.Linq;
using System.Collections.Generic;
using Il2Cpp;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public class FcsModule : IFcsModule, IFcsSessionState
{
    private readonly FSC fcs = new();
    private FcsWindow? window;
    private TacticalRadar? radar;
    private MapOverlay? overlay;

    private float lastScanTime;
    private float nextSweepTime;
    private bool _lastFocused = true;   // 失焦/恢复诊断(2026-08-16 用户: Alt+Tab 后游戏未响应)
    private float _lastTimeScaleWarn;   // 聚焦但 timeScale=0 警告节流
    private bool autoMode;          // 全自动模式:true=雷达接管;false=手动(雷达完全休眠)
    private bool _emergencyMoveInProgress;  // 转移阵地清理进行中(手动转移; 暂停自动派发, 防炮管被重置后新任务卡死)
    private const float EmergencyMoveCleanupTimeout = 120f;
    private float _emergencyMoveStartedAt;
    private int _emergencyMoveGeneration;
    private OwnedRoutine? _emergencyMoveCleanupHandle;
    public string[] CaptureAutomaticExclusions() => fcs.AutomaticExclusions.Snapshot();
    public void RestoreAutomaticExclusions(string[] ids) => fcs.AutomaticExclusions.Restore(ids);
    public void RestoreReloadFlightRisk(bool possible) => fcs.RestoreReloadFlightRisk(possible);

    /// <summary>玩家手动转移阵地(手动买 MoveZone 卡)后的清理(2026-08-16 用户):
    /// 游戏照常重置炮管状态, 但我们没采购卡——暂停自动计划并复用转移后清理
    /// (停派/机构交接/清洗炮管/等基准静止), 防任务卡死(玩家反馈 bug)。</summary>
    private void StartManualMoveCleanup()
    {
        _emergencyMoveInProgress = true;
        _emergencyMoveStartedAt = Time.realtimeSinceStartup;
        var generation = ++_emergencyMoveGeneration;
        fcs.PauseAutomaticExecution("阵地移动开始");
        _emergencyMoveCleanupHandle = fcs.StartTransferCleanup(() => OnEmergencyMoveDone(generation),
            () => _emergencyMoveInProgress && generation == _emergencyMoveGeneration
                && Time.realtimeSinceStartup - _emergencyMoveStartedAt < EmergencyMoveCleanupTimeout);
    }

    /// <summary>转移后清理完成 → 恢复自动派发。</summary>
    private void OnEmergencyMoveDone(int generation)
    {
        // 超时后迟到的回调不能结束下一次转移清理。
        if (generation != _emergencyMoveGeneration || !_emergencyMoveInProgress) return;
        _emergencyMoveInProgress = false;
        _emergencyMoveCleanupHandle = null;
        fcs.ResetTransferBaseline();   // 重设检测基准, 防自己触发的移动 82ms 后二次误清理(2026-08-18)
        nextSweepTime = 0;   // 立即补派一轮
        if (autoMode)
        {
            // 先刷新雷达快照，再恢复暂停计划；否则暂停前的距离可能被沿用。
            radar?.Scan();
            fcs.ResumeAutomaticExecution();
        }
        OnGunIdle();
    }

    public bool Initialize()
    {
        window = new FcsWindow(fcs);
        window.ToggleAutoModeRequested = ToggleAutoMode;
        window.StopRequested = () => fcs.RequestPlayerStop("玩家紧急停止");
        window.StartAutomaticRequested = StartAutomatic;
        fcs.PlayerStopRequested += OnPlayerStop;
        radar = new TacticalRadar(fcs);
        overlay = new MapOverlay(fcs);
        fcs.EntityLocator = radar;   // 手动任务目标解析
        fcs.OnGunIdle += OnGunIdle;
        bool bound = fcs.TryBind();
        if (bound) fcs.SyncTurretPiece();   // 开局强制归位 Piece 一次(忘了放也准)
        return bound;
    }

    /// <summary>任一炮管退膛 → 扫描 → 清空队列 → 给每门空闲炮管各派一个目标。扫荡中每 5s 也跑一次，用于在飞窗口到期后恢复派发。</summary>
    private void OnGunIdle()
    {
        if (!autoMode) return;
        if (fcs.AutomaticExecutionPaused) return;
        if (Time.time < nextSweepTime) return;
        nextSweepTime = Time.time + 0.5f;

        if (radar == null || !fcs.IsBound) return;
        // 紧急移动转移阵地期间不派发: 阵地移动会重置炮管状态(弹被卸下), 新任务会在瞄准段卡死。
        // 转移完成后(倒计时暂停生效)由 EmergencyMoveRoutine 复位标志并触发一次补派。
        if (_emergencyMoveInProgress) return;
        AdjustAllValves(0f);
        radar.Scan();

        fcs.ClearPendingTasks();   // 只清自动任务, 手动任务保留

        // 优先为保留整发找合适目标；空管随后使用正常弹种推荐。
        int nextTargetId = 1;
        var sides = new[] { LeftRight.Left, LeftRight.Right }.OrderByDescending(side =>
            (side == LeftRight.Left ? fcs.LeftGun : fcs.RightGun).AssessChamber()
                == GunSystem.ChamberState.ReadyToFire);
        foreach (var barrel in sides)
        {
            if (barrel == LeftRight.Left ? fcs.LeftTask != null : fcs.RightTask != null) continue;
            var task = PickTaskForGun(barrel, nextTargetId);
            if (task == null) continue;
            nextTargetId++;
            fcs.EnqueueTask(task);
        }
    }

    /// <summary>单点自动任务(非集群路径): 现有选择/弹种/移动快照逻辑。BlastRadiusKm 供击发爆区注册。
    /// CBT 双轨装药(§3.2): 吃紧/危急 → useMaxCharge=true(满装 6 包抢节奏), 其余最低装药。
    /// CBT 基金纪律(§3.6): fundMargin 传入弹种选择(AP 仅 3★ 装甲且扣款后 ≥ 基金线)。</summary>
    private static ArtilleryTask CreateAutoTask(TacticalDecider.TargetInfo ti, int targetId, bool useMaxCharge, float fundMargin)
    {
        var task = new ArtilleryTask
        {
            targetId = targetId,
            entityId = ti.EntityId,
            entityName = ti.Name,
            angel = ti.Angle,
            distance = ti.Distance,
            position = ti.WorldPos,
            bulletType = TacticalDecider.ChooseShellType(ti, fundMargin),
            useMaxCharge = useMaxCharge,
            IsFdc = ti.IsFdc,
            Source = TaskSource.Auto
        };
        // 移动目标冻结快照: 提前量解算全程从这三个字段外推(匀速假设, 不查雷达)。
        // 速度未建立(刚出现)时标记 VelocityUnknown: 装填期从雷达采纳后再外推。
        task.IsMoving = ti.IsMoving;
        task.AimP0 = ti.WorldPos;
        task.AimVel = ti.Velocity;
        task.AimStartTime = Time.time;
        task.VelocityUnknown = !ti.VelocityKnown;
        task.BlastRadiusKm = ShellData.BlastRadiusKm(task.bulletType);
        return task;
    }

    private ArtilleryTask? PickTaskForGun(LeftRight side, int targetId)
    {
        if (radar == null) return null;
        var gun = side == LeftRight.Left ? fcs.LeftGun : fcs.RightGun;
        var state = gun.AssessChamber();
        if (state == GunSystem.ChamberState.Dirty) return null;
        var retained = state == GunSystem.ChamberState.ReadyToFire;
        var shell = default(BulletType);
        if (state != GunSystem.ChamberState.Empty
            && !Enum.TryParse(gun.BulletInChamber(), out shell)) return null;
        var charge = gun.ChamberCharges();
        foreach (var t in radar.AliveHostiles)
        {
            if (fcs.AutomaticExclusions.Contains(t.EntityId)
                || fcs.Registry.IsHandled(t.EntityId) || fcs.Registry.IsHandledNear(t.WorldPos, 0f)) continue;
            if (t.IsFdc || t.IsArmored || t.IsUnderground) continue;
            var task = CreateAutoTask(t, targetId, false, float.MaxValue);
            task.PreferredSide = side;
            if (retained)
            {
                if (!RetainedAmmoPolicy.CanUse(shell, charge, t.Distance,
                    t.IsArmored, t.IsUnderground, t.ImmuneShells)) continue;
                if (radar.AllyPositions.Any(ally => (ally - t.WorldPos).magnitude * ShellData.KmPerWorldUnit
                    <= ShellData.BlastRadiusKm(shell))) continue;
                task.bulletType = shell;
                task.LoadedCharge = charge;
                task.UsesRetainedRound = true;
                task.BlastRadiusKm = ShellData.BlastRadiusKm(shell);
            }
            else if (state == GunSystem.ChamberState.ShellLoaded && shell != task.bulletType)
                continue;
            return task;
        }
        return null;
    }

    public void Update()
    {
        fcs.PointerOverCommandPanel = window?.PointerInsidePanel() ?? false;
        fcs.Update();
        overlay?.Update();   // 地图 overlay: 打击中任务可视化(内部 1Hz 节流)

        if (_emergencyMoveInProgress
            && Time.realtimeSinceStartup - _emergencyMoveStartedAt >= EmergencyMoveCleanupTimeout)
        {
            // 恢复前先停止旧清理，防它稍后继续清洗已交给新计划的炮管。
            if (_emergencyMoveCleanupHandle != null)
            {
                try { _emergencyMoveCleanupHandle.Stop(); }
                catch (System.Exception ex)
                {
                    // 嵌套动作仍受同一个实时授权闸门约束，下一步不会继续执行。
                    MelonLogger.Error($"[FCS] 停止移动清理句柄失败，旧清理授权已超时: {ex}");
                }
            }
            MelonLogger.Error("[FCS] 阵地移动清理超过 120 秒，停止旧清理并解除模块暂停；未完成炮位交接仍保持隔离");
            OnEmergencyMoveDone(_emergencyMoveGeneration);
        }

        // 失焦/恢复诊断(2026-08-16 用户: Alt+Tab 后游戏未响应——怀疑失焦暂停 timeScale=0
        // 未恢复的假死)。日志能打 = 假死(Update 还在跑, 可强制恢复 timeScale);
        // 日志停 = 真卡死(主线程死循环, 与 mod 无关, 游戏自身 bug)。
        bool focused = Application.isFocused;
        if (focused != _lastFocused)
        {
            _lastFocused = focused;
            MelonLogger.Msg($"[FCS] 焦点变化: isFocused={focused} timeScale={Time.timeScale} (Alt+Tab 未响应排查)");
        }
        else if (focused && Time.timeScale == 0f && Time.time - _lastTimeScaleWarn > 5f)
        {
            // 聚焦但 timeScale=0: 失焦暂停未恢复(游戏假死) —— 先只日志, 确认后加恢复保护
            _lastTimeScaleWarn = Time.time;
            MelonLogger.Warning($"[FCS] 聚焦但 timeScale=0 (失焦暂停未恢复? 游戏可能未响应)");
        }

        // 被动扫描:全自动模式下每 5s 周期重扫+派发(在飞窗口到期后恢复派发,或双管全空时补派);
        // 手动模式雷达完全休眠
        if (radar != null && fcs.IsBound && autoMode && Time.time - lastScanTime > 5f)
        {
            lastScanTime = Time.time;
            radar.Scan();
            OnGunIdle();
        }

        // 玩家手动紧急移动检测(2026-08-16 用户): 手动买 MoveZone 卡/转移阵地 → 游戏移动
        // TurretLocation(基准突变) → 复用转移后清理(停派/取消任务/清洗炮管), 防任务卡死
        // (玩家反馈 bug: 手动移动后流程卡住)。自动紧急移动期间跳过(自身在清理)。
        if (fcs.IsBound && autoMode && !_emergencyMoveInProgress && fcs.DetectManualTransferMove())
        {
            MelonLogger.Msg("[FCS] 检测到玩家手动转移阵地, 执行转移后清理(取消任务+清洗炮管)");
            StartManualMoveCleanup();
        }

        // 手柄:Select 键切换全自动/手动模式(等价 Numpad 0)
        var gp = Gamepad.current;
        if (gp != null && gp.selectButton.wasPressedThisFrame && fcs.IsBound)
        {
            ToggleAutoMode();
            return;
        }

        var kb = Keyboard.current;
        if (kb == null || !fcs.IsBound)
            return;

        // Numpad 0: 切换全自动/手动模式
        if (kb.numpad0Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit0Key.wasPressedThisFrame))
        {
            ToggleAutoMode();
            return;
        }

        // Numpad 1-4: manual fire targets
        if (kb.numpad1Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit1Key.wasPressedThisFrame))
            fcs.FireTarget(1);
        else if (kb.numpad2Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit2Key.wasPressedThisFrame))
            fcs.FireTarget(2);
        else if (kb.numpad3Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit3Key.wasPressedThisFrame))
            fcs.FireTarget(3);
        else if (kb.numpad4Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit4Key.wasPressedThisFrame))
            fcs.FireTarget(4);

    }

    /// <summary>切手动撤销所有现有执行；AUTO 从实物和当前目标重新规划。</summary>
    private void ToggleAutoMode()
    {
        if (autoMode) fcs.RequestPlayerStop("切回手动，停止现有执行");
        else StartAutomatic();
    }

    private void OnPlayerStop()
    {
        autoMode = false;
        nextSweepTime = 0;
        if (window != null) window.AutoSweepEnabled = false;
        // 转移清理结束后读取 autoMode，不能越过这次停止重新开火。
    }

    private void StartAutomatic()
    {
        autoMode = true;
        nextSweepTime = 0;
        if (window != null) window.AutoSweepEnabled = true;
        if (_emergencyMoveInProgress) return;
        radar?.Scan();
        fcs.ResumeAutomaticExecution();
        OnGunIdle();
    }

    public void OnGui()
    {
        window?.OnGui();
        radar?.OnGui();
    }

    public void Shutdown()
    {
        fcs.OnGunIdle -= OnGunIdle;
        fcs.PlayerStopRequested -= OnPlayerStop;
        overlay?.Shutdown();   // 销毁全部 overlay 渲染对象
        fcs.Dispose();
        window = null;
        radar = null;
        overlay = null;
    }

    /// <summary>找到所有蒸汽泄漏点，收紧最近阀门到指定值（0=拧紧, 999=全开）</summary>
    private static void AdjustAllValves(float value)
    {
        var dials = new List<(DialInteractable di, Vector3 pos)>();
        foreach (var go in Object.FindObjectsOfType<GameObject>(true))
        {
            var di = go.GetComponent<DialInteractable>();
            if (di != null) dials.Add((di, go.transform.position));
        }
        int done = 0;
        foreach (var go in Object.FindObjectsOfType<GameObject>(true))
        {
            if (go == null || !go.name.ToLower().Contains("steam leak")) continue;
            DialInteractable? nearest = null;
            float minDist = float.MaxValue;
            foreach (var (di, pos) in dials)
            {
                var d = (pos - go.transform.position).magnitude;
                if (d < minDist) { minDist = d; nearest = di; }
            }
            if (nearest == null) continue;
            nearest.SetDialValue(value);
            done++;
        }
        // 阀门日志太吵已移除; 拧紧功能保留
    }
}
