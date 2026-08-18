using Il2Cpp;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// CBT(无尽反炮兵)模式状态机 —— docs/cbt-mode.md §3.0/§6 的参数化实现。
///
/// 反编译+运行时探针实证(2026-08-15 反编译; 2026-08-18 CbtProbe 实战日志钉死 R1/R2):
///   CounterBatteryTimer 是场景单例 MonoBehaviour(节点图 State_StartTimer 动态生成):
///     - TimeRemaining(属性): 剩余秒数, 按游戏秒 -1; **CBT 实测 timeScale=4**(游戏600s≈真实150s)
///     - IsRunning: 第一发炮弹落地后 StartTimer → true; 击杀 FDC / 紧急移动 → PauseTimer → false
///     - 击杀火炮 +30s(=AddTime 净效果) + 征用点+5: **按门计, 14 样本全部精确 +30/+5**(R1 实测)
    ///     - **紧急移动 = 冻结, 我方落地后按原值恢复, 不重置(2026-08-18 实测: 冻 232.8→恢复 232.5)**;
    ///       暂停无时限(不开炮就挂着), 解冻事件驱动(用户钉死 2026-08-18; 实测一次 ~278s 自然解冻, 触发未定);
///       旧注 "SetTime(600) 重置" 已被运行日志推翻 —— 别按地板重置建模
///     - 倒计时**无 600s 上限**(实测加时后 656.6); 归零后 ~72 游戏秒 PERMANENTLY-STOPPED 结束回合
///     - IsExpired / IsPermanentlyStopped: 归零/永久停止
///   FireMission.RunningTimers 的 TimerValue(InitialSeconds=36000=10h, Current 每秒+1)
///   是通用计时器, 与 CBT 600s 无关 —— 不要读它。
///
/// 模式识别(两级, §3.0): 模式级 = 雷达扫到 FDC(icon 含 "fire direction", 由雷达注入 HasFdc);
/// 状态级 = CounterBatteryTimer 存在且倒计时走动。
/// 无 FDC → 现有规则原样运行(剧情反炮兵关/轻松模式不特化)。
/// </summary>
public class CbtMonitor
{
    // §6 策略参数(实战校准)
    public const float InitialTime = 600f;      // CBT 初始倒计时(实测 total≈600.9)
    public const float UrgentThreshold = 360f;  // 吃紧阈值: 低于此值切满装+火炮最优先(2026-08-16 用户: 180→360 翻倍)
    public const float CriticalThreshold = 240f; // 危急阈值: 低于此值触发紧急移动(2026-08-16 用户: 180→240)
    public const float EmergencyMoveCost = 65f; // 紧急移动卡价格
    public const float ReserveAfterMove = 25f;  // 移动后重启资金
    public const float FundLine = 90f;          // 应急基金线 = 65 + 25

    /// <summary>测试档位: 拉高决策阈值, 无需等倒计时自然降到 360/240s 即可观察吃紧/危急行为。
    /// Numpad8 循环切换(生产 → 快速 → 快速+禁紧急移动)。快速档: 600 开局切档后几乎立即
    /// 进入 Urgent/Critical(加时会把倒计时撑在 600+, 纯阈值追不上, 配合 Numpad7 手动触发)。</summary>
    public enum TestMode
    {
        Production,      // 360/240s, 紧急移动启用
        Fast,            // 595/580s, 紧急移动启用
        FastNoMove,      // 595/580s, 紧急移动禁用(只观察 Critical 阶段判定)
        Baseline,        // 2026-08-18 用户 baseline: 去光枷锁——开局/暂停双管对齐落地,
                         // 之后一律 max charge + DRIL 狂射火炮; 无 FDC/无自动移动/无基金线/无集群
    }

    /// <summary>当前测试档位(默认 Baseline——2026-08-18 用户指定先跑 baseline, 比它差的回滚)。</summary>
    public TestMode Mode { get; private set; } = TestMode.Baseline;

    /// <summary>吃紧阈值(测试档位拉高, 切档后立即进入 Urgent 观察双轨)。</summary>
    public float Urgent => Mode == TestMode.Production ? UrgentThreshold : 595f;

    /// <summary>危急阈值(测试档位拉高, 切档后立即进入 Critical 观察紧急移动)。</summary>
    public float Critical => Mode == TestMode.Production ? CriticalThreshold : 580f;

