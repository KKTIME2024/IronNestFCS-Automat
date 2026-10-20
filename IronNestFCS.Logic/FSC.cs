using HarmonyInstance = HarmonyLib.Harmony;
using System.Collections;
using System.Linq;
using System.Reflection;
using Il2Cpp;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public enum LeftRight {
    Left,
    Right,
}

/// <summary>
/// 纯火控领域逻辑：查找游戏对象、读取游戏数据、操控游戏内交互（dial 等）。
/// 不含任何 UI / IMGUI / 生命周期框架代码——那些在 <see cref="FcsModule"/> 和 <see cref="FcsWindow"/> 里。
///
/// 重载安全规则：
///  - 不要在这里注册新的 IL2CPP 类型（同一类型进程内只能注册一次）。
///  - 每次实例用独立的 Harmony 实例；Shutdown 时 UnpatchSelf。
///  - 所有对 IL2CPP 对象的引用在 Shutdown 时清空，便于旧 ALC 回收。
/// </summary>
public class FSC
{
    private const string HarmonyId = "com.svr2kos2.ironnestfcs.logic";

    // 驻留装药补给:装药低于阈值时每 5s 补一包(deskLock 保护),与任务内购买互补。
    private const float PowderCheckInterval = 5f;
    private const int PowderReplenishThreshold = 6;

    private HarmonyInstance? _harmony;
    
    private FcsSceneInteractor _sceneInteractor;
    private readonly PurchaseDeck _purchaseDeck = new();
    public readonly MapTable MapTable = new MapTable();
    public readonly BallisticCalculator BallisticCalculator = new BallisticCalculator();
    public readonly GunSystem LeftGun = new GunSystem();
    public readonly GunSystem RightGun = new GunSystem();
    public readonly Turret Turret = new Turret();
    public readonly TriggerConsole TriggerConsole = new();
    
    // ===== 任务调度 =====
    // 用户不再指定炮管：任务入队后由调度器派给空闲炮管，炮管打完一发自动拉下一个。
    // 所有读写都在 Unity 主线程（入队来自点击回调，派发/完成来自协程），无并发，无需锁。
    private readonly Queue<ArtilleryTask> _taskQueue = new();

    /// <summary>人机协同注册表: 在飞/排队/炮上目标的统一承包登记(手动+自动共用)</summary>
    public readonly TargetRegistry Registry = new();
    public readonly AutomaticTargetExclusions AutomaticExclusions = new();

    /// <summary>P1 计划身份与授权注册表。当前一发任务对应一个隐式单发计划。</summary>
    public readonly FirePlanRegistry PlanRegistry = new();


    /// <summary>已击发、炮弹仍在飞行中的任务(面板倒计时显示)。归零(=射表估计落地)时移除。</summary>
    public readonly List<ArtilleryTask> InFlight = new();

    /// <summary>独立于炮位任务槽位的提交／出膛／结果记录。</summary>
    public readonly List<FireRecord> FireRecords = new();

    /// <summary>机构就绪容差(度): 双机构与 aim 差小于此值且 CanFire 才进入确认。
    /// 0.1°——1°@10km≈175m 已超 HE 致死半径(120m), "对准"可能是打偏; 0.1°@10km≈17.5m
    /// 远小于任何毁伤半径。机构 4°/s、漂移 ~0.3°/s, 收敛有余量。与齐射门容差一致。</summary>
    private const float AimToleranceDeg = 0.1f;
    /// <summary>Piece 自动归位容差(地图局部单位): 超过此值视为放错位置, 拉回真实炮塔。过小会每 5s 抖动。</summary>
    private const float TurretPieceSnapTolerance = 0.01f;
    /// <summary>移动任务装药预测: 额外覆盖装填时长不确定性的距离余量(km)</summary>
    private const float MovingDistanceMarginKm = 1.5f;

    // 连续瞄准几何缓存(Draggable Surface + turret), 避免每帧 GameObject.Find
    private Transform? _mapSurface;
    private Transform? _turretXf;

    /// <summary>手动任务目标解析器(FcsModule 创建雷达后注入)</summary>
    public TacticalRadar? EntityLocator { get; set; }

    /// <summary>当前各炮管正在执行的任务；null 表示该炮管空闲。供 UI 显示与调度判断。</summary>
    public ArtilleryTask? LeftTask { get; private set; }
    public ArtilleryTask? RightTask { get; private set; }

    /// <summary>是否有 FDC 任务在活动(装填/瞄准/在飞/队列, 未结束)。
    /// 2026-08-16 用户: FDC 一次只派一个——双管同时打 2 个 FDC, 两发几乎同时落地,
    /// 第一个 FDC 触发的暂停会被第二个 FDC 弹落地立即解除, 窗口重叠浪费。
    /// 08-16 修正: 【在飞弹也算活动】——FDC 弹击发后任务已退壳(Finished)但弹未落地,
    /// 旧判定会放行第二个 FDC → 双 FDC 弹重叠(13:29 实测)。</summary>
    public bool HasActiveFdcTask
    {
        get
        {
            if (LeftTask is { IsFdc: true } && LeftTask.progress is not (Progress.Finished or Progress.Canceled or Progress.Failed)) return true;
            if (RightTask is { IsFdc: true } && RightTask.progress is not (Progress.Finished or Progress.Canceled or Progress.Failed)) return true;
            foreach (var t in _taskQueue) if (t.IsFdc) return true;
            foreach (var t in InFlight) if (t.IsFdc) return true;   // FDC 弹在飞(未落地/未确认)也算活动
            return false;
        }
    }

    /// <summary>等待派发的任务数（已入队但还没分到炮管）。供 UI 显示。</summary>
    public int PendingCount => _taskQueue.Count;
    public Queue<ArtilleryTask> QueueCan => new Queue<ArtilleryTask>(_taskQueue);
    public bool AutomaticExecutionPaused => _automaticExecutionPaused;
    public bool UnobservedFlightPossible { get; private set; }
    public void RestoreReloadFlightRisk(bool possible) => UnobservedFlightPossible = possible;
    public bool PointerOverCommandPanel { get; set; }
    public string ExecutionBlockReason { get; private set; } = "";
    private string _lastReportedExecutionBlock = "";

    private void ReportExecutionBlock(string reason)
    {
        ExecutionBlockReason = reason;
        if (reason == _lastReportedExecutionBlock) return;
        _lastReportedExecutionBlock = reason;
        if (reason.Length == 0) return;
        MelonLogger.Warning($"[FCS] 派发阻塞: {reason}; paused={_automaticExecutionPaused} "
            + $"flightUnknown={UnobservedFlightPossible} recovery={TriggerConsole.RecoveryPending} "
            + $"queue={PendingCount}; Left[{LeftGun.ExecutionState()}]; Right[{RightGun.ExecutionState()}]");
    }

    /// <summary>
    /// 控制台互斥锁：保护弹道计算器、确认开关台、采购台这三组全局唯一的"短操作"硬件。
    /// 临界区都很短（解算 / 确认弹 / 击发前的确认+击发），用完即放。
    /// </summary>
    private readonly CoroutineLock _deskLock = new();

    /// <summary>
    /// 炮塔方向角锁：方向角是全炮塔共享的，且一旦为某任务转到位，必须独占到这一发打出去为止
    /// （中途被另一任务转走就会打偏）。与 <see cref="_deskLock"/> 分开，是为了让本任务能在
    /// 后台早早抢占炮塔、与装填/升仰角重叠，而不挡住另一管炮在 deskLock 上的解算。
    ///
    /// 防死锁：凡同时需要两把锁处，一律"先 turret 后 desk"。本类只有击发段会嵌套两把锁
    /// （此时炮塔已由后台预约持有，再去抢 desk），解算/确认弹只单独用 desk，故无环、不死锁。
    /// </summary>
    private readonly CoroutineLock _turretLock = new();
    private float _lastPieceSyncTime;   // Piece 自动归位节流

    // ===== 任务看门狗 =====
    // 协程死亡(异常/被 Stop/子协程 yield 卡死)后, 槽位 LeftTask/RightTask 可能残留非 null,
    // TryDispatch 永远认为炮管忙 → 调度停摆且切 MANUAL/AUTO 都救不回(它们只改 autoMode)。
    // 协程内超时(装填 120s/瞄准 240s)依赖协程存活, 协程死了就失效 → 必须在协程外(Update)兜底。
    private ArtilleryTask? _watchdogTaskLeft;
    private Progress _watchdogProgressLeft = Progress.Pending;
    private float _watchdogSinceLeft;
    private ArtilleryTask? _watchdogTaskRight;
    private Progress _watchdogProgressRight = Progress.Pending;
    private float _watchdogSinceRight;
    private float _lastWatchdogTime;   // 看门狗 5s 节流(低频兜底, 阈值都是 90s+)
    // 任务→炮塔预约映射(看门狗强制释放用): 任务协程创建预约时登记, 结束后移除。
    private readonly Dictionary<ArtilleryTask, TurretReservation> _taskTurretRes = new();
    private readonly ExecutionLeaseLedger _slotLeases = new();
    private readonly HashSet<ArtilleryTask> _activeTaskRoutines = new();
    private bool _automaticExecutionPaused = true;
    private readonly ExecutionControl _execution = new();
    private bool _stoppingExecution;
    public event Action? PlayerStopRequested;
    public void RequestPlayerStop(string reason)
    {
        StopAllExecution(reason);
        PlayerStopRequested?.Invoke();
    }
    private readonly HashSet<ArtilleryTask> _armCleanupInProgress = new();
    private readonly HashSet<ArtilleryTask> _unknownFireHoldInProgress = new();
    private readonly Dictionary<Progress, float> _watchdogLimit = new()
    {
        [Progress.SelectingBullet] = 90f,   // 采购+回退链最长 ~4 次采购
        [Progress.LoadingBullet] = 120f,    // 装填(转弹仓+推弹)机械时间; 2026-08-18 事故后 240→120
                                           // (健康 ≤~75s, 卡死时 60s 真实秒死等 → 30s)
        [Progress.LoadingPowder] = 120f,    // 装药采购循环
        [Progress.WaitLoading] = 150f,      // 等 CanFire(装填完成确认)
        [Progress.Aiming] = 300f,           // 瞄准跟踪(协程内已有 240s 超时, 此处兜底协程死亡)
        [Progress.WaitingForFire] = 120f,   // 等击发
        [Progress.BackToIdle] = 180f,       // 退膛回位
    };

    // 正在运行的协程句柄。Dispose 时全部停掉，避免热重载后旧 ALC 的协程继续执行导致崩溃。
    private readonly List<OwnedRoutine> _runningCoroutines = new();
    private readonly Dictionary<ArtilleryTask, OwnedRoutine> _taskRoutineHandles = new();
    private readonly Dictionary<ArtilleryTask, List<OwnedRoutine>> _taskChildHandles = new();
    private bool _disposing;
    private readonly HashSet<LeftRight> _unloadingSides = new();
    private readonly HashSet<LeftRight> _unloadActive = new();
    private bool _consoleRecoveryRunning;
    private OwnedRoutine? _consoleRecoveryHandle;
    private OwnedRoutine? _refillHandle;
    public FSC() {
        this._sceneInteractor = new FcsSceneInteractor(this);
    }

    public bool IsBound { get; private set; } = false;

    /// <summary>查找并绑定游戏对象。返回 false 表示当前场景还没有目标控件。</summary>
    public bool TryBind()
    {
        // 每次重载创建全新的 Harmony 实例，避免与上一版补丁冲突。
        _sceneInteractor = new FcsSceneInteractor(this);
        _sceneInteractor.Initialize();
        _harmony = new HarmonyInstance(HarmonyId);
        _deskLock.Reset();
        _turretLock.Reset();
        IsBound = MapTable.TryBind()
                  && BallisticCalculator.TryBind()
                  && LeftGun.TryBind("Left")
                  && RightGun.TryBind("Right")
                  && _purchaseDeck.TryBind()
                  && Turret.TryBind()
                  && TriggerConsole.TryBind();
        MelonLogger.Msg("[FCS] Initialize: " + (IsBound ? "success" : "failed"));
        if (IsBound) {
            CacheAimGeometry();   // 连续瞄准几何缓存(一次, 避免每帧 GameObject.Find)
            TriggerConsole.TryAcknowledgeRecovery();
            MelonLogger.Msg($"[FCS] 实物绑定: recovery={TriggerConsole.RecoveryPending} "
                + $"flightUnknown={UnobservedFlightPossible}; Left[{LeftGun.ExecutionState()}]; Right[{RightGun.ExecutionState()}]");
            // 控制台武装状态独立于本次逻辑实例；热重载后先按物理姿态收回遗留武装。
            // 只绑定读数，不自动改动玩家遗留武装；恢复须经过明确确认。
            // 驻留装药补给协程:保证装药充足,减少任务内等待购买。
            // 弹药由已授权任务按需采购；暂停/手动模式不驻留自动购买。
        }
        // 探针已完成使命，需要时取消注释：
        // if (IsBound) RunEnemyProbe();

        return IsBound;
    }

