using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using IronNestFCS.Abstractions;
using IronNestFCS.Domain.Diagnostics;
using IronNestFCS.Domain.Observation;
using IronNestFCS.Domain.Planning;
using IronNestFCS.Logic.FCS;
using IronNestFCS.Logic.Planning;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public class FcsModule : IFcsModule
{
    private readonly FSC fcs = new();
    private FcsWindow? window;
    private TacticalRadar? radar;
    private MapOverlay? overlay;
    private ObservationAdapter? _observation;
    private readonly PlannerRollout _rollout = new();
    private readonly BaselinePlanner _planner = new();
    private readonly LegacySchedulerAdapter _adapter = new();

    private float lastScanTime;
    private float nextSweepTime;
    private float lastCbtPollTime;
    private float _lastMoveBlockLog;    // 紧急移动延迟日志节流
    private bool _lastFocused = true;   // 失焦/恢复诊断(2026-08-16 用户: Alt+Tab 后游戏未响应)
    private float _lastTimeScaleWarn;   // 聚焦但 timeScale=0 警告节流
    private bool autoMode;          // 全自动模式:true=雷达接管;false=手动(雷达完全休眠)
    private bool _emergencyMoveTriggered;  // 本危急周期已触发过紧急移动(防重复采购)
    private float _emergencyMoveAttemptTime; // 触发时刻(30s 无暂停 → 采购失败, 允许重试)
    private float _lastEmergencyMoveTime;   // 上次紧急移动成功时刻(冷却期内不重复花 65 点)
    private bool _emergencyMoveInProgress;  // 转移阵地进行中(暂停自动派发, 防炮管被重置后新任务卡死)
    private const float EmergencyMoveCooldown = 45f;  // 紧急移动冷却(秒): 防 FDC 暂停误触发连买

    /// <summary>
    /// 紧急移动: 采购 MoveZone 卡(65 点, 暂停倒计时到下一发落地 + 转移阵地)。
    /// 转移期间暂停自动派发(阵地移动会重置炮管状态, 弹被卸下 → 新任务瞄准段卡死);
    /// 正在执行的任务置 Canceled(瞄准/装填循环新增检查点, 干净退出释放锁/槽位);
    /// 等倒计时暂停生效(IsRunning=false)后复位, 并触发补派。
    /// </summary>
    private void StartEmergencyMove()
    {
        _emergencyMoveInProgress = true;
        fcs.ClearPendingTasks();   // 队列里的自动任务先清掉(阵地转移后目标角度全变)
        // 双管正在执行的任务置 Canceled: 转移阵地重置炮管状态(弹被卸下), 继续瞄准必卡死
        if (fcs.LeftTask != null) fcs.LeftTask.Canceled = true;
        if (fcs.RightTask != null) fcs.RightTask.Canceled = true;
        fcs.StartEmergencyMove(OnEmergencyMoveDone);
    }

    /// <summary>玩家手动转移阵地(手动买 MoveZone 卡)后的清理(2026-08-16 用户):
    /// 游戏照常重置炮管状态, 但我们没采购卡——跳过采购, 复用转移后清理
    /// (停派/取消任务/清洗炮管/等基准静止), 防任务卡死(玩家反馈 bug)。</summary>
    private void StartManualMoveCleanup()
    {
        _emergencyMoveInProgress = true;
        fcs.ClearPendingTasks();
        if (fcs.LeftTask != null) fcs.LeftTask.Canceled = true;
        if (fcs.RightTask != null) fcs.RightTask.Canceled = true;
        fcs.StartTransferCleanup(OnEmergencyMoveDone);
    }

    /// <summary>紧急移动完成(倒计时暂停生效或超时) → 恢复自动派发。</summary>
    private void OnEmergencyMoveDone()
    {
        _emergencyMoveInProgress = false;
        _lastEmergencyMoveTime = Time.time;
        nextSweepTime = 0;   // 立即补派一轮
        OnGunIdle();
    }

    public bool Initialize()
    {
        window = new FcsWindow(fcs, _rollout);
        radar = new TacticalRadar(fcs);
        overlay = new MapOverlay(fcs);
        _observation = new ObservationAdapter(fcs, radar);
        fcs.EntityLocator = radar;   // 手动任务目标解析
        fcs.OnGunIdle += OnGunIdle;
        bool bound = fcs.TryBind();
        if (bound) fcs.SyncTurretPiece();   // 开局强制归位 Piece 一次(忘了放也准)
        return bound;
    }

    /// <summary>任一炮管退膛 → 捕获快照 → Baseline 规划/派发。扫荡中每 5s 也跑一次，用于在飞窗口到期后恢复派发。
    /// PlanningDisabled: 只停自动规划——不创建任何自动任务, 手动 FIFO 派发与在途/活动任务不受影响。</summary>
    private void OnGunIdle()
    {
        if (!autoMode) return;
        if (Time.time < nextSweepTime) return;
        nextSweepTime = Time.time + 0.5f;

        if (radar == null || !fcs.IsBound) return;
        // 紧急移动转移阵地期间不派发: 阵地移动会重置炮管状态(弹被卸下), 新任务会在瞄准段卡死。
        // 转移完成后(倒计时暂停生效)由 EmergencyMoveRoutine 复位标志并触发一次补派。
        if (_emergencyMoveInProgress) return;
        AdjustAllValves(0f);

        // 观测捕获(内部雷达扫描一次): 无效观测 fail-closed——
        // 不清队列、不规划、不派发, 保留上次可信数据, 绝不对账空存活集。
        var snapshot = _observation!.Capture().Current;
        var freeBarrels = (snapshot.LeftBarrel.HasActiveTask ? 0 : 1) + (snapshot.RightBarrel.HasActiveTask ? 0 : 1);
        if (!snapshot.IsValid)
        {
            _rollout.RecordInvalid(snapshot.InvalidReason);
            MelonLogger.Warning($"[FCS] scan invalid: {snapshot.InvalidReason} — keep last data, no dispatch this cycle");
            MelonLogger.Msg(FormatGoldenLine(snapshot.Version, false, freeBarrels, snapshot.PendingManualTaskCount, snapshot.InvalidReason, "-"));
            return;
        }

        // T1–T4 手动 FIFO 优先于自主规划(PlanningDisabled 下同样生效):
        // 手动队列非空 → 跳过自动规划, 不清队列、不取消活动任务,
        // 只触发一次派发让手动任务占位已空闲炮管(唯一分配路径)。
        if (fcs.QueueCan.Any(t => t.Source == TaskSource.Manual))
        {
            fcs.DispatchPending();
            MelonLogger.Msg(FormatGoldenLine(snapshot.Version, true, freeBarrels, snapshot.PendingManualTaskCount, null, "-"));
            return;
        }

        // PlanningDisabled: 不创建任何自动任务; 手动 FIFO 与在途/活动任务不受影响。
        if (_rollout.Authority == PlannerAuthority.PlanningDisabled)
        {
            MelonLogger.Msg(FormatGoldenLine(snapshot.Version, true, freeBarrels, snapshot.PendingManualTaskCount, null, "disabled"));
            return;
        }

        RunBaselineAuthority(snapshot, freeBarrels);
    }

    /// <summary>Baseline 权威(唯一自动决策源): 规划 → 转换 → 入队。
    /// 转换全有或全无, 先于第一个副作用; 任何失败本扫描不派发(无回退路径)。</summary>
    private void RunBaselineAuthority(BattleSnapshot snapshot, int freeBarrels)
    {
        EngagementPlan plan;
        try
        {
            plan = _planner.Plan(snapshot);
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[FCS] baseline plan exception scan={snapshot.Version}: {ex} — no dispatch this cycle");
            return;
        }

        if (plan.Groups.Count == 0)
        {
            switch (plan.NoPlanReason)
            {
                case NoPlanReason.ManualQueuePending:
                case NoPlanReason.NoFreeBarrel:
                case NoPlanReason.NoEligibleTarget:
                    // 无计划也要先清旧自动队列, 防陈旧快照任务稍后继续发射
                    // (手动任务不受影响: ClearPendingTasks 只清 Auto)。
                    fcs.ClearPendingTasks();
                    MelonLogger.Msg(FormatGoldenLine(snapshot.Version, true, freeBarrels, snapshot.PendingManualTaskCount, null, PlanFingerprint.Format(plan)));
                    return;
                case NoPlanReason.ObservationInvalid:
                    return;
                default:
                    MelonLogger.Error($"[FCS] baseline contract: no groups with reason {plan.NoPlanReason} — no dispatch this cycle");
                    return;
            }
        }

        var result = _adapter.TryConvert(plan, snapshot.Version, Time.time);
        if (!result.Accepted)
        {
            MelonLogger.Warning($"[FCS] baseline adapter rejected scan={snapshot.Version}: " +
                (result.Rejection != PlanRejection.None ? result.Rejection.ToString() : result.Detail ?? "adapter-rejected") +
                " — no dispatch this cycle");
            return;
        }

        // 转换成功后才清旧自动队列, 按计划顺序入队(入队顺序即分配顺序)。
        fcs.ClearPendingTasks();
        var anyEnqueued = false;
        try
        {
            foreach (var task in result.Tasks)
            {
                fcs.EnqueueTask(task);
                anyEnqueued = true;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[FCS] baseline enqueue interrupted scan={snapshot.Version} enqueued={anyEnqueued}: {ex}");
            return;
        }
        MelonLogger.Msg(FormatGoldenLine(snapshot.Version, true, freeBarrels, snapshot.PendingManualTaskCount, null, PlanFingerprint.Format(plan)));
    }

    /// <summary>结构化诊断行(格式不变, 权威来源为 Baseline 计划本身):
    /// decision 字段承载本扫描的 Baseline 计划指纹; "-" = 未规划(手动 FIFO/停用), "disabled" = PlanningDisabled。</summary>
    private static string FormatGoldenLine(long scanVersion, bool valid, int freeBarrels, int manualCount, string? failure, string decisionFingerprint) =>
        $"[PLAN-GOLDEN] scan={scanVersion}|valid={(valid ? 1 : 0)}|free={freeBarrels}|manual={manualCount}|baseline={decisionFingerprint}|reason={failure ?? "none"}";

    public void Update()
    {
        fcs.Update();
        overlay?.Update();   // 地图 overlay: 打击中任务可视化(内部 1Hz 节流)

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
            OnGunIdle();
        }

        // 紧急移动自动触发(§3.5): 危急(阈值内) 且积分 ≥ 基金线(90) → 采购 MoveZone 卡(65 点,
        // 暂停倒计时到下一发落地 + 转移阵地)。仅全自动模式。
        if (fcs.IsBound && autoMode && Time.time - lastCbtPollTime > 5f)
        {
            lastCbtPollTime = Time.time;
            // 统一紧急链路(2026-08-16 用户设计): CRITICAL 检测 → 紧急状态。
            // 积分分流: 有积分(≥基金线90) → 紧急移动(MoveZone 65 点, 暂停+转移阵地);
            // 没积分 → 派发层走 FDC(免费暂停, 由规划层 FdcDispatchable 把关)。
            if (fcs.Cbt.ShouldEmergencyMove() && !_emergencyMoveTriggered
                // FDC 暂停窗口内不触发: FDC 击杀暂停已生效, 此时买 MoveZone 65 点
                // 重复花(暂停部分浪费)+ 转移阵地重置炮管 → 组合拳被毁。
                // 等暂停解除(下一发落地)后再评估, 紧急移动的暂停从干净状态开始。
                && !fcs.Cbt.IsPausedWindow
                && Time.time - _lastEmergencyMoveTime > EmergencyMoveCooldown)
            {
                // 暂停收益保护(2026-08-15): 任何在飞炮弹落地都会立即解除暂停——若此刻有弹在飞,
                // 触发紧急移动的暂停窗口会被压缩到"触发→那发弹落地", 65 点白花。
                // 正确做法: 等所有在飞弹落地(落地瞬间=装填间隙)再触发, 窗口从干净状态开始,
                // 完整覆盖下一轮装填+飞行。判定与 InFlight 移除同源(射表), 落地后下周期放行。
                float soonestLanding = fcs.SoonestImpactIn();
                if (soonestLanding > 0f)
                {
                    if (Time.time - _lastMoveBlockLog > 20f)
                    {
                        _lastMoveBlockLog = Time.time;
                        MelonLogger.Msg($"[CBT] 危急但有在飞弹({soonestLanding:F0}s后落地), 等落地后再紧急移动(避免暂停被立即解除)");
                    }
                }
                else
                {
                    _emergencyMoveTriggered = true;
                    _emergencyMoveAttemptTime = Time.time;
                    MelonLogger.Msg($"[CBT] 危急: 触发紧急移动 (remain={fcs.Cbt.TimeRemaining:F1}s pts={fcs.Cbt.RequisitionPoints})");
                    StartEmergencyMove();
                }
            }
            // 倒计时已暂停(紧急移动卡生效) → 允许下次再次触发
            if (_emergencyMoveTriggered && !fcs.Cbt.IsRunning)
                _emergencyMoveTriggered = false;
            // 采购失败兜底: 触发 30s 后倒计时仍 ≥阈值(未暂停) → 允许重试
            else if (_emergencyMoveTriggered && Time.time - _emergencyMoveAttemptTime > 30f)
                _emergencyMoveTriggered = false;
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

        // Ctrl+Numpad7: 紧急停用开关(Baseline ↔ PlanningDisabled)。
        // PlanningDisabled 只停自动规划: 不创建任何自动任务; T1–T4 FIFO、在途/活动任务不受影响。
        if (kb.numpad7Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit7Key.wasPressedThisFrame))
        {
            _rollout.ToggleAuthority();
            MelonLogger.Msg($"[FCS] planner mode: {_rollout.Authority}" +
                (_rollout.Authority == PlannerAuthority.PlanningDisabled ? " (auto planning disabled, manual FIFO unaffected)" : ""));
        }

        // Numpad 8: CBT 测试档位切换(2026-08-16 用户: 注释掉, 不再用测试档)
        // if (kb.numpad8Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit8Key.wasPressedThisFrame))
        //     fcs.Cbt.CycleTestMode();

        // Numpad 9: CBT 扫描探针(2026-08-16 用户: 注释掉, 临时调试已完成)
        // if (kb.numpad9Key.wasPressedThisFrame)
        //     fcs.CbtScanProbe();
    }

    /// <summary>切换全自动/手动模式。切手动时清空自动队列(正在打的不打断,打完自然停)。</summary>
    private void ToggleAutoMode()
    {
        autoMode = !autoMode;
        nextSweepTime = 0;  // 重设时间窗，防止立即触发的首轮被防重入窗口跳过
        if (!autoMode)
        {
            fcs.ClearPendingTasks();   // 只清自动入队的队列, 手动任务保留
            CancelOrForceFire(fcs.LeftTask);
            CancelOrForceFire(fcs.RightTask);
            MelonLogger.Msg("[FCS] 手动模式:雷达休眠,手动标点 T1-T4 接管");
        }
        else
        {
            OnGunIdle();
            MelonLogger.Msg("[FCS] 全自动模式:雷达接管");
        }
        if (window != null) window.AutoSweepEnabled = autoMode;
    }

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

    public void OnGui()
    {
        window?.OnGui();
        radar?.OnGui();
    }

    public void Shutdown()
    {
        fcs.OnGunIdle -= OnGunIdle;
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