    /// <summary>Numpad8: 循环切换测试档位。</summary>
    public void CycleTestMode()
    {
        Mode = Mode switch
        {
            TestMode.Production => TestMode.Fast,
            TestMode.Fast => TestMode.FastNoMove,
            TestMode.FastNoMove => TestMode.Baseline,
            _ => TestMode.Production
        };
        MelonLogger.Msg($"[CBT] 测试档位: {Mode} (urgent<{Urgent:F0}s critical<{Critical:F0}s emergencyMove={Mode != TestMode.FastNoMove && Mode != TestMode.Baseline})");
    }

    public enum CbtPhase
    {
        None,      // 无计时器(剧情关/普通关/开局未生成) → 现有规则
        Opening,   // 计时器存在, 未启动(第一发未落地): 打最近火炮/3★, FDC 扣留
        Wide,      // 宽裕期 ≥360s: 3★ 印钞 + 火炮循环, 最低装药, FDC 扣留
        Urgent,    // 吃紧期 <360s: 火炮最优先 + 满装抢节奏 + FDC 组合拳
        Critical,  // 危急 <240s: 满装 + FDC 组合拳 + 紧急移动兜底(买暂停窗, 不重置)
        Paused,    // 暂停(FDC 击杀/MoveZone 卡): 倒计时冻结, 炮弹飞行不烧时间。
                   // 与 Opening 的区别: 已启动过(_everStarted)。
                   // 规则(2026-08-18 用户钉死): 暂停**没有自己的时限**——不开炮就挂在冻结值
                   // (如 232s)几小时不变; 解冻是事件驱动的: 我方炮弹落地。
                   // 落地即解冻 = 暂停中击杀照常 +30(无白赚), 该炮落地处就是解冻点。
                   // 用法 = 免费装填期: 两管都装好, 双管就绪后先后开炮、对齐同时落地——
                   // 与开局/宽裕一视同仁, 没有"扣发等窗尽"一说。
                   // FDC 击杀 = 结此暂停+起新暂停(续暂停链, 2026-08-16 用户确认)。
    }

    private CounterBatteryTimer? _timer;
    private MissionStatsTracker? _tracker;
    private float _lastPollTime;
    private float _lastFindTime;      // 计时器未找到时 5s 降频搜索
    private float _lastRemaining = -1f;
    private CbtPhase _lastPhase = CbtPhase.None;
    private bool _modeLocked;     // 本局已识别为无尽反炮兵模式(FDC 指纹出现过), 锁定不回退
    private bool _everStarted;    // 倒计时曾启动过(第一发落地或 StartTimer): 区分 Opening 与暂停窗口

    /// <summary>模式级指纹: 雷达本轮扫到存活 FDC(icon 含 fire direction)。由雷达每轮 Scan 注入。
    /// 置 true 时立即锁定本局模式(不等监视器下一 tick)——雷达 Scan 先于 CbtMonitor.Update 完成,
    /// 若锁模式滞后, 开局首轮派发会按原始优先级先打近火炮(2026-08-15 用户: 开局先打远的)。</summary>
    private bool _hasFdc;
    public bool HasFdc
    {
        get => _hasFdc;
        set { _hasFdc = value; if (value) _modeLocked = true; }
    }

    /// <summary>无尽反炮兵模式激活 = 模式级指纹(FDC 出现过)。§3.0 模式级是唯一识别——
    /// 状态级(计时器)只决定阶段。FDC 指纹首次出现即锁定本局(不因 FDC 全灭回退,
    /// 危急时刻正是 FDC 死光之后)。开局计时器未生成也算(否则开局 FDC 优先级 6 会被打)。</summary>
    public bool IsCbtMode => _modeLocked;

    /// <summary>当前阶段(未激活时为 None)。</summary>
    public CbtPhase Phase { get; private set; } = CbtPhase.None;

    /// <summary>供排序/派发用的生效阶段: 指纹已锁定 CBT 模式但监视器尚未 tick(开局首轮扫描,
    /// Phase 还停在 None)时按 Opening 处理——否则首轮排序退回原始优先级, 近火炮先被派发。
    /// 2026-08-16 保险: 非 CBT 模式强制 None——即使剧情关存在 running 的 CounterBatteryTimer
    /// (阶段机误判), 排序也绝不进入 CBT 双轨(其他关卡不翻车)。</summary>
    public CbtPhase EffectivePhase => !IsCbtMode
        ? CbtPhase.None
        : Phase != CbtPhase.None ? Phase : CbtPhase.Opening;

    /// <summary>排序用阶段: 直接取生效阶段(2026-08-16: 移除测试档升 Urgent 的覆盖——
    /// 它把 Opening 升成 Urgent 会破坏开局 farFirst(同优先级距离大优先, 先打远的白嫖),
    /// 实测 FastNoMove 档开局 Left 拿 2.68km 近炮; 且 FDC 只在 Critical/Paused 放行后,
    /// 测试档排序提前 FDC 已无意义)。</summary>
    public CbtPhase SortPhase => EffectivePhase;