    /// <summary>
    /// 深探 FireMission 的 Il2Cpp Dictionary 结构：
    ///   .Entities → Dictionary&lt;string, MapEntity&gt; (目标数据库)
    ///   .RunningTimers → Dictionary&lt;string, TimerValue&gt; (CBT 计时器)
    /// Il2Cpp 的泛型字典与 .NET 标准 Dictionary API 不同，需要探查正确的读取方式。
    /// </summary>
    private void RunEnemyProbe()
    {
        try
        {
            MelonLogger.Msg("[FCS] === MapEntity deep probe ===");
            var fm = GameObject.Find("Fire Mission Root")?.GetComponent<FireMission>();
            if (fm == null) { MelonLogger.Msg("[FCS]   no FireMission"); return; }

            var fmType = fm.GetType();
            var entitiesProp = fmType.GetProperty("Entities", BindingFlags.Public | BindingFlags.Instance);
            if (entitiesProp == null) { MelonLogger.Msg("[FCS]   no Entities prop"); return; }
            var entities = entitiesProp.GetValue(fm);
            if (entities == null) { MelonLogger.Msg("[FCS]   Entities == null"); return; }

            // 获取 Count
            var countProp = entities.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            var total = countProp != null ? (int)countProp.GetValue(entities) : -1;
            MelonLogger.Msg($"[FCS] Entities.Count = {total}");

            // 通过 Il2Cpp 泛型 GetEnumerator() 枚举前 5 条
            var getEnum = entities.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
            if (getEnum == null) { MelonLogger.Msg("[FCS]   no GetEnumerator"); return; }
            var enumerator = getEnum.Invoke(entities, null);
            if (enumerator == null) { MelonLogger.Msg("[FCS]   GetEnumerator returned null"); return; }

            var enumType = enumerator.GetType();
            var moveNext = enumType.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
            var currentProp = enumType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
            if (moveNext == null || currentProp == null) { MelonLogger.Msg("[FCS]   no MoveNext/Current on enumerator"); return; }

            int shown = 0;
            while (shown < 5 && (bool)moveNext.Invoke(enumerator, null)!)
            {
                var kvp = currentProp.GetValue(enumerator);
                if (kvp == null) continue;

                var kvpType = kvp.GetType();
                var keyProp = kvpType.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                var valueProp = kvpType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);

                var key = keyProp?.GetValue(kvp)?.ToString() ?? "?";
                var mapEntity = valueProp?.GetValue(kvp);

                MelonLogger.Msg($"[FCS] --- Entities['{key}'] ---");
                if (mapEntity == null) { MelonLogger.Msg("[FCS]   MapEntity = null"); shown++; continue; }

                var meType = mapEntity.GetType();
                MelonLogger.Msg($"[FCS]   MapEntity type: {meType.FullName}");
                MelonLogger.Msg($"[FCS]   MapEntity.ToString(): {mapEntity}");

                // Dump MapEntity 所有属性
                foreach (var p in meType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.Name is "ObjectClass" or "Pointer" or "WasCollected")
                        continue;
                    try
                    {
                        var v = p.GetValue(mapEntity);
                        if (v == null)
                            MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) = null");
                        else
                            MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) = {v}");
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) → {ex.GetType().Name}: {ex.Message}");
                    }
                }
                shown++;
            }
            MelonLogger.Msg($"[FCS] === Enumerated {shown} MapEntity entries ===");

            // Dump coordinateRoot: 这是游戏网格坐标 → 地图桌面的映射桥梁
            DumpCoordinateRoot(fm);

            DumpTimerValue(fm);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS] MapEntity probe failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void DumpCoordinateRoot(FireMission fm)
    {
        try
        {
            var crProp = fm.GetType().GetProperty("coordinateRoot", BindingFlags.Public | BindingFlags.Instance);
            if (crProp == null) { MelonLogger.Msg("[FCS] --- coordinateRoot: no property ---"); return; }
            var cr = crProp.GetValue(fm) as RectTransform;
            if (cr == null) { MelonLogger.Msg("[FCS] --- coordinateRoot: null ---"); return; }

            MelonLogger.Msg("[FCS] --- coordinateRoot RectTransform ---");
            MelonLogger.Msg($"[FCS]   rect = {cr.rect}");
            MelonLogger.Msg($"[FCS]   anchorMin = {cr.anchorMin}, anchorMax = {cr.anchorMax}");
            MelonLogger.Msg($"[FCS]   pivot = {cr.pivot}");
            MelonLogger.Msg($"[FCS]   sizeDelta = {cr.sizeDelta}");
            MelonLogger.Msg($"[FCS]   localPosition = {cr.localPosition}");
            MelonLogger.Msg($"[FCS]   localScale = {cr.localScale}");
            MelonLogger.Msg($"[FCS]   anchoredPosition = {cr.anchoredPosition}");
            MelonLogger.Msg($"[FCS]   lossyScale = {cr.lossyScale}");
            // 也 dump 父 Transform（可能是 Draggable Surface 或 Map 相关）
            var parent = cr.parent;
            if (parent != null)
                MelonLogger.Msg($"[FCS]   parent = '{parent.name}', parent.localScale = {parent.localScale}, parent.localPosition = {parent.localPosition}");

            // 尝试把 MapEntity.Position 通过 coordinateRoot 转换
            var entitiesProp = fm.GetType().GetProperty("Entities", BindingFlags.Public | BindingFlags.Instance);
            if (entitiesProp != null)
            {
                var entities = entitiesProp.GetValue(fm);
                if (entities != null)
                {
                    var getEnum = entities.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
                    if (getEnum != null)
                    {
                        var enumerator = getEnum.Invoke(entities, null);
                        if (enumerator != null)
                        {
                            var moveNext = enumerator.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
                            var currentProp = enumerator.GetType().GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
                            if (moveNext != null && currentProp != null && (bool)moveNext.Invoke(enumerator, null)!)
                            {
                                var kvp = currentProp.GetValue(enumerator);
                                var valueProp = kvp?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                                var me = valueProp?.GetValue(kvp);
                                var posProp = me?.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
                                if (posProp != null)
                                {
                                    var mapPos = (Vector3)posProp.GetValue(me)!;
                                    // 试 RectTransformUtility 转换
                                    var worldFromRect = cr.TransformPoint(mapPos);
                                    MelonLogger.Msg($"[FCS]   Test: MapEntity.Pos={mapPos} → coordRoot.TransformPoint={worldFromRect}");
                                    // 试 Draggable Surface TransformPoint
                                    var ds = GameObject.Find("Draggable Surface")?.transform;
                                    if (ds != null)
                                        MelonLogger.Msg($"[FCS]   Test: MapEntity.Pos={mapPos} → DragSurface.TransformPoint={ds.TransformPoint(mapPos)}");
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) { MelonLogger.Warning($"[FCS] coordinateRoot dump: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void DumpTimerValue(FireMission fm)
    {
        try
        {
            var timersProp = fm.GetType().GetProperty("RunningTimers", BindingFlags.Public | BindingFlags.Instance);
            if (timersProp == null) return;
            var timers = timersProp.GetValue(fm);
            if (timers == null) { MelonLogger.Msg("[FCS] --- TimerValue: no timers (empty dictionary) ---"); return; }

            // 尝试从 Values 获取元素
            var valuesProp = timers.GetType().GetProperty("Values", BindingFlags.Public | BindingFlags.Instance);
            if (valuesProp == null) return;
            var values = valuesProp.GetValue(timers);
            if (values == null) return;

            var getEnum = values.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
            if (getEnum == null) return;
            var enumerator = getEnum.Invoke(values, null);
            if (enumerator == null) return;

            var moveNext = enumerator.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
            var currentProp = enumerator.GetType().GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
            if (moveNext == null || currentProp == null) return;

            if ((bool)moveNext.Invoke(enumerator, null)!)
            {
                var tv = currentProp.GetValue(enumerator);
                if (tv != null)
                {
                    MelonLogger.Msg($"[FCS] --- TimerValue: {tv.GetType().FullName} ---");
                    foreach (var p in tv.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (p.Name is "ObjectClass" or "Pointer" or "WasCollected") continue;
                        try { MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) = {p.GetValue(tv)}"); }
                        catch (Exception ex) { MelonLogger.Msg($"[FCS]   .{p.Name} → {ex.GetType().Name}: {ex.Message}"); }
                    }
                }
            }
            else MelonLogger.Msg("[FCS] --- TimerValue: enumerator empty ---");
        }
        catch (Exception ex) { MelonLogger.Warning($"[FCS] TimerValue dump: {ex.GetType().Name}: {ex.Message}"); }
    }


    public void Update() {
        _sceneInteractor.Update();
        // 膛内弹来源检测(5s 节流): 膛内有弹但来源 Unknown(开局/退壳后) → 用户手动装的,
        // 标 Manual 供派发保护(自动任务不碰用户屯的弹)。活动中的自动任务跳过(见 CheckChamberOrigin)。
        if (Time.time - _lastOriginCheckTime > 5f)
        {
            _lastOriginCheckTime = Time.time;
            CheckChamberOrigin(LeftGun, LeftTask);
            CheckChamberOrigin(RightGun, RightTask);
        }
        // 任务看门狗(5s 节流): 协程死亡/卡死后槽位残留会永久卡死调度, 这里按进度停留时长强制清理。
        if (Time.time - _lastWatchdogTime > 5f)
        {
            _lastWatchdogTime = Time.time;
            WatchdogSlot(LeftRight.Left, LeftTask, ref _watchdogTaskLeft, ref _watchdogProgressLeft, ref _watchdogSinceLeft);
            WatchdogSlot(LeftRight.Right, RightTask, ref _watchdogTaskRight, ref _watchdogProgressRight, ref _watchdogSinceRight);
        }
        // 面板倒计时归零(=射表估计落地)的任务移出在飞列表
        if (InFlight.Count > 0)
            InFlight.RemoveAll(t => {
                if (Time.time - t.FiredAt < t.EstimatedToF) return false;
                MarkFireRecordFlightWindowExpired(t, "预计飞行窗口结束，但命中结果未确认");
                PlanRegistry.MarkFlightWindowExpired(t, "预计飞行窗口结束，但命中结果未确认");
                return true;
            });
        // Piece 自动归位: 忘了放/紧急移动后 5s 内自愈(手动/自动都生效)
        if (Time.time - _lastPieceSyncTime > 5f) {
            _lastPieceSyncTime = Time.time;
            SyncTurretPiece();
        }
    }

    private float _lastOriginCheckTime;
    private float _lastFdcWaitLog;   // FDC 等旧弹落地诊断日志节流(2026-08-16)

    /// <summary>膛内弹来源检测: 膛空 → 复位 Unknown; 膛内有弹且 Unknown → 用户手动装的(标 Manual)。
    /// Auto(自动任务装的)保持——任务中断遗留的弹仍可被下个自动任务复用。
    /// 2026-08-16: 自动任务活动期(装填/等待/瞄准/回位)跳过本管——"推弹完成→确认弹种"之间
    /// 膛内弹已出现但 Origin 可能还没标 Auto, 5s 轮询撞上会误判成用户手动 → 本管被永久跳过派发。</summary>
    private static void CheckChamberOrigin(GunSystem gun, ArtilleryTask? task)
    {
        if (gun.IsChamberEmpty())
        {
            if (gun.Origin != GunSystem.ChamberOrigin.Unknown) gun.MarkChamberEmpty();
            return;
        }
        bool autoTaskActive = task != null && task.Source == TaskSource.Auto
            && task.progress is not (Progress.Pending or Progress.Finished or Progress.Canceled or Progress.Failed);
        if (autoTaskActive) return;   // 膛内弹归正在执行的任务管, 轮询不抢判
        if (gun.Origin == GunSystem.ChamberOrigin.Unknown)
        {
            MelonLogger.Msg($"[FCS] 检测到膛内弹 {gun.BulletInChamber()} (非自动装填), 标记用户手动, 自动任务将跳过本管");
            gun.MarkManual();
        }
    }

    /// <summary>看门狗: 任务在某个进度停留超过阈值 → 协程可能已死亡, 强制取消并释放槽位恢复调度。
    /// 幂等: 协程若还活着, Canceled 会在下一个检查点退出(见 RunTaskRoutine); 已死的直接清引用。</summary>
    private void WatchdogSlot(LeftRight side, ArtilleryTask? task, ref ArtilleryTask? lastTask, ref Progress lastProgress, ref float since)
    {
        if (!ReferenceEquals(task, lastTask))
        {
            // 任务对象换了(上一任务结束, 新任务进槽) → 停留计时必须归零,
            // 否则新任务继承旧任务的计时, 出生几秒就被旧账误杀(2026-08-18 实测事故)。
            lastTask = task;
            lastProgress = Progress.Pending;
            since = 0f;
            return;
        }
        if (task == null || task.Paused || task.progress == Progress.Finished || task.progress == Progress.Canceled
            || task.progress == Progress.Failed || task.progress == Progress.Pending)
        {
            lastProgress = Progress.Pending;
            since = 0f;
            return;
        }
        // 进度变化 → 重置计时
        if (task.progress != lastProgress)
        {
            lastProgress = task.progress;
            since = Time.time;
            return;
        }
        if (!_watchdogLimit.TryGetValue(task.progress, out var limit)) return;
        if (Time.time - since <= limit) return;
        MelonLogger.Error($"[FCS] 看门狗: 任务 T{task.targetId}({task.entityId}) 卡在 {task.progress} 超过 {limit}s, " +
                          $"bullet={task.bulletType} charged={task.LoadedCharge} fired={task.Fired} canceled={task.Canceled} " +
                          $"btn.max={_sceneInteractor.maxCharge} task.max={task.useMaxCharge}, 强制取消释放槽位");
        task.RevokeFireAuthorization($"watchdog timeout in {task.progress}");
        if (!task.Fired && PlanRegistry.Get(task.PlanId)?.Result == FirePlanResult.Pending)
            MarkFireResultUnknown(side, task, "击发提交后执行超时，出膛未确认");
        PlanRegistry.Revoke(task, task.CancelReason);
        if (_taskRoutineHandles.TryGetValue(task, out var handle))
        {
            try { handle.Stop(); }
            catch (Exception e) { MelonLogger.Error($"[FCS] 看门狗停止任务失败，保持炮位隔离: {e}"); }
        }
        if (task.ShouldReleaseTargetOnExit) Registry.Release(task);
        ReleaseSlot(side, task);
        // 炮塔锁: 协程可能卡死在无检查点的等待上(后台 ReserveTurretAndRotate 还在转),
        // 直接标记 Released 让后台自归; 已持锁的立即释放。
        // 旧动作未退出时不抢走炮塔锁；所属 finally 按 lease 归还。
        // deskLock: 任务卡死在装药采购等锁点, 可能是驻留协程(SyncCalculatorVisual/ReplenishPowderLoop)
        // 持锁死亡泄漏 → 强制复位, 防止所有后续任务永久等锁。
        // 不重置仍可能有旧动作持有的共享锁；撤销后由所属协程退出时释放。
        lastProgress = Progress.Pending;
        since = 0f;
        try { OnGunIdle?.Invoke(); } catch { }
    }

    /// <summary>自动归位 Player Turret Piece 到真实炮塔位置(忘了放/紧急移动后找不到自己时自愈)。
    /// Piece 与 GetTurretLocal 都是地图局部坐标, 直接写 localPosition。TurretLocation 找不到时 GetTurretLocal 回退自身 → 无操作。</summary>
    public void SyncTurretPiece() {
        var piece = MapTable.Turret;
        if (piece == null) return;
        var real = MapTable.GetTurretLocal();
        if ((piece.localPosition - real).magnitude > TurretPieceSnapTolerance)
            piece.localPosition = real;
    }

    /// <summary>最近的在飞炮弹预计落地剩余秒数。无在飞弹返回 -1。
    /// 紧急移动/FDC 暂停收益保护: 在飞弹落地会立即解除暂停, 触发前需避开。
    /// 判定与 InFlight 移除同源(射表 remain<0 即视为落地)——宁等不早, 防射表误差导致
    /// 误触发(弹实际还在飞, 暂停被立即解除)。注意: 返回的是【最早】落地——只够"有无在飞弹"
    /// 判定, FDC 落地顺序保护要用 <see cref="LatestImpactIn"/>。</summary>
    public float SoonestImpactIn()
    {
        float best = -1f;
        foreach (var t in InFlight)
        {
            float remain = t.EstimatedToF - (Time.time - t.FiredAt);
            if (remain < 0f) continue;   // 射表已落地(与 InFlight 移除同判定)
            if (best < 0f || remain < best) best = remain;
        }
        foreach (var t in new[] { LeftTask, RightTask })
        {
            if (t == null || !t.FireResultUnknown || t.FireResultUnknownAt <= 0f) continue;
            // 未知结果可能已经出膛；按提交时刻和射表保守占用落地窗口。
            float remain = t.EstimatedToF - (Time.time - t.FireResultUnknownAt);
            if (remain >= 0f && (best < 0f || remain < best)) best = remain;
        }
        return best;
    }

    /// <summary>最晚的在飞炮弹预计落地剩余秒数。无在飞弹返回 -1。
    /// FDC 落地顺序保护用: 需保证【所有】旧弹都比 FDC 弹先落地(max < fdcToF),
    /// 否则任一旧弹在 FDC 弹之后落地都会立即解除 FDC 暂停。SoonestImpactIn 是 min, 不适用。</summary>
    public float LatestImpactIn()
    {
        float best = -1f;
        foreach (var t in InFlight)
        {
            float remain = t.EstimatedToF - (Time.time - t.FiredAt);
            if (remain < 0f) continue;
            if (remain > best) best = remain;
        }
        foreach (var t in new[] { LeftTask, RightTask })
        {
            if (t == null || !t.FireResultUnknown || t.FireResultUnknownAt <= 0f) continue;
            float remain = t.EstimatedToF - (Time.time - t.FireResultUnknownAt);
            if (remain > best) best = remain;
        }
        return best;
    }

    /// <summary>键盘快捷键触发射击目标（小键盘 1-4）</summary>
    public void FireTarget(int targetId) {
        _sceneInteractor.FireTarget(targetId);
    }

    /// <summary>释放：撤销补丁、清空 IL2CPP 引用。</summary>
    public void Dispose()
    {
        _disposing = true;
        _automaticExecutionPaused = true;
        foreach (var task in _activeTaskRoutines.ToList()) task.RevokeFireAuthorization("逻辑卸载");
        // 停掉所有未完成的协程，否则热重载后旧 ALC 的协程仍会被 Unity 驱动 → 崩溃。
        foreach (var handle in _runningCoroutines.ToArray()) {
            try { handle.Stop(); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] Stop coroutines failed: {ex}"); }
        }
        TriggerConsole.StopArmsNow();
        _runningCoroutines.Clear();
        _armCleanupInProgress.Clear();
        _unknownFireHoldInProgress.Clear();

        // 清空调度状态，避免热重载后残留任务/槽位影响新一轮绑定。
        _taskQueue.Clear();
        LeftTask = null;
        RightTask = null;
        _activeTaskRoutines.Clear();
        _taskRoutineHandles.Clear();
        _taskChildHandles.Clear();
        InFlight.Clear();
        FireRecords.Clear();
        Registry.Clear();
        PlanRegistry.Clear();
        _slotLeases.Clear();

        _sceneInteractor.ShutDown();
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { MelonLogger.Error($"[FCS] UnpatchSelf failed: {ex}"); }
        _harmony = null;
    }

    /// <summary>
    /// 驻留协程:每 5s 检查两管炮装药,低于阈值补一包。用 _deskLock 保护(采购台是共享硬件,
    /// 与任务内采购互斥)。TryBind 成功后登记进 _runningCoroutines,Dispose 时随协程一起 Stop。
    /// CBT 基金纪律(§3.6): 跌破基金线时停止装药囤积(只用 DRIL 维持循环, 任务内按需仍会买)。
    /// </summary>
    private IEnumerator ReplenishPowderLoop() {
        while (true) {
            yield return new WaitForSeconds(PowderCheckInterval);
            if (_automaticExecutionPaused || _disposing) continue;
            // 取两管炮装药的最小值, 任一管低于阈值就补
            var charges = Math.Min(LeftGun.RemainingCharges(), RightGun.RemainingCharges());
            if (charges >= PowderReplenishThreshold) continue;
            MelonLogger.Msg(
                $"[FCS] AutoReplenish: powder charges {charges} < {PowderReplenishThreshold}, buying one");
            var generation = _execution.Generation;
            var handle = StartOwnedRoutine(CancellableRoutine.Run(ReplenishPowderOnce(),
                () => !_disposing && !_automaticExecutionPaused && generation == _execution.Generation));
            _refillHandle = handle;
            while (!handle.IsCompleted) yield return null;
            if (ReferenceEquals(_refillHandle, handle)) _refillHandle = null;
        }
    }

    private IEnumerator ReplenishPowderOnce()
    {
        var lease = _deskLock.CreateLease();
        yield return _deskLock.Acquire(lease);
        try { yield return _purchaseDeck.BuyPowders(); }
        finally { _deskLock.Release(lease); }
    }

    public IEnumerator ExposeAllEntities() {
        while (true) {
            foreach (var m in MapTable.GetAllFireMissionEntities()) {
                m.GetComponent<Image>().enabled = true;
            }

            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>
    /// 把任务加入调度队列。用户不指定炮管——调度器自动派给空闲炮管。
    /// 入队后立即尝试派发；若两管炮都忙，任务留在队列里，等某管炮打完自动拉取。
    /// 必须在主线程调用（点击回调即是）。
    /// </summary>
    public void EnqueueTask(ArtilleryTask task) {
        task.progress = Progress.Pending;
        task.SlotLeaseId = 0;
        task.FireAuthorized = true;
        task.Canceled = false;
        task.Paused = false;
        task.PreserveAfterPause = false;
        task.forceFire = false;
        task.CancelReason = "";
        task.ArmConfirmed = false;
        task.FireResultUnknown = false;
        task.FireResultUnknownAt = 0f;
        task.SharedTriggerGroupId = 0;
        task.PlanId = 0;
        task.PlanRevision = 0;
        // 手动任务: 解析标记位置的存活敌目标 → entityId(注册表按目标屏蔽);
        // 解析失败保持空 → 按位置提交(小半径屏蔽)。
        if (task.Source == TaskSource.Manual && task.entityId is not { Length: > 0 } && EntityLocator != null)
            task.entityId = EntityLocator.FindNearestHostileId(task.position, TargetRegistry.ManualResolveMaxDistance) ?? "";
        _execution.Admit(task);
        task.TriggerSubmitted = false;
        PlanRegistry.Admit(task);
        if (task.Source == TaskSource.Auto && _automaticExecutionPaused)
        {
            task.RevokeFireAuthorization("自动执行已停止");
            PlanRegistry.Revoke(task, task.CancelReason);
            return;
        }
        _taskQueue.Enqueue(task);
        // 手动任务入队即登记(队列在自动清队列中存活); 自动任务也登记，避免炮位交接期间重复规划。
        if (IsKillContract(task)) Registry.Commit(task);
        TryDispatch();
    }

    /// <summary>击杀契约: 非杀伤弹(STAR 照明)不登记注册表——照明弹的意义是把目标暴露出来打,
    /// 登记反而触发 65s 在飞窗口屏蔽, 雷达看着刚被照亮的活目标干瞪眼。
    /// ponytail: 只列 STAR; 其他非杀伤弹(SMOKE 等)出现同样屏蔽再扩展。</summary>
    private static bool IsKillContract(ArtilleryTask t) => t.bulletType != BulletType.STAR;


    /// <summary>转移后重新观察炮膛，保留弹药；不把转移当作退弹授权。</summary>
    private IEnumerator ObserveRetainedChamber(GunSystem gun, string tag)
    {
        if (gun.AssessChamber() == GunSystem.ChamberState.ReadyToFire)
        {
            MelonLogger.Msg($"[FCS] {tag} 膛内整发 {gun.BulletInChamber()} 且机构空闲: 移动后保留, 不清洗(任务直接复用击发)");
            yield break;
        }
        // 移动或模式切换不构成退弹授权；不可靠的半装填留给明确恢复操作。
        MelonLogger.Warning($"[FCS] {tag} 移动后状态未确认，保留弹药并等待明确退弹/手动接管");
        yield break;
    }

    /// <summary>等待暂停/取消任务完成机构交接；未知武装或未知出膛不会被清洗流程盲目覆盖。</summary>
    private IEnumerator WaitForTaskHandover(float realtimeSeconds)
    {
        var deadline = Time.realtimeSinceStartup + realtimeSeconds;
        while ((LeftTask != null || RightTask != null)
               && Time.realtimeSinceStartup < deadline)
            yield return null;
    }

    /// <summary>等待转移基准完全静止: 真实炮塔在地图面下的局部位置连续两次采样(1s 间隔)一致。
    /// 仅紧急移动协程内调用(有界 ≤durSeconds), 不做每帧轮询; 引用不可用(无法判定)立即放行。
    /// 判定用局部坐标——Bearing/DistKm/雷达角度都以它为基准, 它静止了任务才不会被冻结错基准。</summary>
    private IEnumerator WaitForMoveSettle(float durSeconds)
    {
        var deadline = Time.time + durSeconds;
        Vector3? prev = TurretLocalOnSurface();
        while (Time.time < deadline)
        {
            yield return new WaitForSeconds(1f);
            var cur = TurretLocalOnSurface();
            if (prev == null || cur == null) yield break;              // 引用不可用 → 无法判定, 放行
            if ((cur.Value - prev.Value).sqrMagnitude < 0.0001f) yield break;  // 已静止
            prev = cur;
        }
    }

    /// <summary>真实炮塔在地图面下的局部坐标(瞄准基准)。引用缺失返回 null。</summary>
    private Vector3? TurretLocalOnSurface()
    {
        var surface = AimSurface;
        var turretXf = AimTurretBase;
        if (surface == null || turretXf == null) return null;
        return surface.InverseTransformPoint(turretXf.position);
    }

    private Vector3? _lastTurretLocal;         // 手动转移检测: 上次采样基准
    private float _lastTransferCheckTime;

    /// <summary>玩家手动紧急移动检测(2026-08-16 用户): 玩家手动买 MoveZone 卡/转移阵地 →
    /// 游戏移动 TurretLocation → 基准位置突变 → 返回 true(调用方执行转移后清理, 防任务卡死——
    /// 玩家反馈 bug: 手动移动后流程卡住)。正常游戏 TurretLocation 不动(玩家拖 Piece 不影响它);
    /// 自动紧急移动期间(_emergencyMoveInProgress)由调用方跳过。5s 节流(用户: 5s 就行——
    /// 转移是持续过程, 5s 间隔采样对比移动前后位置差, 一样能触发)。</summary>
    public bool DetectManualTransferMove()
    {
        if (Time.time - _lastTransferCheckTime < 5f) return false;
        _lastTransferCheckTime = Time.time;
        var local = TurretLocalOnSurface();
        if (local == null) { _lastTurretLocal = null; return false; }
        if (_lastTurretLocal == null) { _lastTurretLocal = local; return false; }   // 首次采样(开局)
        float dist = (local.Value - _lastTurretLocal.Value).magnitude;
        _lastTurretLocal = local;
        // 阈值 0.3 world unit ≈ 80m/s: 转移是 km 级移动(动画期每秒 >1 unit); 正常静止为 0
        return dist > 0.3f;
    }

    /// <summary>手动转移后的清理(2026-08-16 用户): 玩家已手动买 MoveZone 卡, 跳过采购——
    /// 等转移动画稳定 → 刷新几何引用 → 等基准静止 → 清洗双管(手动弹不退壳) → 回调恢复派发。
    /// 与自动紧急移动的转移后段一致, 防炮管状态被重置后任务卡死。</summary>
    public OwnedRoutine StartTransferCleanup(Action? onDone = null, Func<bool>? isAuthorized = null)
    {
        var routine = CancellableRoutine.Run(TransferCleanupRoutine(onDone), isAuthorized ?? (() => true));
        return StartOwnedRoutine(routine);
    }

    private IEnumerator TransferCleanupRoutine(Action? onDone)
    {
        yield return new WaitForSeconds(5f);   // 转移动画稳定
        RefreshAimGeometry();
        yield return WaitForMoveSettle(10f);
        yield return WaitForTaskHandover(30f);
        if (LeftTask == null)
            yield return ObserveRetainedChamber(LeftGun, "Left");   // 装填完毕的炮管保留整发(2026-08-18)
        else
            MelonLogger.Warning("[FCS] Left 仍有未完成机构交接，跳过清洗并保持隔离");
        if (RightTask == null)
            yield return ObserveRetainedChamber(RightGun, "Right");
        else
            MelonLogger.Warning("[FCS] Right 仍有未完成机构交接，跳过清洗并保持隔离");
        MelonLogger.Msg("[FCS] 手动转移清理完成");
        onDone?.Invoke();
    }

    /// <summary>移动完成后重设手动转移检测的位置基准(2026-08-18): 自动紧急移动自己造成的
    /// TurretLocation 突变会被检测器拿移动前的旧基准比 → 移动刚结束 82ms 后触发第二次
    /// 清理(双清洗互相打架, 机构 desync)。重采基准后残余位移 ≈0, 不再误触发。</summary>
    public void ResetTransferBaseline()
    {
        _lastTurretLocal = TurretLocalOnSurface();
        _lastTransferCheckTime = Time.time;
    }

    /// <summary>弹种不可用回退链: DRIL→LE→HE(开局即有)→HCHE; AP 不可用 → HE。链尾返回自身。</summary>
    private static BulletType FallbackShell(BulletType t) => t switch
    {
        BulletType.DRIL => BulletType.LE,
        BulletType.LE => BulletType.HE,
        BulletType.AP => BulletType.HE,
        BulletType.HE => BulletType.HCHE,
        _ => t
    };

    /// <summary>插队：任务放到队列最前面，用于高优先级目标</summary>
    public void EnqueueTaskFront(ArtilleryTask task) {
        task.progress = Progress.Pending;
        task.SlotLeaseId = 0;
        task.FireAuthorized = true;
        task.Canceled = false;
        task.Paused = false;
        task.PreserveAfterPause = false;
        task.forceFire = false;
        task.CancelReason = "";
        task.ArmConfirmed = false;
        task.FireResultUnknown = false;
        task.FireResultUnknownAt = 0f;
        task.SharedTriggerGroupId = 0;
        task.PlanId = 0;
        task.PlanRevision = 0;
        _execution.Admit(task);
        task.TriggerSubmitted = false;
        PlanRegistry.Admit(task);
        if (task.Source == TaskSource.Auto && _automaticExecutionPaused)
        {
            task.RevokeFireAuthorization("自动执行已停止");
            PlanRegistry.Revoke(task, task.CancelReason);
            return;
        }
        var existing = _taskQueue.ToArray();
        _taskQueue.Clear();
        _taskQueue.Enqueue(task);
        foreach (var t in existing) _taskQueue.Enqueue(t);
        TryDispatch();
    }

    /// <summary>把队首任务派给空闲炮管，直到没有空闲炮管或队列空。</summary>
    private void TryDispatch() {
        if (_disposing || _stoppingExecution || !IsBound) return;
        if (_consoleRecoveryRunning)
        {
            ReportExecutionBlock("Waiting for console recovery");
            return;
        }
        if (TriggerConsole.RecoveryPending && !TriggerConsole.TryAcknowledgeRecovery())
        {
            ReportExecutionBlock("Waiting for disarm confirmation");
            return;
        }
        var candidates = _taskQueue.Count;
        while (_taskQueue.Count > 0 && candidates-- > 0) {
            var task = _taskQueue.Peek();
            LeftRight slot;
            if (LeftTask == null && CanAcceptRetainedShell(LeftRight.Left, task)) slot = LeftRight.Left;
            else if (RightTask == null && CanAcceptRetainedShell(LeftRight.Right, task)) slot = LeftRight.Right;
            else
            {
                task.CancelReason = "等待可用炮管；保留弹不匹配时需明确退弹";
                ReportExecutionBlock("Waiting for compatible idle gun");
                _taskQueue.Enqueue(_taskQueue.Dequeue());
                continue;
            }
            if (_automaticExecutionPaused && task.Source == TaskSource.Auto)
            {
                ReportExecutionBlock("Automatic execution paused");
                break;
            }
            // 另一个炮管被玩家或已撤销任务物理武装时，自动任务留在队列，
            // 不先装填再在全局扳机处反复失败和消耗资源。
            if (task.Source == TaskSource.Auto && HasUnsafeArmForAutomaticFire())
            {
                ReportExecutionBlock("Waiting for arm handover");
                break;
            }
            ReportExecutionBlock("");
            _taskQueue.Dequeue();
            task.PreserveAfterPause = false;
            task.SlotLeaseId = _slotLeases.Acquire(slot.ToString());
            MelonLogger.Msg($"[FCS] 派发 P{task.PlanId}/R{task.PlanRevision} {slot} lease={task.SlotLeaseId}");
            PlanRegistry.MarkDispatched(task);
            if (slot == LeftRight.Left) LeftTask = task;
            else RightTask = task;
            if (IsKillContract(task)) Registry.Commit(task);   // 手动任务幂等(入队已登记); STAR 不登记
            StartTaskRoutine(slot, task);
        }
    }

    /// <summary>启动一个火控任务协程。用 MelonCoroutines 跑协程实现延时——
    /// 协程由 Unity 在主线程分帧驱动，yield 期间不阻塞、恢复后仍在主线程，
    /// 因此可安全访问 IL2CPP 对象。绝不能用 async/Task.Delay：其 continuation
    /// 会在线程池线程恢复，跨线程访问 IL2CPP 运行时会导致进程崩溃且无日志。
    /// 经守护层启动: 手动驱动子协程 MoveNext, 异常必定捕获(日志+槽位清理),
    /// 防协程静默死亡后 LeftTask/RightTask 残留 → 调度停摆。</summary>
    private void StartTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        _activeTaskRoutines.Add(task);
        var handle = StartOwnedRoutine(TaskRoutineGuard(leftRight, task));
        if (!handle.IsCompleted) _taskRoutineHandles[task] = handle;
    }

    private OwnedRoutine StartOwnedRoutine(IEnumerator routine)
    {
        var handle = new OwnedRoutine(routine, MelonCoroutines.Stop,
            completed => _runningCoroutines.Remove(completed));
        _runningCoroutines.Add(handle);
        handle.Start(MelonCoroutines.Start);
        return handle;
    }

    private bool CanAcceptRetainedShell(LeftRight side, ArtilleryTask task)
    {
        if (_unloadingSides.Contains(side) || (task.PreferredSide.HasValue && task.PreferredSide != side)) return false;
        var gun = side == LeftRight.Left ? LeftGun : RightGun;
        if (!gun.IsTaskReady() || gun.IsPendingReload()) return false;
        var state = gun.AssessChamber();
        if (state == GunSystem.ChamberState.Empty) return !task.UsesRetainedRound;
        if (state == GunSystem.ChamberState.Dirty || !gun.ChamberMatches(task.bulletType)) return false;
        if (state != GunSystem.ChamberState.ReadyToFire) return !task.UsesRetainedRound;
        if (task.UsesRetainedRound)
            return gun.ChamberCharges() == task.LoadedCharge
                && RetainedAmmoPolicy.CanReach(task.distance, task.LoadedCharge);
        var distance = task.distance;
        if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel))
        {
            var tof = ToFTable.FlightTime(distance, TargetLeadSolver.ChargeFor(distance));
            var lead = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel, Time.time - task.AimStartTime + 60f, tof);
            distance = Mathf.Max(distance, DistKm(lead));
        }
        var required = task.useMaxCharge || _sceneInteractor.maxCharge ? 6
            : Mathf.Min(6, TargetLeadSolver.ChargeFor(distance + (task.IsMoving ? MovingDistanceMarginKm : 0f)));
        return gun.ChamberCharges() is >= 1 and <= 6 && gun.ChamberCharges() >= required;
    }

    private bool RetainedAimSupported(ArtilleryTask task)
    {
        if (!task.UsesRetainedRound) return true;
        var aim = task.position;
        if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel))
            aim = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel, Time.time - task.AimStartTime,
                ToFTable.FlightTime(task.distance, task.LoadedCharge));
        return RetainedAmmoPolicy.CanReach(DistKm(aim), task.LoadedCharge)
            && EntityLocator != null
            && !EntityLocator.AllyPositions.Any(ally =>
                (ally - aim).magnitude * ShellData.KmPerWorldUnit <= ShellData.BlastRadiusKm(task.bulletType));
    }

    private bool ShouldAutomaticallyFire(ArtilleryTask task) =>
        _execution.IsAuthorized(task) && (task.Source == TaskSource.Auto || _sceneInteractor.AutoFire);

    public bool RequestUnload(LeftRight side)
    {
        if (!IsBound || _disposing || _unloadActive.Contains(side)) return false;
        var task = side == LeftRight.Left ? LeftTask : RightTask;
        var gun = side == LeftRight.Left ? LeftGun : RightGun;
        if ((task != null && (_activeTaskRoutines.Contains(task) || task.FireResultUnknown || task.Fired))
            || !gun.ReloadIdle() || gun.IsPendingReload()
            || !TriggerConsole.TryReadArmState(side, out var armed, out _) || armed) return false;
        _unloadingSides.Add(side);
        _unloadActive.Add(side);
        if (task != null)
        {
            RevokeTask(task, "玩家请求退弹复位", false);
            ReleaseSlot(side, task);
        }
        StartOwnedRoutine(CancellableRoutine.Run(UnloadRoutine(side, gun), () => !_disposing));
        return true;
    }

    private IEnumerator UnloadRoutine(LeftRight side, GunSystem gun)
    {
        try
        {
            yield return gun.CleanState();
            if (!gun.IsChamberEmpty() || !gun.ReloadIdle())
            {
                MelonLogger.Error($"[FCS] {side} 退弹未确认完成，保留隔离；请手动复位后再次请求退弹");
                yield break;
            }
            _unloadingSides.Remove(side);
        }
        finally
        {
            _unloadActive.Remove(side);
            if (!_disposing) TryDispatch();
        }
    }

    private bool OwnsSlot(LeftRight side, ArtilleryTask task) =>
        ReferenceEquals(side == LeftRight.Left ? LeftTask : RightTask, task)
        && _slotLeases.IsOwner(side.ToString(), task.SlotLeaseId);

    private void StartTaskChild(LeftRight side, ArtilleryTask task, IEnumerator child)
    {
        var routine = CancellableRoutine.Run(child, () => !_disposing && OwnsSlot(side, task) && _execution.IsAuthorized(task));
        var handle = StartOwnedRoutine(routine);
        if (handle.IsCompleted) return;
        if (!_taskChildHandles.TryGetValue(task, out var handles))
            _taskChildHandles[task] = handles = new List<OwnedRoutine>();
        handles.Add(handle);
    }

    private void StopTaskChildren(ArtilleryTask task)
    {
        if (!_taskChildHandles.Remove(task, out var handles)) return;
        foreach (var handle in handles)
            try { handle.Stop(); }
            catch (Exception e) { MelonLogger.Error($"[FCS] 停止计划子动作失败 P{task.PlanId}: {e}"); }
    }

    /// <summary>任务协程守护层: 手动驱动 RunTaskRoutine 的 MoveNext, 捕获异常打日志并清理槽位。
    /// C# 迭代器限制(含 catch 的 try 内不能 yield) → 不能 yield return 子协程, 只能手动驱动。
    /// OwnedRoutine 停止调度后显式 Dispose 本层，确保内层 finally 执行。</summary>
    private IEnumerator TaskRoutineGuard(LeftRight leftRight, ArtilleryTask task) {
        Exception? ex = null;
        var observingStoppedShot = false;
        var inner = CancellableRoutine.Run(RunTaskRoutine(leftRight, task),
            () => !_disposing && OwnsSlot(leftRight, task)
                && (_execution.IsAuthorized(task) || ExecutionControl.NeedsShotObservation(task)));
        try {
            while (true) {
                bool moved;
                try
                {
                    if (task.Canceled && task.TriggerSubmitted && !task.Fired
                        && !task.FireResultUnknown && !observingStoppedShot)
                    {
                        observingStoppedShot = true;
                        try { (inner as IDisposable)?.Dispose(); }
                        finally { TriggerConsole.TryDisarmNow(leftRight, task.SlotLeaseId); }
                        inner = CancellableRoutine.Run(ObserveStoppedShot(leftRight, task),
                            () => !_disposing && OwnsSlot(leftRight, task));
                    }
                    moved = inner.MoveNext();
                }
                catch (Exception e) { ex = e; break; }
                if (!moved) break;
                yield return inner.Current;
            }
        }
        finally {
            try { (inner as IDisposable)?.Dispose(); }
            catch (Exception cleanupError) { ex = ex == null ? cleanupError : new AggregateException(ex, cleanupError); }
            finally
            {
                StopTaskChildren(task);
                if (TriggerConsole.GetArmOwner(leftRight) == task.SlotLeaseId && task.SlotLeaseId != 0)
                    task.ArmConfirmed = true;
                if (_taskTurretRes.TryGetValue(task, out var reservation)) ReleaseTurretOnce(reservation);
                _activeTaskRoutines.Remove(task);
                _taskRoutineHandles.Remove(task);
            }
            if (ex != null) {
                MelonLogger.Error($"[FCS] 任务协程异常 T{task.targetId}({task.entityId}): {ex}");
                task.RevokeFireAuthorization("task coroutine exception");
                if (!task.Fired && PlanRegistry.Get(task.PlanId)?.Result == FirePlanResult.Pending)
                    MarkFireResultUnknown(leftRight, task, "击发提交后协程异常，出膛未确认");
                if (task.Paused) PlanRegistry.Pause(task, task.CancelReason);
                else PlanRegistry.Fail(task, task.CancelReason);
                MarkFireRecordCanceled(task);
                if (task.ShouldReleaseTargetOnExit) Registry.Release(task);
                ReleaseSlot(leftRight, task);
                _taskTurretRes.Remove(task);
                try { OnGunIdle?.Invoke(); } catch { }
            }
            else {
                _taskTurretRes.Remove(task);
                PlanRegistry.Finish(task);
                if (task.ShouldReleaseTargetOnExit) Registry.Release(task);
            }
            if (!_disposing && OwnsSlot(leftRight, task)) ReleaseSlot(leftRight, task);
            if (!_disposing) TryDispatch();
        }
    }

    /// <summary>任一炮管退膛时触发回调（WaitBackToIdle 完成后）。供雷达层刷新队列。</summary>
    public event Action? OnGunIdle;

    /// <summary>清空队列中雷达自动入队的任务, 保留玩家手动入队的任务。</summary>
    public void ClearPendingTasks() {
        var kept = new List<ArtilleryTask>();
        foreach (var task in _taskQueue)
        {
            if (task.Source == TaskSource.Manual)
                kept.Add(task);
            else
            {
                task.RevokeFireAuthorization("automatic queue cleared");
                PlanRegistry.Revoke(task, task.CancelReason);
                task.progress = Progress.Canceled;
                Registry.Release(task);
            }
        }
        _taskQueue.Clear();
        foreach (var t in kept) _taskQueue.Enqueue(t);
    }

    /// <summary>
    /// 撤销一个当前或排队任务的后续执行授权。调用方不直接写 Canceled，
    /// 这样已确认武装的任务会走统一的物理解武装交接路径。
    /// </summary>
    public void RevokeTask(ArtilleryTask task, string reason, bool excludeFromAutomatic = true) {
        MelonLogger.Msg($"[FCS] 撤销 P{task.PlanId}/R{task.PlanRevision} lease={task.SlotLeaseId} exclude={excludeFromAutomatic}: {reason}");
        if (excludeFromAutomatic) AutomaticExclusions.Exclude(task);
        task.Paused = false;
        task.PreserveAfterPause = false;
        task.RevokeFireAuthorization(reason);
        PlanRegistry.Revoke(task, reason);
        StopTaskChildren(task);
        if (_taskQueue.Contains(task)) {
            var remaining = _taskQueue.Where(candidate => !ReferenceEquals(candidate, task)).ToList();
            _taskQueue.Clear();
            foreach (var candidate in remaining) _taskQueue.Enqueue(candidate);
            task.progress = Progress.Canceled;
            Registry.Release(task);
            return;
        }
        if (!_activeTaskRoutines.Contains(task)
            && !ReferenceEquals(LeftTask, task) && !ReferenceEquals(RightTask, task))
        {
            task.progress = Progress.Canceled;
            if (task.ShouldReleaseTargetOnExit) Registry.Release(task);
            return;
        }
        // 运行中的未武装任务要先让自己的协程到达检查点并释放子动作；
        // 已武装任务则立即进入物理解武装交接。只有终态任务需要这里兜底清槽位。
        var terminal = task.progress is Progress.Finished or Progress.Canceled or Progress.Failed;
        if (!task.ArmConfirmed && !terminal) return;
        if (ReferenceEquals(LeftTask, task)) ReleaseSlot(LeftRight.Left, task);
        else if (ReferenceEquals(RightTask, task)) ReleaseSlot(LeftRight.Right, task);
    }

    /// <summary>兼容旧入口：停止现有执行，不保留可恢复的任务队列。</summary>
    public void PauseAutomaticExecution(string reason) => StopAllExecution(reason);

    public void StopAllExecution(string reason, ArtilleryTask? executingTask = null)
    {
        if (_stoppingExecution) return;
        _stoppingExecution = true;
        _automaticExecutionPaused = true;
        try
        {
            var tasks = _taskQueue.Concat(new[] { LeftTask, RightTask }.OfType<ArtilleryTask>())
                .Distinct().ToArray();
            // 玩家刚刚拉过扳机的实物证据也必须保留，不能在停止时丢掉。
            foreach (var side in new[] { LeftRight.Left, LeftRight.Right })
            {
                var task = side == LeftRight.Left ? LeftTask : RightTask;
                var gun = side == LeftRight.Left ? LeftGun : RightGun;
                try
                {
                    if (task != null && task.progress == Progress.WaitingForFire && gun.IsPendingReload()
                        && !task.TriggerSubmitted && !task.Fired)
                        ObserveTriggerSubmission(side, task);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[FCS] 停止时无法读取 {side} 出膛状态: {ex.Message}");
                    if (task != null && task.ArmConfirmed && !task.Fired)
                        MarkFireResultUnknown(side, task, "停止时无法读取已武装炮管的出膛结果");
                }
            }
            _execution.Stop(tasks, PlanRegistry, reason);
            try { _consoleRecoveryHandle?.Stop(); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] 停止控制台恢复失败: {ex}"); }
            _consoleRecoveryHandle = null;
            try { _refillHandle?.Stop(); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] 停止补药失败: {ex}"); }
            _refillHandle = null;
            _taskQueue.Clear();
            foreach (var task in tasks)
            {
                StopTaskChildren(task);
                if (task.ShouldReleaseTargetOnExit) Registry.Release(task);
                if ((task.TriggerSubmitted && !task.FireResultUnknown) || task.Fired
                    || ReferenceEquals(task, executingTask)) continue;
                if (_taskRoutineHandles.TryGetValue(task, out var handle))
                {
                    try { handle.Stop(); }
                    catch (Exception ex) { MelonLogger.Error($"[FCS] 停止任务失败: {ex}"); }
                }
            }
        }
        finally
        {
            // 先收束协程 finally（包括 OnClickUp），再统一 OFF，避免迟到松键重新武装。
            TriggerConsole.StopArmsNow();
            _stoppingExecution = false;
        }
        ReportExecutionBlock(reason);
    }

    private void ObserveTriggerSubmission(LeftRight side, ArtilleryTask task)
    {
        task.TriggerSubmitted = true;
        PlanRegistry.MarkTriggerSubmitted(task);
        RecordTriggerSubmitted(side, task);
    }

    /// <summary>恢复自动执行并重新验证后续计划；已出膛和结果未知任务不会重入队列。</summary>
    public void ResumeAutomaticExecution()
    {
        if (_disposing) return;
        if (UnobservedFlightPossible)
        {
            ReportExecutionBlock("Reload flight history unknown");
            return;
        }
        _execution.StartAutomatic();
        _automaticExecutionPaused = false;
        if (TriggerConsole.RecoveryPending && !TriggerConsole.TryAcknowledgeRecovery()
            && !_consoleRecoveryRunning)
        {
            _consoleRecoveryRunning = true;
            var generation = _execution.Generation;
            _consoleRecoveryHandle = StartOwnedRoutine(CancellableRoutine.Run(RecoverConsoleForAutomaticExecution(),
                () => !_disposing && !_automaticExecutionPaused && generation == _execution.Generation));
        }
        TryDispatch();
        try { OnGunIdle?.Invoke(); } catch (Exception ex) { MelonLogger.Error($"[FCS] 恢复后扫描失败: {ex}"); }
    }

    private IEnumerator RecoverConsoleForAutomaticExecution()
    {
        try
        {
            yield return TriggerConsole.RecoverArms();
            if (TriggerConsole.RecoveryPending)
                MelonLogger.Warning("[FCS] 控制台恢复未确认，暂不派发；等待实际武装状态可读");
        }
        finally { _consoleRecoveryRunning = false; }
        TryDispatch();
    }

    /// <summary>玩家确认现场无未登记在飞炮弹；同时复核实物，不以计时到期自动恢复。</summary>
    public bool AcknowledgeReloadSafety()
    {
        if (!IsBound || !LeftGun.MechanismSettled() || !RightGun.MechanismSettled()
            || LeftGun.IsPendingReload() || RightGun.IsPendingReload()
            || !TriggerConsole.TryAcknowledgeRecovery()) return false;
        UnobservedFlightPossible = false;
        return true;
    }

    private void CancelInvalidAutomaticTarget(ArtilleryTask task, string reason)
    {
        task.RevokeFireAuthorization(reason);
        PlanRegistry.Revoke(task, reason);
        task.progress = Progress.Canceled;
        if (task.ShouldReleaseTargetOnExit) Registry.Release(task);
    }

    /// <summary>炮管打完一发后释放槽位并尝试拉取队列里的下一个任务。</summary>
    private void ReleaseSlot(LeftRight leftRight, ArtilleryTask owner) {
        var current = leftRight == LeftRight.Left ? LeftTask : RightTask;
        if (!ReferenceEquals(current, owner) || current.SlotLeaseId != owner.SlotLeaseId
            || !_slotLeases.IsOwner(leftRight.ToString(), owner.SlotLeaseId))
        {
            MelonLogger.Warning($"[FCS] 忽略过期任务 T{owner.targetId} 的 {leftRight} 炮位释放 " +
                                $"(lease={owner.SlotLeaseId}, current={(current == null ? "empty" : $"T{current.targetId}/{current.SlotLeaseId}")})");
            return;
        }

        // 守护层必须先 Dispose 嵌套动作、收束后台子动作，才能启动实物交接。
        if (_disposing || _activeTaskRoutines.Contains(owner)) return;

        // 全局扳机没有炮管选择。已确认武装的任务即使已经取消、失败或击发完成，
        // 也必须先完成物理解除武装，再把炮位交给下一代任务。
        if (owner.FireResultUnknown) {
            RequestUnknownFireHold(leftRight, owner);
            return;
        }

        RequestArmCleanup(leftRight, owner);
    }

    private void ClearSlot(LeftRight leftRight, ArtilleryTask owner) {
        var current = leftRight == LeftRight.Left ? LeftTask : RightTask;
        if (!ReferenceEquals(current, owner) || current.SlotLeaseId != owner.SlotLeaseId
            || !_slotLeases.IsOwner(leftRight.ToString(), owner.SlotLeaseId))
        {
            MelonLogger.Warning($"[FCS] 忽略过期任务 T{owner.targetId} 的 {leftRight} 炮位清理 "
                                + $"(lease={owner.SlotLeaseId}, current={(current == null ? "empty" : $"T{current.targetId}/{current.SlotLeaseId}")})");
            return;
        }

        if (leftRight == LeftRight.Left) LeftTask = null;
        else RightTask = null;
        _slotLeases.Release(leftRight.ToString(), owner.SlotLeaseId);
        MelonLogger.Msg($"[FCS] 实物交接完成 P{owner.PlanId} {leftRight} lease={owner.SlotLeaseId}");
        owner.SlotLeaseId = 0;
        TryDispatch();
    }

    private void RequestArmCleanup(LeftRight leftRight, ArtilleryTask owner) {
        if (!_armCleanupInProgress.Add(owner)) return;
        StartOwnedRoutine(DisarmAndReleaseSlot(leftRight, owner));
    }

    private void RequestUnknownFireHold(LeftRight leftRight, ArtilleryTask owner) {
        if (!_unknownFireHoldInProgress.Add(owner)) return;
        StartOwnedRoutine(HoldUnknownFire(leftRight, owner));
    }

    private IEnumerator HoldUnknownFire(LeftRight leftRight, ArtilleryTask owner) {
        try {
            while (true) {
                var current = leftRight == LeftRight.Left ? LeftTask : RightTask;
                if (!ReferenceEquals(current, owner) || current.SlotLeaseId != owner.SlotLeaseId)
                    yield break;

                if (owner.ArmConfirmed) {
                    yield return TriggerConsole.Disarm(leftRight, owner.SlotLeaseId);
                    if (TriggerConsole.LastDisarmConfirmed)
                        owner.ArmConfirmed = false;
                    else
                        MelonLogger.Error($"[FCS] {leftRight} 击发结果未知且解除武装未确认，继续隔离全局扳机");
                }

                // 结果未知的炮位不自动释放；只能由实物读数确认后通过
                // TryAcknowledgeUnknownFire 交给下一项计划。
                yield return new WaitForSeconds(5f);
            }
        }
        finally {
            _unknownFireHoldInProgress.Remove(owner);
        }
    }

    /// <summary>
    /// 玩家或恢复流程在确认膛空、机构静止且武装已解除后调用，结束“出膛未知”隔离。
    /// 未满足全部实物条件时返回 false，避免把未知状态重新当作可用炮位。
    /// </summary>
    public bool TryAcknowledgeUnknownFire(LeftRight side) {
        var task = side == LeftRight.Left ? LeftTask : RightTask;
        var gun = side == LeftRight.Left ? LeftGun : RightGun;
        if (task == null || !task.FireResultUnknown || task.ArmConfirmed) return false;
        if (!TriggerConsole.TryReadArmState(side, out var armed, out _)
            || armed || !gun.IsChamberEmpty() || !gun.ReloadIdle()) return false;

        task.FireResultUnknown = false;
        task.FireResultUnknownAt = 0f;
        task.progress = Progress.Failed;
        task.CancelReason = "玩家确认击发结果未知并完成机构接管";
        Registry.Release(task);
        ClearSlot(side, task);
        return true;
    }

    private IEnumerator DisarmAndReleaseSlot(LeftRight leftRight, ArtilleryTask owner) {
        try {
            while (true) {
                var current = leftRight == LeftRight.Left ? LeftTask : RightTask;
                if (!ReferenceEquals(current, owner) || current.SlotLeaseId != owner.SlotLeaseId)
                    yield break;

                var gun = leftRight == LeftRight.Left ? LeftGun : RightGun;
                if (owner.ArmConfirmed)
                {
                    yield return TriggerConsole.Disarm(leftRight, owner.SlotLeaseId);
                    if (TriggerConsole.LastDisarmConfirmed) owner.ArmConfirmed = false;
                }
                if (!_activeTaskRoutines.Contains(owner) && !owner.ArmConfirmed
                    && TriggerConsole.TryReadArmState(leftRight, out var armed, out _) && !armed
                    && gun.MechanismSettled() && !gun.IsPendingReload())
                {
                    ClearSlot(leftRight, owner);
                    yield break;
                }

                // 姿态未知或动画没有完成时，继续保留炮位和全局扳机隔离；
                // 下一次尝试会重新读取实物，玩家手动解除后也能自然恢复。
                owner.CancelReason = "等待解除武装、机构静止和膛内状态确认";
                yield return new WaitForSeconds(1f);
            }
        }
        finally {
            _armCleanupInProgress.Remove(owner);
        }
    }

    private bool HasUnsafeArmForAutomaticFire() {
        if (!IsBound) return false;
        if (TriggerConsole.RecoveryPending) return true;
        foreach (var side in new[] { LeftRight.Left, LeftRight.Right }) {
            if (!TriggerConsole.TryReadArmState(side, out var armed, out _)) return true;
            if (!armed) continue;
            var task = side == LeftRight.Left ? LeftTask : RightTask;
            if (task == null || task.Source != TaskSource.Auto || !_execution.IsAuthorized(task) || !task.ArmConfirmed)
                return true;
        }
        return false;
    }

    private void MarkFireResultUnknown(LeftRight side, ArtilleryTask task, string reason) {
        task.FireResultUnknown = true;
        task.FireResultUnknownAt = Time.time;
        task.FireAuthorized = false;
        task.forceFire = false;
        task.Canceled = true;
        task.progress = Progress.FirePending;
        task.CancelReason = reason;
        var submitted = FindFireRecord(task);
        if (submitted != null)
            submitted.MarkUnknown(task.FireResultUnknownAt, reason);
        else
            FireRecords.Add(new FireRecord(task, side, FireRecordState.ResultUnknown,
                task.FireResultUnknownAt, reason));
        PlanRegistry.MarkFireUnknown(task, reason);
        MelonLogger.Error($"[FCS] T{task.targetId} {reason}；保留目标登记和炮位，禁止自动复用");
    }

    private FireRecord? FindFireRecord(ArtilleryTask task)
    {
        return FireRecords.LastOrDefault(record =>
            record.PlanId == task.PlanId && record.PlanRevision == task.PlanRevision);
    }

    private void RecordTriggerSubmitted(LeftRight side, ArtilleryTask task)
    {
        if (FindFireRecord(task) != null) return;
        FireRecords.Add(new FireRecord(task, side, FireRecordState.Submitted, Time.time));
    }

    private void RecordLaunched(LeftRight side, ArtilleryTask task)
    {
        var record = FindFireRecord(task);
        if (record == null)
        {
            record = new FireRecord(task, side, FireRecordState.Launched, task.FiredAt);
            FireRecords.Add(record);
        }
        else
            record.MarkLaunched(task);
    }

    private void MarkFireRecordFlightWindowExpired(ArtilleryTask task, string reason)
    {
        FindFireRecord(task)?.MarkFlightWindowExpired(reason);
    }

    private void MarkFireRecordCanceled(ArtilleryTask task)
    {
        if (!task.TriggerSubmitted && !task.Fired) FindFireRecord(task)?.MarkCanceled(task.CancelReason);
    }

    private void RecordConfirmedLaunch(LeftRight leftRight, ArtilleryTask task)
    {
        if (task.Fired) return;
        // 爆区覆盖: 落点 = 静态任务目标/集群质心, 移动任务提前点; 半径 = 该弹毁伤半径
        Vector3 impactPos = task.position;
        if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel))
        {
            var tof = ToFTable.FlightTime(task.distance, task.LoadedCharge);
            impactPos = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel,
                Time.time - task.AimStartTime, tof);
        }
        float blastKm = task.BlastRadiusKm > 0f ? task.BlastRadiusKm : ShellData.BlastRadiusKm(task.bulletType);
        task.Fired = true;
        task.FiredAt = Time.time;
        RecordLaunched(leftRight, task);
        PlanRegistry.MarkFireConfirmed(task);
        Registry.MarkFiredBlast(task, impactPos, blastKm);
        // 集群成员派发时已登记(Commit), 击发无需再登记
        InFlight.Add(task);   // 面板倒计时从这里开始, 归零(估计落地)时移除
    }

    private IEnumerator ObserveStoppedShot(LeftRight side, ArtilleryTask task)
    {
        var gun = side == LeftRight.Left ? LeftGun : RightGun;
        yield return gun.WaitFire();
        if (!gun.LastFireConfirmed)
        {
            MarkFireResultUnknown(side, task, "停止前已提交扳机，出膛未确认");
            yield break;
        }
        RecordConfirmedLaunch(side, task);
        task.progress = Progress.BackToIdle;
        yield return gun.WaitBackToIdle();
        task.progress = gun.LastBackToIdleConfirmed ? Progress.Finished : Progress.Failed;
    }

    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;

        // 目标在入队和真正启动之间也可能消失；机构动作前再做一次实时复核。
        var trackId = task.TrackEntityId is { Length: > 0 } ? task.TrackEntityId : task.entityId;
        if (task.Source == TaskSource.Auto
            && (EntityLocator == null || string.IsNullOrEmpty(trackId)
                || !EntityLocator.TryGetLiveHostilePosition(trackId, out _)))
        {
            CancelInvalidAutomaticTarget(task, "执行前目标已死亡、离场或位置不可解析");
            ReleaseSlot(leftRight, task);
            yield break;
        }

        if (task.UsesRetainedRound && (gunSys.AssessChamber() != GunSystem.ChamberState.ReadyToFire
            || !gunSys.ChamberMatches(task.bulletType) || gunSys.ChamberCharges() != task.LoadedCharge))
        {
            task.progress = Progress.Failed;
            task.CancelReason = "保留弹实物已改变，等待重新规划";
            yield break;
        }

        // ===== 炮塔预约：任务一开始就在后台抢炮塔锁并装填期追方位 =====
        // 方向旋转与装填/升仰角不冲突。后台协程阻塞式抢炮塔锁（"一旦释放就立即获取"），
        // 装填期每帧追实时方位(装填要求仰角静止)；装填完成后主流程接管双机构驱动。
        // 炮塔方向必须独占到这一发打出去为止，故锁一直持有到击发完成(ReleaseOnce 归还)。
        var turret = new TurretReservation();
        _taskTurretRes[task] = turret;   // 看门狗强制释放炮塔锁用
        // 独立的 fire-and-forget 协程，必须登记以便 Dispose 时一并 Stop，
        // 否则热重载后旧 ALC 的它仍被 Unity 驱动 → 崩溃。
        StartTaskChild(leftRight, task, ReserveTurretAndRotate(task, turret));

        // ===== 临界区 1：采购炮弹 =====
        // 仰角/方向角由 aim(t) 公式每帧算, 游戏计算器仅作并行装饰同步(见 SyncCalculatorVisual)。
        // desk 锁只保护采购台(全局唯一硬件), 两门炮"抢占"问题随之消失。
        bool viable = true;
        bool slotReleased = false;
        var selectionLease = _deskLock.CreateLease();
        yield return _deskLock.Acquire(selectionLease);
        try {
            task.progress = Progress.SelectingBullet;
            // 弹仓里没有目标弹种则采购（采购台也是共享硬件，放在锁内）。
            // 未解锁弹种(如 LE)采购点击无效——采购后核验弹是否真进舱, 未进则沿回退链换弹重试,
            // 避免"LE 永远买不到 → 任务失败 → 重新派发再试 LE"的死循环。
            // A matching retained shell already belongs to this shot. Cylinder stock
            // and capacity must not block reuse or cause a fallback to another shell.
            if (task.UsesRetainedRound)
            {
                viable = gunSys.AssessChamber() == GunSystem.ChamberState.ReadyToFire
                    && gunSys.ChamberMatches(task.bulletType) && gunSys.ChamberCharges() == task.LoadedCharge;
                if (!viable)
                {
                    task.progress = Progress.Failed;
                    task.CancelReason = "保留弹实物已改变，等待重新规划";
                }
            }
            else if (!gunSys.ChamberMatches(task.bulletType) && !gunSys.HaveBulletInCylinder(task.bulletType)) {
                if (!gunSys.HaveEmptyShellInCylinder()) {
                    task.progress = Progress.Failed;
                    viable = false;
                }
                else {
                    for (var attempt = 0; attempt < 4; attempt++) {
                        // 先退出采购循环，再走下方统一交接；直接 yield break
                        // 会绕过炮塔预约和炮位的 finally 收尾。
                        if (task.Canceled) { task.progress = Progress.Canceled; break; }
                        yield return _purchaseDeck.BuyShell(task.bulletType, leftRight);
                        if (gunSys.HaveBulletInCylinder(task.bulletType)) break;   // 到货
                        var next = FallbackShell(task.bulletType);
                        if (task.Source == TaskSource.Manual)
                        {
                            task.CancelReason = "指定弹种未采购成功，禁止自动替换弹种";
                            task.progress = Progress.Failed;
                            viable = false;
                            break;
                        }
                        if (next == task.bulletType) break;   // 链尽头, 不再换
                        MelonLogger.Msg($"[FCS] {leftRight} 弹种 {task.bulletType} 不可用(未解锁?), 回退 {next}");
                        task.bulletType = next;
                    }
                }
            }
        }
        finally {
            _deskLock.Release(selectionLease);
        }

        try {
            // 3b 切割点: 切手动时置 Canceled 的自动任务在此干净放弃——
            // 还没碰炮膛(未 LoadBullet), 无卡膛风险。复用 viable 分支的 abort 模式。
            if (task.Canceled) {
                task.progress = Progress.Canceled;
                turret.Canceled = true;
                ReleaseTurretOnce(turret);
                if (task.ShouldReleaseTargetOnExit) Registry.Release(task);
                ReleaseSlot(leftRight, task);
                slotReleased = true;
                yield break;
            }
            if (!viable) {
                // 任务不可行：取消炮塔预约并归还（后台若尚未抢到，会在抢到后自行归还），
                // 并释放炮管槽位让队列里的下一个任务能用这管炮。
                turret.Canceled = true;
                ReleaseTurretOnce(turret);
                ReleaseSlot(leftRight, task);
                slotReleased = true;
                yield break;
            }

            // ===== 锁外：装填（每管炮独立，最耗时段，可与另一管炮全程并行）=====
            // 膛内状态接管(2026-08-15): 每发任务都先评估膛内状态——手动半装填/任务中断/
            // 转移阵地后炮管可能停在任意中间态, 按状态分流:
            //   ReadyToFire: 弹+药已装好(CanFire) → 跳过推弹+装药, 直接复用(省整个装填周期)
            //   ShellLoaded: 弹在膛未就绪 → 弹种匹配则跳过推弹只装药; 不匹配则保留待命
            //   Empty: 正常装填
            // 2026-08-16: 移除手动弹保护(用户)——膛内任何弹(含手动装的)都按上述状态接管,
            // 弹种匹配就复用；不匹配则保留，退弹需要独立授权。
            task.progress = Progress.LoadingBullet;
            bool skipLoadBullet = false;   // 跳过推弹(膛内已有匹配弹)
            bool reuseReady = false;       // 整发复用(跳过推弹+装药, 直接瞄准)
            var chamberState = gunSys.AssessChamber();
            if (task.UsesRetainedRound && (chamberState != GunSystem.ChamberState.ReadyToFire
                || !gunSys.ChamberMatches(task.bulletType) || gunSys.ChamberCharges() != task.LoadedCharge))
            {
                task.progress = Progress.Failed;
                task.CancelReason = "保留弹实物已改变，等待重新规划";
                yield break;
            }
            if (chamberState == GunSystem.ChamberState.Dirty)
            {
                task.progress = Progress.Failed;
                task.CancelReason = "膛内或机构状态未知，需要手动复核";
                yield break;
            }
            if (chamberState == GunSystem.ChamberState.ReadyToFire)
            {
                // 整发已装填完毕未发射(手动装好/上一任务遗留)。
                // 复用前提: 弹种匹配 + 膛内装药 ≥ 本次所需。
                //   装药 == 所需 → 直接复用, 仰角按所需算
                //   装药 > 所需  → 复用但仰角按【实际装药】算(装药多=射程远, 调低仰角打准,
                //                    不必退壳浪费这发——2026-08-15 用户指出)
                //   装药 < 所需  → 射程不足则保留待命
                //   装药读取失败 → 保留待核查
                if (gunSys.ChamberMatches(task.bulletType))
                {
                    // 先按当前距离算所需装药, 再比对
                    AdoptVelocityIfNeeded(task);
                    var d0 = task.distance;
                    var dLead = d0;
                    if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel)) {
                        var t0 = ToFTable.FlightTime(d0, TargetLeadSolver.ChargeFor(d0));
                        var f0 = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel, Time.time - task.AimStartTime + 60f, t0);
                        dLead = Mathf.Max(d0, DistKm(f0));
                    }
                    var needCharges = task.useMaxCharge || _sceneInteractor.maxCharge
                        ? 6
                        : Mathf.Min(6, TargetLeadSolver.ChargeFor(
                            dLead + (task.IsMoving ? MovingDistanceMarginKm : 0f)));
                    int chamberCharges = gunSys.ChamberCharges();
                    // 多于最低装药仍可复用；按实际装药解算，不能拿推荐装药替代实物。
                    var reuseOk = task.UsesRetainedRound
                        ? chamberCharges == task.LoadedCharge && RetainedAmmoPolicy.CanReach(task.distance, chamberCharges)
                        : chamberCharges is >= 1 and <= 6 && chamberCharges >= needCharges;
                    if (reuseOk)
                    {
                        // 装药匹配 → 复用。仰角按实际装药定格。
                        if (chamberCharges > needCharges)
                            MelonLogger.Msg($"[FCS] {leftRight} 膛内装药 {chamberCharges}包 > 所需 {needCharges}包, 复用并按实际装药瞄准");
                        reuseReady = true;
                        task.LoadedCharge = chamberCharges;   // 实际装药, 覆盖公式值
                    }
                    else
                    {
                        MelonLogger.Msg($"[FCS] {leftRight} 膛内装药 {chamberCharges}包 不足或读数无效，所需 {needCharges}包(dist={task.distance:F2}km)，保留待命");
                        task.progress = Progress.Failed;
                        task.CancelReason = "保留装药不兼容；需要明确退弹或选择其他炮管";
                        yield break;
                    }
                }
                else
                {
                    // 弹种不匹配时保留整发，不隐式退弹。
                    MelonLogger.Msg($"[FCS] {leftRight} 膛内弹种 {gunSys.BulletInChamber()} ≠ 期望 {task.bulletType}，保留待命");
                    task.progress = Progress.Failed;
                    task.CancelReason = "保留弹种不兼容；需要明确退弹或选择其他炮管";
                    yield break;
                }
            }
            else if (chamberState == GunSystem.ChamberState.ShellLoaded)
            {
                // 弹在膛但未就绪(半装填): 弹种匹配 → 跳过推弹(避免推弹杆顶到已有弹)
                if (gunSys.ChamberMatches(task.bulletType))
                {
                    MelonLogger.Msg($"[FCS] {leftRight} 膛内已有 {gunSys.BulletInChamber()}(半装填), 跳过推弹直接装药");
                    skipLoadBullet = true;
                }
                else
                {
                    // 弹种不匹配时保留弹头。
                    MelonLogger.Msg($"[FCS] {leftRight} 膛内残留 {gunSys.BulletInChamber()} ≠ 期望 {task.bulletType}，保留待命");
                    task.progress = Progress.Failed;
                    task.CancelReason = "保留弹种不兼容；需要明确退弹或选择其他炮管";
                    yield break;
                }
            }

            // ===== 推弹（除非跳过: 膛内已有匹配弹/整发复用）=====
            if (!skipLoadBullet && !reuseReady)
            {
                // 决定推弹即标 Auto(2026-08-16): 来源检测 5s 轮询可能在"推弹完成→确认弹种"
                // 之间把自动装的弹误判成用户手动。此刻膛必空(状态接管已分流), 标 Auto 安全;
                // 任务中断遗留的弹也保持 Auto(可复用), 不再被误标 Manual 堵死本管。
                gunSys.MarkAutoLoaded();
                yield return gunSys.LoadBullet(task.bulletType, () => task.Canceled);
                // LoadBullet 只是点推弹杆按钮——炮弹从挂架到炮膛需要机械时间，
                // 不能立刻读 BulletInChamber()，否则会误判为装填失败。
                {
            var chamberTimeout = 0;
            while (gunSys.BulletInChamber() == null && chamberTimeout < 30) {
                if (task.Canceled) { task.progress = Progress.Canceled; yield break; }
                yield return new WaitForSeconds(0.5f);
                chamberTimeout++;
            }
                    if (gunSys.BulletInChamber() != task.bulletType.ToString()) {
                        MelonLogger.Error($"[FCS] {leftRight} 炮管：装填后弹种不匹配，" +
                                          $"期望 {task.bulletType}，实际 {gunSys.BulletInChamber() ?? "null"}");
                        task.progress = Progress.Failed;
                        yield break;
                    }
                    gunSys.MarkAutoLoaded();   // 自动装填成功, 来源标 Auto
                }
            }
            else if (skipLoadBullet)
            {
                // 复用膛内匹配弹(ShellLoaded 半装填): 弹是自动任务遗留的(Auto 来源) → 保持 Auto
            }

            // ===== 装药决策（装弹完成后定格）=====
            // 装弹(转弹仓+推弹)必远超雷达 5s 扫描周期 → 冷启动目标此刻速度必然已建立并被采纳,
            // 按真实提前量定格装药, 无需猜测余量。静止目标 = ChargeFor(当前距离)。
            // ReadyToFire 复用: LoadedCharge 已在状态接管段设为实际装药(≥所需), 这里不覆盖。
            int powderCount;
            if (!reuseReady)
            {
                AdoptVelocityIfNeeded(task);
                var distNowKm = task.distance;
                var distLeadMaxKm = distNowKm;
                if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel)) {
                    var tof = ToFTable.FlightTime(distNowKm, TargetLeadSolver.ChargeFor(distNowKm));
                    var far = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel, Time.time - task.AimStartTime + 60f, tof);
                    distLeadMaxKm = Mathf.Max(distNowKm, DistKm(far));
                }
                powderCount = task.useMaxCharge || _sceneInteractor.maxCharge
                    ? 6
                    : Mathf.Min(6, TargetLeadSolver.ChargeFor(
                        distLeadMaxKm + (task.IsMoving ? MovingDistanceMarginKm : 0f)));
                task.LoadedCharge = powderCount;
                task.EstimatedToF = ToFTable.FlightTime(task.distance, powderCount);   // 面板倒计时起点
                MelonLogger.Msg($"[FCS] {leftRight} 装药决策: task.max={task.useMaxCharge} btn.max={_sceneInteractor.maxCharge} dist={task.distance:F2} → {powderCount}包");
            }
            else
            {
                // 复用膛内已装弹: LoadedCharge 已在状态接管段定格(实际装药), 补射表
                powderCount = task.LoadedCharge;
                task.EstimatedToF = ToFTable.FlightTime(task.distance, task.LoadedCharge);
                MelonLogger.Msg($"[FCS] {leftRight} 装药决策(复用膛内): dist={task.distance:F2} 实际装药={task.LoadedCharge}包");
            }

            // 计算器纯装饰视觉同步: 与装填/瞄准并行(不进任务关键路径)。desk 锁短持有, 登记以便 Dispose 停掉。
            if (!reuseReady) StartTaskChild(leftRight, task, SyncCalculatorVisual(task, powderCount));

            // ===== 临界区 2：装药采购（定格后不足才买; 弹种装填失败不再浪费装药）=====
            // ReadyToFire 复用路径已装好药 → 跳过采购+推药。
            if (!reuseReady)
            {
                task.progress = Progress.LoadingPowder;
                var powderLease = _deskLock.CreateLease();
                yield return _deskLock.Acquire(powderLease);
                try {
                    // 单次采购未必补满（偶发点击早于卡牌入槽而失败），循环购买直到够本次发射所需;
                    // 购买次数上限兜底, 采购始终无效时不至于无限循环（每次约 2.5s）。
                    // 补购失败＝任务不可行（finally 兜底释放槽位/炮塔/登记）。
                    var powderPurchaseAttempts = 0;
                    while (gunSys.RemainingCharges() < powderCount) {
                        if (task.Canceled) { task.progress = Progress.Canceled; viable = false; yield break; }
                        var chargesBefore = gunSys.RemainingCharges();
                        yield return _purchaseDeck.BuyPowders();
                        // 无效快速失败(2026-08-19 热重载局): 点数不足/采购无效 → 包数不加。
                        // 空转 10 次要烧穿 120s 看门狗被强杀, 死协程残留互踩同管后继任务(13° 空炮)。
                        // 本发快速失败, 等点数恢复后重派。
                        if (gunSys.RemainingCharges() <= chargesBefore) {
                            MelonLogger.Error($"[FCS] {leftRight} 装药采购无效(包数 {chargesBefore} 未增加, 点数不足或采购失败), 放弃本发");
                            task.progress = Progress.Failed;
                            viable = false;
                            yield break;
                        }
                        if (++powderPurchaseAttempts >= 10) {
                            MelonLogger.Error(
                                $"[FCS] {leftRight} 炮管：购买装药 {powderPurchaseAttempts} 次后仍不足 " +
                                $"{powderCount}（当前 {gunSys.RemainingCharges()}），停止补购。");
                            task.progress = Progress.Failed;
                            viable = false;
                            yield break;
                        }
                    }
                }
                finally {
                    _deskLock.Release(powderLease);
                }

                yield return gunSys.LoadPowder(powderCount, () => task.Canceled);
                task.progress = Progress.WaitLoading;
                var loadTimeout = 0;
                while (!gunSys.CanFire()) {
                    if (task.Canceled) { task.progress = Progress.Canceled; yield break; }
                    yield return new WaitForSeconds(1f);
                    if (++loadTimeout >= 120) { // 2 分钟超时
                        MelonLogger.Error($"[FCS] {leftRight} 炮管：等待装填完成超时");
                        task.progress = Progress.Failed;
                        yield break;
                    }
                }
            }

            // ===== 锁外：连续瞄准跟踪 =====
            // 装填已完成 → 仰角可动。每帧重算 aim(t) 并驱动双机构, 直到收敛 + CanFire。
            // 移动目标走提前量(冻结快照外推); 静态退化为恒定 aim（同一循环, 无分类边界）。
            // 后台协程停止驱动方位, 主流程接管。
            turret.PostLoad = true;
            // 击发串行化门(6bf05e9 回归修复): 全局扳机 Fire() 击发所有臂杆已拉下的炮管,
            // 两管任务同时进击发段 = 双管齐射。锁由后台持有到击发完成(ReleaseTurretOnce)。
            // 仰角杆每管独立——等锁期间照样追仰角(与另一管并行), 只有方位/击发段被锁串行化。
            task.progress = Progress.Aiming;
            // 各炮管可并行装填，方位、武装与击发必须持锁串行。
            while (!turret.Acquired) {
                if (task.Canceled) { task.progress = Progress.Canceled; yield break; }
                TryComputeAimTargets(task, out _, out var elev);
                gunSys.SetElevationTarget(elev);
                yield return null;
            }
            turret.Aiming = true;   // 主流程接管方位(后台停止驱动)
            MelonLogger.Msg($"[FCS] {leftRight} 炮塔锁已持有, 进入瞄准/击发段");
            RecheckStaticBearing(task, leftRight);   // 基准复核: 转移已漂移基准就在此纠正定格方位

            var aimTrackStart = Time.time;
            while (true) {
                // 紧急移动/切手动: 转移阵地会重置炮管状态(弹被卸下), 瞄准永远不收敛 →
                // 置 Canceled 后在此干净退出(锁/槽位/登记由 finally 兜底释放)。
                if (task.Canceled) { task.progress = Progress.Canceled; yield break; }
                TryComputeAimTargets(task, out var bearingTarget, out var elevTarget);
                // 出装药覆盖检查: 移动目标提前点距离超过装药射程 → 退化
                if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel)) {
                    var tof = ToFTable.FlightTime(task.distance, task.LoadedCharge);
                    var leadDist = DistKm(TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel,
                        Time.time - task.AimStartTime, tof));
                    if (leadDist * 0.2f > task.LoadedCharge + 0.1f) {
                        task.progress = Progress.Failed;
                        yield break;
                    }
                }
                if (turret.Acquired)
                    Turret.SetDesiredRotation(bearingTarget);
                gunSys.SetElevationTarget(elevTarget);
                // 收敛判定对"当前帧目标值"(移动目标 aim 逐帧漂移, 机构追上 = 收敛)
                if (gunSys.ElevationError(elevTarget) < AimToleranceDeg
                    && Turret.AngleError(bearingTarget) < AimToleranceDeg
                    && gunSys.CanFire())
                    break;
                // 兜底: 时间制(帧数在 60fps 下只有 1/60 秒/次, 帧数会误杀 32s 仰角摆动)
                if (Time.time - aimTrackStart > 240f) { task.progress = Progress.Failed; yield break; }
                yield return null;
            }

            // ===== FDC 落地顺序保护(2026-08-15 用户提出, 08-16 流程 1 保留) =====
            // FDC 弹落地(≈击杀时刻)触发暂停, 暂停在"下一次炮弹落地"解除。
            // 若击发 FDC 时仍有旧弹在飞且其落地晚于 FDC 弹 → 旧弹成为"下一次落地" →
            // FDC 触发的暂停被立即解除, FDC 白费。
            // 约束: 击发前等所有在飞弹的剩余落地时间 < 本弹 ToF(旧弹先落地, FDC 弹最后落地,
            // 且旧弹至少提前 0.1s——"追求上一发炮弹落地 ≥0.1s 后 FDC 落地", 用户 08-16 流程设定)。
            // 不做专门的卡时间对齐(用户 08-16: 不值得——收益依赖 FDC 击杀成功, 且落地顺序
            // 保护已天然保证"旧弹先落地", 间隙≈击发段耗时即可接受)。
            if (task.IsFdc && ShouldAutomaticallyFire(task) && !task.forceFire && !task.Canceled)
            {
                float fdcToF = ToFTable.FlightTime(task.distance, task.LoadedCharge);
                var fdcCoordDeadline = Time.time + 45f;   // 最长等 45s(旧弹最晚落地), 防死等
                while (Time.time < fdcCoordDeadline && !task.Canceled)
                {
                    // 必须用【最晚】落地(2026-08-16 修正): SoonestImpactIn 返回最早, 只要有一发
                    // 旧弹比 FDC 先落地就放行——另一发更晚落地的旧弹会在 FDC 弹落地后立即解除暂停。
                    float latestImpact = LatestImpactIn();
                    if (latestImpact <= 0f || latestImpact + 0.1f < fdcToF) break;   // 旧弹都先落地 ≥0.1s(或无在飞弹)
                    // 诊断(2026-08-16 用户: FDC 等火炮弹落地才开火)——打印等待数值定位:
                    // fdcToF 正常(10s+)时 FDC 在火炮弹剩余 ≈ fdcToF 时击发(对齐); 异常小则等火炮弹落地。
                    if (Time.time - _lastFdcWaitLog > 10f)
                    {
                        _lastFdcWaitLog = Time.time;
                        MelonLogger.Msg($"[FCS] FDC 等旧弹落地: fdcToF={fdcToF:F1}s latestImpact={latestImpact:F1}s dist={task.distance:F2} 装药={task.LoadedCharge} (对齐点=剩余 {fdcToF - 0.1f:F1}s)");
                    }
                    // 有旧弹会在 FDC 弹之后落地 → 等它落地(维持瞄准)
                    TryComputeAimTargets(task, out var fb, out var fe);
                    Turret.SetDesiredRotation(fb);
                    gunSys.SetElevationTarget(fe);
                    yield return null;
                }
                if (task.Canceled) { task.progress = Progress.Canceled; yield break; }
            }

            // ===== 击发（turret 锁仍由后台持有, 直接确认+击发）=====
            task.progress = Progress.WaitingForFire;
            try {
                RecheckStaticBearing(task, leftRight);   // 击发前基准复核: 与上次调用(瞄准段入口)相隔 3-6s, 期间可能完成转移
                if (!_execution.IsAuthorized(task) || !PlanRegistry.IsAuthorized(task)) {
                    task.progress = Progress.Canceled;
                    yield break;
                }   // 计划或任务授权已撤销，不能进入全局扳机
                if (!RetainedAimSupported(task) || !gunSys.CanFire() || !gunSys.ChamberMatches(task.bulletType)
                    || gunSys.ChamberCharges() != task.LoadedCharge)
                {
                    const string reason = "击发前弹种/装药/机构状态与计划不一致，需要重新确认";
                    if (task.Source == TaskSource.Auto) StopAllExecution(reason, task);
                    else RevokeTask(task, reason, false);
                    task.progress = Progress.Canceled;
                    yield break;
                }
                // 臂杆按压与 5 项确认并行(开关是纯 0/1 标志, 一次性全点完, 耗时在 arm 保持期预算内)。
                // arm 自动落下 = 该炮管就绪(此刻方位/仰角已收敛, 不对错误角 arm 落下)
                yield return TriggerConsole.ArmWithFastConfirm(leftRight, task.SlotLeaseId);
                if (!TriggerConsole.LastArmConfirmed) {
                    if (TriggerConsole.ArmMayBeOn)
                        task.ArmConfirmed = true;
                    task.progress = Progress.Failed;
                    task.CancelReason = "武装姿态未确认";
                    MelonLogger.Error($"[FCS] {leftRight} 武装未通过物理姿态确认，不进入全局扳机段");
                    yield break;
                }
                task.ArmConfirmed = true;
                if (!task.MayFire) { task.progress = Progress.Canceled; yield break; }   // arm 后已撤销授权不再击发
                if (ShouldAutomaticallyFire(task)) {
                    // 确认序列 ~3-4s 期间 aim 漂移(机构只趋向旧 setpoint, 不会自行追新值);
                    // 复检窗口内主动驱动 aim(t) 追到对齐再打, 追不上 → 弃发(尽力打已删)。
                    var recheckStart = Time.time;
                    bool AimAligned() {
                        TryComputeAimTargets(task, out var rb, out var re2);
                        if (gunSys.ElevationError(re2) >= AimToleranceDeg) return false;
                        return Turret.AngleError(rb) < AimToleranceDeg;
                    }
                    // 复检=主动追赶(2026-08-19 用户实测: 动态目标完全追得上): arm 确认期 aim(t) 漂移,
                    // 机构只会趋向旧 setpoint, 干等追不上 → 每帧驱动最新 aim(t) 直到收敛
                    while (!AimAligned() && !task.Canceled && Time.time - recheckStart < 8f) {
                        TryComputeAimTargets(task, out var cb, out var ce);
                        if (turret.Acquired) Turret.SetDesiredRotation(cb);
                        gunSys.SetElevationTarget(ce);
                        yield return null;
                    }
                    if (task.Canceled) { task.progress = Progress.Canceled; yield break; }
                    // 追踪超时仍偏 → 放弃本发(尽力打已删, 2026-08-19 用户裁定): 偏大发必空炮
                    // (13° 仰角差 = 数百米偏差) → 本发失败, 炮管稳定后重派。
                    {
                        TryComputeAimTargets(task, out var gb, out var ge);
                        var elevGap = gunSys.ElevationError(ge);
                        var azGap = Turret.AngleError(gb);
                        if (elevGap > 2f || azGap > 1f) {
                            MelonLogger.Error($"[FCS] {leftRight} 复检追踪 8s 未收敛: 目标az={gb:F2}° 目标elev={ge:F2}° | 炮塔az={Turret.CurrentBearing:F2}°({(Turret.Rotating ? "转动中" : "停")}) 炮塔elev={gunSys.ElevationActual:F2}° azErr={azGap:F2}° elevErr={elevGap:F2}°, 放弃本发防空炮");
                            task.progress = Progress.Failed;
                            yield break;
                        }
                    }
                    if (turret.Acquired) {
                        // 空膛护栏: 膛内无弹时拉扳机 = 空炮(无弹飞出但任务照常标记已击发
                        // → 目标永不死 → 反复重派发)。宁可任务失败重装再来, 不打空炮。
                        if (gunSys.IsChamberEmpty()) {
                            MelonLogger.Error($"[FCS] {leftRight} 击发时空膛(弹丢失?), 放弃本任务防空炮");
                            task.progress = Progress.Failed;
                            yield break;
                        }
                        // 只有持锁者拉扳机，提交时再次核对另一管物理 OFF。
                        if (!task.MayFire) { task.progress = Progress.Canceled; yield break; }   // 拉扳机前最后一道闸
                        TryComputeAimTargets(task, out var fb, out var fe);
                        MelonLogger.Msg($"[FCS] {leftRight} 击发: {task.entityId} dist={task.distance:F2} 药={task.LoadedCharge} 弹={task.bulletType} " +
                            $"目标az={fb:F2}° 目标elev={fe:F2}° | 炮塔az={Turret.CurrentBearing:F2}°({(Turret.Rotating ? "转动中" : "停")}) 炮塔elev={gunSys.ElevationActual:F2}° " +
                            $"azErr={Turret.AngleError(fb):F2}° elevErr={gunSys.ElevationError(fe):F2}° 膛={gunSys.BulletInChamber()}");
                        if (!TriggerConsole.TryFire(leftRight, task.SlotLeaseId,
                                () => _execution.IsAuthorized(task) && PlanRegistry.IsAuthorized(task)
                                    && OwnsSlot(leftRight, task) && turret.Acquired && RetainedAimSupported(task)
                                    && gunSys.CanFire() && gunSys.ChamberMatches(task.bulletType)
                                    && gunSys.ChamberCharges() == task.LoadedCharge,
                                () => ObserveTriggerSubmission(leftRight, task))) {
                            task.progress = Progress.Failed;
                            task.CancelReason = "击发授权或另一管解除武装未确认";
                            yield break;
                        }
                        yield return gunSys.WaitFire();
                        if (!gunSys.LastFireConfirmed)
                        {
                            MarkFireResultUnknown(leftRight, task, "击发请求后未确认出膛");
                            yield break;
                        }
                        PlanRegistry.MarkFireConfirmed(task);
                    }
                } else {
                    // AutoFire 关: 持续跟踪 aim(t) 直到玩家扳机——否则炮管停在旧点, 移动目标必偏。
                    // 静态任务此循环目标值恒定, 退化为无操作等待。
                    // Canceled 检查点(fire-patches 移植): 看门狗取消后不再每帧驱动方位/占用资源。
                    while (!gunSys.IsPendingReload() && !task.Canceled) {
                        TryComputeAimTargets(task, out var b, out var e);
                        Turret.SetDesiredRotation(b);
                        gunSys.SetElevationTarget(e);
                        yield return null;
                    }
                    if (task.Canceled && !gunSys.IsPendingReload()) { task.progress = Progress.Canceled; yield break; }
                    if (!task.TriggerSubmitted) ObserveTriggerSubmission(leftRight, task);
                    // 玩家扳机时刻 = 击发时刻: 与 AutoFire 分支同格式记录, 空炮归因靠它
                    {
                        TryComputeAimTargets(task, out var mb, out var me);
                        MelonLogger.Msg($"[FCS] {leftRight} 玩家扳机: {task.entityId} dist={task.distance:F2} 药={task.LoadedCharge} 弹={task.bulletType} " +
                            $"目标az={mb:F2}° 目标elev={me:F2}° | 炮塔az={Turret.CurrentBearing:F2}°({(Turret.Rotating ? "转动中" : "停")}) 炮塔elev={gunSys.ElevationActual:F2}° " +
                            $"azErr={Turret.AngleError(mb):F2}° elevErr={gunSys.ElevationError(me):F2}° 膛={gunSys.BulletInChamber()}");
                    }
                }
                RecordConfirmedLaunch(leftRight, task);
                // 归还击发锁之前先 OFF，下一管不会遇到上一发遗留的武装。
                yield return TriggerConsole.Disarm(leftRight, task.SlotLeaseId);
                if (TriggerConsole.LastDisarmConfirmed) task.ArmConfirmed = false;
            }
            finally {
                ReleaseTurretOnce(turret);
            }

            // ===== 锁外：回位（仰角回 0，每管炮独立，最耗时段之一）=====
            task.progress = Progress.BackToIdle;
            yield return gunSys.WaitBackToIdle();
            if (!gunSys.LastBackToIdleConfirmed)
            {
                task.progress = Progress.Failed;
                task.CancelReason = "退膛回位未确认";
                MelonLogger.Error($"[FCS] {leftRight} 退膛回位超时，保留未知膛内状态，不宣布任务完成");
                yield break;
            }
            gunSys.MarkChamberEmpty();   // 击发+退壳完成, 膛空 → 来源复位
            task.progress = Progress.Finished;
            _sceneInteractor.TaskFinished(task);
            ReleaseSlot(leftRight, task);
            slotReleased = true;
        }
        finally {
            // 未击发的任务结束时解除登记(击发过的留给击杀确认/窗口到期)。
            // Canceled/Failed 分支幂等。
            MarkFireRecordCanceled(task);
            if (task.ShouldReleaseTargetOnExit) Registry.Release(task);
            PlanRegistry.Finish(task);
            // 防泄漏：协程崩了或 yield break 没走到 ReleaseSlot 时兜底释放
            if (!slotReleased) {
                ReleaseSlot(leftRight, task);
            }
            // 确保炮塔锁归还（已归还的 ReleaseTurretOnce 是幂等的）
            ReleaseTurretOnce(turret);
            _taskTurretRes.Remove(task);   // 看门狗映射清理
            // 通知雷达：有一门炮退膛完毕，可以刷新队列了
            try { OnGunIdle?.Invoke(); } catch { }
        }
    }

    /// <summary>
    /// 炮塔预约状态。四个标志全在主线程协作式调度下读写，无真正并发。
    /// 生命周期：后台 <see cref="ReserveTurretAndRotate"/> 抢锁并装填期追方位；
    /// 主流程装填完成后置 PostLoad 接管驱动；击发后 / 任务放弃时 ReleaseTurretOnce 归还。
    /// Released 保证恰好归还一次。
    /// </summary>
    private sealed class TurretReservation {
        public CoroutineLock.Lease? Lease;
        public bool Acquired;   // 已拿到炮塔锁
        public bool PostLoad;   // 装填完成(语义保留: 主流程接管时机)
        public bool Aiming;     // 主流程已拿到锁接管方位驱动(后台停止)
        public bool Canceled;   // 主流程已放弃本次预约
        public bool Released;   // 锁已归还（防重复 Release）
    }

    /// <summary>
    /// 后台预约炮塔。阻塞式抢锁实现"一旦炮塔释放就立即获取"。
    /// 抢到后若发现已被取消则立即归还；否则装填期每帧追实时方位
    /// （装填要求仰角静止, 只转方位）；装填完成后主流程接棒(PostLoad), 此处只持锁。
    /// </summary>
    private IEnumerator ReserveTurretAndRotate(ArtilleryTask task, TurretReservation res) {
        res.Lease = _turretLock.CreateLease();
        yield return _turretLock.Acquire(res.Lease);
        res.Acquired = true;
        // 主流程可能在抢到锁前已结束(取消/同方位齐射非持锁者提前完成)——拿到锁立即归还, 防锁泄漏。
        if (res.Canceled || res.Released) {
            _turretLock.Release(res.Lease);
            yield break;
        }
        while (!res.Released) {
            // 主流程未接管前持续追方位(装填期+等锁期); 拿到锁接管后(Aiming)停止。
            if (!res.Aiming && TryGetTaskBearing(task, out var bearing))
                Turret.SetDesiredRotation(bearing);
            yield return null;
        }
    }

    /// <summary>归还炮塔锁。Released 标记"主流程已结束占用"(无论是否持锁), 后台抢到锁时据此自归。
    /// 只在确实持锁(Acquired)时立即释放; 未持锁的提前结束(同方位齐射非持锁者)由后台拿到锁后归还。</summary>
    private void ReleaseTurretOnce(TurretReservation res) {
        if (res.Released) return;
        res.Released = true;
        if (res.Acquired && res.Lease != null) _turretLock.Release(res.Lease);
    }

    // ===== 连续瞄准几何与 aim 辅助（统一路径共用）=====

    /// <summary>缓存地图面 + turret 引用（一次, TryBind 时调用）。避免每帧 GameObject.Find。
    /// 优先用真实炮塔 TurretLocation(转移阵地时游戏移动它, 实时位置即新基准);
    /// 找不到时回退 Player Turret Piece(可拖动标记)。</summary>
    private void CacheAimGeometry() {
        _mapSurface = GameObject.Find("Draggable Surface")?.transform;
        _turretXf = MapTable.TurretLocation ?? MapTable.Turret;
    }

    /// <summary>几何引用刷新(2026-08-15): 紧急移动转移阵地可能销毁重建 Piece/地图面,
    /// 缓存变 fake-null → Bearing/DistKm 返回 0 → 打空。仅紧急移动完成后显式调用一次,
    /// 不做每帧 Find——GameObject.Find 是 O(n) 场景搜索, 每帧执行是性能杀手。</summary>
    private void RefreshAimGeometry() {
        _mapSurface = GameObject.Find("Draggable Surface")?.transform;
        _turretXf = MapTable.TurretLocation ?? MapTable.Turret;
    }

    /// <summary>瞄准用地图面(懒自愈, 2026-08-16 fire-patches-abandoned 移植): 转移阵地销毁
    /// 重建后缓存变 fake-null → Bearing/DistKm 静默返回 0 → 连续打错方向(azErr=0 查不出来)。
    /// 仅在 null 时补一次 Find(一次性成本), 不做每帧 Find。</summary>
    private Transform? AimSurface {
        get {
            if (_mapSurface == null)
                _mapSurface = GameObject.Find("Draggable Surface")?.transform;
            return _mapSurface;
        }
    }

    /// <summary>瞄准用炮塔基准(懒自愈): MapTable.TurretLocation 自身是懒引用, fake-null 时
    /// 重取自动拿到转移重建后的新对象, 不再静默用过期基准。</summary>
    private Transform? AimTurretBase {
        get {
            if (_turretXf == null)
                _turretXf = MapTable.TurretLocation ?? MapTable.Turret;
            return _turretXf;
        }
    }

    /// <summary>世界坐标 → 距离 km（与 TacticalRadar.CalcDistance 同换算）</summary>
    private float DistKm(Vector3 worldPos) {
        var surface = AimSurface;
        var turretXf = AimTurretBase;
        if (surface == null || turretXf == null) return 0f;
        // 统一转地图面局部坐标: Piece 父=地图面(localPosition 即局部), TurretLocation 父=MapRoot
        // (须 InverseTransformPoint 世界坐标)。用世界坐标转换对两者都正确(2026-08-15)。
        var turretLocal = surface.InverseTransformPoint(turretXf.position);
        var target = surface.InverseTransformPoint(worldPos) - turretLocal;
        return target.magnitude * 3.8164f;
    }

    /// <summary>世界坐标 → 方位角（与 TacticalRadar.CalcAngle 同逻辑）</summary>
    private float Bearing(Vector3 worldPos) {
        var surface = AimSurface;
        var turretXf = AimTurretBase;
        if (surface == null || turretXf == null) return 0f;
        var turretLocal = surface.InverseTransformPoint(turretXf.position);
        var target = surface.InverseTransformPoint(worldPos) - turretLocal;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        return angle < 0 ? angle + 360f : angle;
    }

    /// <summary>当前 aim 目标值。移动目标从冻结快照外推; 静态用任务创建时定格的固定值
    /// (angel/distance)——大部分目标是静态, 固定值零每帧开销(2026-08-15 性能回归)。
    /// 固定值由雷达按真实炮塔 TurretLocation 基准算好, 紧急移动转移后新派发的任务
    /// 自动用新基准, 不需要每帧重算。返回是否算移动。</summary>
    private bool TryComputeAimTargets(ArtilleryTask task, out float bearing, out float elev) {
        AdoptVelocityIfNeeded(task);
        bearing = task.angel;
        elev = TargetLeadSolver.Elevation(task.distance, task.LoadedCharge);
        if (!task.IsMoving || !TargetLeadSolver.IsMoving(task.AimVel)) return false;
        var tof = ToFTable.FlightTime(task.distance, task.LoadedCharge);
        var aim = TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel, Time.time - task.AimStartTime, tof);
        bearing = Bearing(aim);
        elev = TargetLeadSolver.Elevation(DistKm(aim), task.LoadedCharge);
        return true;
    }

    /// <summary>
    /// 静态任务基准复核(2026-08-16 fire-patches-abandoned 移植, 打错方向三连发修复):
    /// 任务方位/距离在派发时按当时雷达基准定格; 转移阵地/地图面重建后基准漂移 →
    /// 连续朝错误方位开炮(azErr=0 看不出: 瞄准忠实追错误基准)。用当前几何对实体最新
    /// 位置重算, 偏差 >1° 更新任务方位/距离并重瞄(集群任务=领队位置+集群偏移)。
    /// </summary>
    private void RecheckStaticBearing(ArtilleryTask task, LeftRight leftRight)
    {
        if (task.IsMoving || EntityLocator == null) return;
        var trackId = task.TrackEntityId is { Length: > 0 } ? task.TrackEntityId : task.entityId;
        if (string.IsNullOrEmpty(trackId)) return;
        if (!EntityLocator.TryGetMotion(trackId, out var livePos, out _)) return;
        var aimPos = task.ClusterOffset.sqrMagnitude > 0f ? livePos + task.ClusterOffset : livePos;
        var freshBearing = Bearing(aimPos);
        var freshDistance = DistKm(aimPos);
        var drift = Mathf.Abs(Mathf.DeltaAngle(task.angel, freshBearing));
        var distanceDrift = Mathf.Abs(task.distance - freshDistance);
        // 阵地移动可能只改变径向距离，方位几乎不动；距离变化同样必须触发重算。
        if (drift > 1f || distanceDrift > 0.05f)
        {
            MelonLogger.Warning($"[FCS] {leftRight} 基准复核: 方位漂移 {task.angel:F1}°→{freshBearing:F1}° " +
                                $"dist {task.distance:F2}→{freshDistance:F2}km, 更新重瞄(防打错方向)");
            task.angel = freshBearing;
            task.distance = freshDistance;
        }
    }

    /// <summary>
    /// 快照采纳(装填期与瞄准期调用):
    /// 1. 冷启动(一次性): 目标刚出现无速度, 从雷达最新扫描采纳位置+速度, 重新冻结快照。
    /// 2. 变速/停车(按需): 恒定匀速假设对"可被逼停"的目标失效(实测列车 0.008→0——轨道被毁停车),
    ///    雷达估速明显变化(>2m/s)时重冻结快照; 速度跌破阈值 → 退化为静态瞄准当前位置, 停止追幽灵提前点。
    /// 仍按冻结公式外推, 不连续跟踪——只在速度实质变化时重置。
    /// </summary>
    private void AdoptVelocityIfNeeded(ArtilleryTask task) {
        if (EntityLocator == null) return;
        // 跟踪对象: 集群任务看领队(车列编队同步动停), 单点任务就是自身
        var trackId = task.TrackEntityId is { Length: > 0 } ? task.TrackEntityId : task.entityId;
        if (task.VelocityUnknown) {
            if (trackId is { Length: > 0 } && EntityLocator.TryGetMotion(trackId, out var pos, out var vel)) {
                task.AimP0 = pos;
                task.AimVel = vel;
                task.AimStartTime = Time.time;
                task.VelocityUnknown = false;
                task.IsMoving = TargetLeadSolver.IsMoving(vel);
                MelonLogger.Msg($"[FCS] 已采纳 {trackId} 速度 {vel.magnitude * 3.8164f:F3}km/s, 快照重置");
            }
            return;
        }
        if (task.IsMoving && trackId is { Length: > 0 }
            && EntityLocator.TryGetMotion(trackId, out var livePos, out var liveVel)
            && (liveVel - task.AimVel).magnitude * ShellData.KmPerWorldUnit > 0.002f)
        {
            task.AimP0 = livePos;
            task.AimVel = liveVel;
            task.AimStartTime = Time.time;
            task.IsMoving = TargetLeadSolver.IsMoving(liveVel);
            if (!task.IsMoving) {
                // 停车 → 静态瞄准点: 集群任务 = 领队当前位置 + 集群偏移(保持车列中点, 落点不跳车头);
                // 单点任务 = 目标当前位置(不再追幽灵提前点)
                task.position = task.TrackEntityId is { Length: > 0 } && task.ClusterOffset.sqrMagnitude > 0f
                    ? livePos + task.ClusterOffset
                    : livePos;
                task.angel = Bearing(task.position);
                task.distance = DistKm(task.position);
            }
            MelonLogger.Msg($"[FCS] 变速采纳 {trackId}: v={liveVel.magnitude * 3.8164f:F3}km/s, 快照重置{(task.IsMoving ? "" : " (停车→静态, 落点改当前位置)")}");
        }
    }

    /// <summary>开火前对齐复检: 双机构对当前 aim(t) 都在容差内。</summary>
    private bool AlignOk(ArtilleryTask task, GunSystem gunSys, float tol) {
        TryComputeAimTargets(task, out var bearing, out var elev);
        return gunSys.ElevationError(elev) < tol && Turret.AngleError(bearing) < tol;
    }

    /// <summary>
    /// 装填期炮塔方位(后台预约协程用): 静态=任务方位; 移动=提前点方位(冻结快照外推)。
    /// 2026-08-16 用户要求: 方向机从装填期(含 WaitLoading)开始驱动——静态目标不再等到
    /// Aiming 才转, 装填与转向重叠, 装填完方位已就位(此前静态返回 false → 装填完才大角度追)。
    /// </summary>
    private bool TryGetTaskBearing(ArtilleryTask task, out float bearing) {
        AdoptVelocityIfNeeded(task);   // 装填期采纳: 炮塔尽早转向提前方位, 避免装填完再大角度追
        bearing = task.angel;
        if (task.IsMoving && TargetLeadSolver.IsMoving(task.AimVel))
        {
            var tof = ToFTable.FlightTime(task.distance, task.LoadedCharge);
            bearing = Bearing(TargetLeadSolver.LeadPoint(task.AimP0, task.AimVel,
                Time.time - task.AimStartTime, tof));
        }
        return true;
    }

    /// <summary>
    /// 集群收益分析: 对静态软目标 T, 求 HE/HCHE 最大可覆盖集群(MEC 圆心落点, 友军禁区)。
    /// 有集群(≥2) → 返回集群任务(位置提交, 打质心, 注册表按毁伤半径覆盖); 无 → null(调用方走单点)。
    /// 选择流程不动——只在"本应打 HE"的软目标上做升级。
    /// </summary>
    public ArtilleryTask? TryBuildClusterTask(TacticalDecider.TargetInfo ti, int targetId)
    {
        if (ti.IsArmored || ti.IsUnderground) return null;   // 只软目标(装甲/地下不走集群)
        if (EntityLocator == null) return null;

        // 候选 = 未处理软目标世界坐标(含已派发的覆盖区过滤)。
        // 移动集群(列车/车队): 编队刚体——成员与 T 同向同速才可同簇(集群几何随动不变),
        // 快照位置成簇, 任务带 IsMoving 快照, 现有提前量路径把整簇带到命中点。
        var candidates = new List<Vector3>();
        foreach (var o in EntityLocator.AliveHostiles)
        {
            if (o.EntityId == ti.EntityId) continue;
            if (o.IsArmored || o.IsUnderground) continue;
            if (o.IsMoving != ti.IsMoving) continue;
            if (o.IsMoving && (o.Velocity - ti.Velocity).magnitude * ShellData.KmPerWorldUnit > 0.002f) continue;
            if (Registry.IsHandled(o.EntityId)) continue;
            if (Registry.IsHandledNear(o.WorldPos, 0f)) continue;
            candidates.Add(o.WorldPos);
        }
        var friendlies = EntityLocator.AllyPositions;

        var he = ClusterSolver.Best(ti.WorldPos, candidates,
            ShellData.BlastRadiusKm(BulletType.HE), friendlies, ShellData.FriendlySafeRadiusKm(BulletType.HE));
        var hche = ClusterSolver.Best(ti.WorldPos, candidates,
            ShellData.BlastRadiusKm(BulletType.HCHE), friendlies, ShellData.FriendlySafeRadiusKm(BulletType.HCHE));

        // 集群门槛(用户 2026-08-13 定): HE ≥2、HCHE ≥4——集群打得勤快比省点更重要。
        // DRIL 基准纯成本是 HE 4/HCHE 6, 时间补偿降 1 是 3/5——再放宽到 2/4 图个热闹。
        bool heValid = he.HasValue && he.Value.Count >= 2;
        bool hcheValid = hche.HasValue && hche.Value.Count >= 4;
        // 性价比选弹: 每申请点覆盖目标数 = 覆盖数/成本(HE 10 点, HCHE 18 点)。
        // 交叉相乘免浮点: hche.Count×10 > he.Count×18 → HCHE 每点覆盖更多 → 升级。
        // 例: HE 3 vs HCHE 5 → 3×18=54 > 5×10=50 → HE 更划算(保持); HE 2 vs HCHE 5 → 2×18<5×10 → HCHE。
        bool hcheWorthIt = hcheValid && (!heValid
            || hche.Value.Count * ShellData.Cost(BulletType.HE) > he.Value.Count * ShellData.Cost(BulletType.HCHE));

        BulletType shell;
        Vector3 impact;
        int coverCount;
        if (heValid && !hcheWorthIt) { shell = BulletType.HE; impact = he.Value.Impact; coverCount = he.Value.Count; }
        else if (hcheValid) { shell = BulletType.HCHE; impact = hche.Value.Impact; coverCount = hche.Value.Count; }
        else return null;

        // 实测毁伤半径: 落点 1km 内所有存活目标距落点的径向距离 + 覆盖数。
        // 配合 Reconcile 击杀日志读出真实杀伤半径(维基 0.27/0.63 疑似偏大——覆盖成员没死)。
        // 探针: MEC 半径(he/hche) + 覆盖目标 WorldPos 原始坐标——对照游戏 UI 定位 20% 距离偏差。完成后移除。
        var nearby = EntityLocator.AliveHostiles
            .Where(h => (h.WorldPos - impact).magnitude * ShellData.KmPerWorldUnit < 1.0f)
            .Select(h => $"{h.EntityId}@{(h.WorldPos - impact).magnitude * ShellData.KmPerWorldUnit:F2}km");
        var candPos = candidates
            .Select(p => $"({p.x:F3},{p.y:F3},{p.z:F3})");
        MelonLogger.Msg($"[FCS] 集群 {ti.EntityId}: {shell} 落点{DistKm(impact):F2}km MECr={((shell == BulletType.HE ? he : hche).Value.RadiusKm):F2}km 覆盖{coverCount}个{(ti.IsMoving ? " [移动]" : "")} | 1km内: {string.Join(" ", nearby)} | 候选:{string.Join(" ", candPos)}");

        // 移动集群覆盖成员: 击发时按 entityId 登记——爆区几何以落点为中心, 车列在落点后方,
        // 在飞期间几何屏蔽拦不住, 需按实体屏蔽(死亡由 Reconcile 释放)。
        List<string>? members = null;
        if (ti.IsMoving)
        {
            float blastKm = ShellData.BlastRadiusKm(shell);
            members = new List<string>();
            foreach (var o in EntityLocator.AliveHostiles)
            {
                if (o.IsArmored || o.IsUnderground || !o.IsMoving) continue;
                if ((o.Velocity - ti.Velocity).magnitude * ShellData.KmPerWorldUnit > 0.002f) continue;
                if ((o.WorldPos - impact).magnitude * ShellData.KmPerWorldUnit <= blastKm)
                    members.Add(o.EntityId);
            }
        }

        return new ArtilleryTask
        {
            targetId = targetId,
            entityId = "",                                  // 位置提交(集群), 注册表按毁伤半径覆盖
            entityName = ti.Name,                           // 领队显示名(面板识别用)
            angel = Bearing(impact),
            distance = DistKm(impact),
            position = impact,
            bulletType = shell,
            useMaxCharge = false,                            // 纯反炮兵: 最低装药(满装唯一收益是缩短 ToF, 无竞速需求)
            Source = TaskSource.Auto,
            BlastRadiusKm = ShellData.BlastRadiusKm(shell),
            // 移动集群: 冻结快照带整簇(装填期采纳无必要——速度已知), 提前量路径击发前重算提前点。
            // TrackEntityId=领队: 车列剧情停车/变速时二次采纳(AdoptVelocityIfNeeded)跟踪对象。
            // ClusterOffset=落点相对领队: 停车采纳时落点跟领队平移, 保持车列中点。
            IsMoving = ti.IsMoving,
            AimP0 = impact,
            AimVel = ti.Velocity,
            AimStartTime = Time.time,
            VelocityUnknown = false,
            ClusterMembers = members,
            TrackEntityId = ti.EntityId,
            ClusterOffset = impact - ti.WorldPos,
        };
    }

    /// <summary>纯装饰: 装填期并行驱动一次游戏计算器（静态=正确数据, 移动=大致数据）。
    /// 不回传仰角, 不参与瞄准。desk 锁短持有, 与采购/另一门炮互斥。
    /// 守护: 计算器卡死(游戏 UI 异常)会永久持有 deskLock → 所有任务卡 LoadingPowder,
    /// 每步用手动驱动+8s 竞速超时, 超时放弃并释放锁(装饰失败不影响任务)。</summary>
    private IEnumerator SyncCalculatorVisual(ArtilleryTask task, int powderCount) {
        var visualLease = _deskLock.CreateLease();
        yield return _deskLock.Acquire(visualLease);
        try {
            yield return RaceTimeout(BallisticCalculator.SetDistance(task.distance), 8f, "SetDistance");
            yield return RaceTimeout(BallisticCalculator.SetDirection(task.angel), 8f, "SetDirection");
            yield return RaceTimeout(BallisticCalculator.SetCharge(powderCount), 8f, "SetCharge");
            yield return RaceTimeout(BallisticCalculator.SetShellType(task.bulletType), 8f, "SetShellType");
            yield return RaceTimeout(BallisticCalculator.Calculate(), 8f, "Calculate");
        } finally {
            _deskLock.Release(visualLease);
        }
    }

    /// <summary>手动驱动子协程 + 竞速超时: 子协程卡死(永不完成)时超时放弃, 不让调用方永久等待。
    /// 与 TaskRoutineGuard 同理——yield return 子协程若内部卡死, 外层检查永远不会执行。</summary>
    private IEnumerator RaceTimeout(IEnumerator inner, float timeout, string what) {
        float deadline = Time.time + timeout;
        try {
            while (Time.time < deadline) {
                bool moved;
                try
                {
                    moved = inner.MoveNext();
                }
                catch (Exception ex) { MelonLogger.Warning($"[FCS] 装饰协程 {what} 异常: {ex.GetType().Name}: {ex.Message}"); yield break; }
                if (!moved) yield break;
                yield return inner.Current;
            }
            MelonLogger.Warning($"[FCS] 装饰协程 {what} 超时放弃");
        }
        finally {
            try { (inner as System.IDisposable)?.Dispose(); } catch { }
        }
    }
}