    /// <summary>剩余秒数(计时器不存在/未读到时 -1)。</summary>
    public float TimeRemaining => _timer != null ? _timer.TimeRemaining : -1f;

    /// <summary>倒计时是否在走(第一发落地后 true, FDC 击杀暂停时 false)。</summary>
    public bool IsRunning => _timer != null && _timer.IsRunning;

    /// <summary>是否处于"暂停窗口"(2026-08-15 策略): 倒计时已启动过(非开局)但当前停走。
    /// FDC 击杀 / MoveZone 卡都会暂停到下一发落地。暂停期间炮弹飞行不烧倒计时——
    /// 应最大化准备(装填双管)并控制开炮节奏, 让第一发落地正好是暂停解除点。
    /// 2026-08-16 修正: 用 _everStarted 而非 remain<599——暂停中击杀火炮 AddTime(+30) 会把
    /// remain 被 +30 推进过 ≥600(无上限, 实测 656.6), 旧判定会误判成"未启动" → 暂停协调/扣留全部失效。</summary>
    public bool IsPausedWindow
    {
        get
        {
            if (_timer == null || !IsCbtMode) return false;
            return !IsRunning && _everStarted;
        }
    }

    /// <summary>倒计时是否归零(游戏即将失败)。</summary>
    public bool IsExpired => _timer != null && _timer.IsExpired;

    /// <summary>征用点余额(MissionStatsTracker 全局单例)。读取失败返回 -1。</summary>
    public int RequisitionPoints
    {
        get
        {
            try
            {
                if (_tracker == null) _tracker = Object.FindObjectOfType<MissionStatsTracker>();
                return _tracker != null ? _tracker.RequisitionPoints : -1;
            }
            catch { return -1; }
        }
    }

    /// <summary>每帧调用(轻量, 内部 0.5s 节流)。驱动阶段机。热重载/场景切换后引用失效自动重找。
    /// 计时器搜索节流: 未找到时(剧情关无 CBT/开局未生成)每 5s 找一次, 避免 0.5s 一次全场景搜索。</summary>
    public void Update()
    {
        if (Time.time - _lastPollTime < 0.5f) return;
        _lastPollTime = Time.time;

        // 模式锁定: FDC 指纹首次出现即锁(本局不回退)
        if (HasFdc) _modeLocked = true;

        // 计时器引用缓存。Unity fake-null: 对象被 Destroy 后 _timer == null 成立 → 自动重找。
        // 未找到时降频到 5s(全场景 FindObjectOfType 是 O(n), 0.5s 一次在无 CBT 场景持续空转)。
        if (_timer == null && Time.time - _lastFindTime > 5f)
        {
            _lastFindTime = Time.time;
            _timer = Object.FindObjectOfType<CounterBatteryTimer>();
        }

        // 计时器未生成(第一发落地前节点图才 spawn, 实测开局 ~100s 内不存在):
        // CBT 模式已由 FDC 指纹锁定 → 按 Opening 处理(FDC 扣留 + 最低装药);
        // 非 CBT 模式(剧情关/普通关) → None(现有规则)。
        if (_timer == null)
        {
            Phase = IsCbtMode ? CbtPhase.Opening : CbtPhase.None;
            return;
        }

        float remain;
        bool running;
        try
        {
            remain = _timer.TimeRemaining;
            running = _timer.IsRunning;
        }
        catch
        {
            // 原生对象已销毁但 wrapper 未空 → 强制重找
            _timer = null;
            Phase = CbtPhase.None;
            return;
        }

        // "曾启动过"记忆(2026-08-16): 见过 running=true 或 remain < 600 即置位。
        // 用于区分 Opening(从未启动) 与 暂停窗口(启动过, 即使暂停中 AddTime 把 remain
        // 弹回 ≥600)——旧判定只看 remain 会被暂停中 +30s 骗成 Opening。
        if (running || remain < InitialTime - 1f) _everStarted = true;

        // 状态级 = 倒计时走动。开局未启动且剩余≈600 → Opening。
        // 暂停(FDC 击杀/MoveZone, 启动过且停走) → Paused: 无时限, 事件驱动解冻(我方落地)。
        // 与开局/宽裕期一视同仁: 两管都装好, 双管就绪先后开炮对齐同时落地;
        // 暂停只是把这段工时变成免费, 落地即解冻并照常 +30/门。
        // 2026-08-16 保险: running 分支也带 IsCbtMode——非 CBT 关(剧情)即使有 timer 走动
        // 也强制 None(其他关卡不翻车, 阶段机只服务 CBT 模式)。
        if (!running && remain >= InitialTime - 1f && !_everStarted)
        {
            Phase = IsCbtMode ? CbtPhase.Opening : CbtPhase.None;
        }
        else if (!running && _everStarted)
        {
            Phase = IsCbtMode ? CbtPhase.Paused : CbtPhase.None;
        }
        else if (!IsCbtMode)
        {
            Phase = CbtPhase.None;
        }
        else if (remain <= Critical)
        {
            Phase = CbtPhase.Critical;
        }
        else if (remain <= Urgent)
        {
            Phase = CbtPhase.Urgent;
        }
        else
        {
            Phase = CbtPhase.Wide;
        }

        // 阶段/大跳变日志(30s 以上跳变 = 加时/重置, 值得记录)
        if (_lastPhase != Phase || _lastRemaining < 0 || Mathf.Abs(_lastRemaining - remain) > 30f)
        {
            MelonLogger.Msg($"[CBT] phase={Phase} remain={remain:F1} running={running} fdc={HasFdc} pts={RequisitionPoints}");
            _lastPhase = Phase;
        }
        _lastRemaining = remain;
    }

    /// <summary>双轨装药(§3.2): 吃紧/危急(阈值内) → 满装 6 包抢节奏; 宽裕/开局 → 最低装药攒钱。
    /// Baseline 档: 每发按"够到本发距离的最低装药"(TargetLeadSolver.ChargeFor, 上限 6 包)定——
    /// 2026-08-18 用户原设"一律满装", 当日局 #21(5.57km)/#14(4.49km) 满装平弹道两管 0/2
    /// (仰角公式按"装药=0.2×距离"配对标定于 36–60°, 近距强 6 包低角外推超射打空)
    /// → "下一发该打什么"的装药参数由本发距离决定。非 Baseline/非 CBT 走原规则。</summary>
    public bool ShouldUseMaxCharge()
    {
        if (!IsCbtMode) return false;
        if (Mode == TestMode.Baseline) return false;
        return Phase is CbtPhase.Urgent or CbtPhase.Critical;
    }

    /// <summary>FDC 是否可派(§3.3/§3.4): 非危急(阈值外)绝不打——开局打=浪费, 宽裕期打=浪费暂停储备。
    /// 吃紧/危急才放行作暂停组合拳。暂停窗口(Paused)也放行——窗口内击杀 FDC = 结窗+起新窗
    /// (净效果续暂停; 2026-08-18 用户规则下"落地解窗"对 FDC 同样适用, 新旧语义在此自洽)。
    /// 测试档(Fast/FastNoMove)直接放行(2026-08-16): 生产档 360 阈值下倒计时必然降到吃紧;
    /// Fast 档 595 阈值 + 火炮击杀 +30s 会把 remain 撑回 600+ → 永远 Wide → FDC 扣留看不到
    /// 发射——测试档绕开扣留直接观察 FDC 行为(排序仍垫底, 其他目标优先)。</summary>
    public bool FdcDispatchable()
    {
        if (!IsCbtMode) return false;
        if (Mode == TestMode.Baseline) return false;   // baseline 不打 FDC(用户 2026-08-18)
        // 只在危急/暂停窗口放行(2026-08-16 用户): FDC 是危急专属储备——吃紧期打火炮 +30s
        // 维持倒计时更实在, FDC 留危急当免费暂停(生产 <240s / Fast <580s)。
        // 暂停窗口放行 = 危急的延续(窗口内 FDC 续暂停, 用户权衡后加回)。测试档同规则。
        return Phase is CbtPhase.Critical or CbtPhase.Paused;
    }

    /// <summary>紧急移动触发条件(§3.5): 危急(阈值内) 且积分 ≥ 65+25。
    /// 测试档 FastNoMove 禁用(只观察 Critical 阶段判定, 不真花钱); Baseline 禁用(用户 2026-08-18)。</summary>
    public bool ShouldEmergencyMove()
    {
        return Mode != TestMode.FastNoMove && Mode != TestMode.Baseline
            && IsCbtMode && Phase == CbtPhase.Critical && RequisitionPoints >= FundLine;
    }

    /// <summary>基金纪律(§3.6): 跌破基金线(90) → 停高价采购。余量 = 积分-基金线, 负数表示跌破。</summary>
    public float FundMargin => RequisitionPoints - FundLine;
}
