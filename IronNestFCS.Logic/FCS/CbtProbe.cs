using System;
using Il2Cpp;
using IronNestFCS.Logic;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// CbtProbe —— CBT 规则判据探针(纯观察者: 不改任何游戏状态, 只做只读采样 + 日志)。
///
/// 目的: 用一次实战跑出 regime 判据(2026-08-18 三层分析):
///   R1 多杀结算: 一发 HE/HCHE 集群击杀 N 门火炮 → 倒计时净 +30s×N(按杀) 还是 +30s×1(按发)?
///      积分净 +5×N 还是 +5×1?  (决定最优解 regime A 滚雪球 vs B 地板链)
///   R2 MoveZone 卡真效果: 购买后倒计时是回到 600(地板重置) 还是停在原值继续走(只暂停/无重置)?
///      两条既有记录互相矛盾(CbtMonitor 反编译注 vs FSC 08-15 实测注)。
///   附带: 30s 心跳时间轴 + 每个大事件附战场快照(存活 FDC/非FDC 数 + 两管任务), 对齐是哪一发/哪个目标。
///
/// 通道选择(为什么只有轮询, 没有事件订阅/方法挂钩):
///   - UnityEvent 订阅不可行: interop 的 UnityAction 只有 (object, IntPtr) 构造, managed 侧无法从
///     lambda 实例化(CS1660), 无法 AddListener。
///   - Harmony 钩方法不可行: interop stub 是 managed IL 外壳(调 il2cpp_runtime_invoke), 游戏自己的
///     节点图走 native→native 调用, 根本不经过 stub —— 钩子只能拦到 mod 自身的调用, 看不到游戏行为。
///   - 轮询净状态完全够: R1/R2 问的就是"一次事件后净变化是多少"(规则的实现与调用次数无关),
///     10Hz 采样 + 边缘检测即可分辨: 跳升(Δ>2s)、暂停冻结(值不再递减)、恢复首值(≈600=重置)。
///
/// 判读(日志 [PROBE] 前缀, 与 [FCS]/[Radar]/[CBT] 同流按时序对齐, 对照游戏内观察):
///   "TMR JUMP +X (a→b)"      倒计时跳升: 击杀集群后 X≈30N=按杀 / X≈30=按发; b≈600 且发生在移动时=重置
///   "RUN true→false 暂停"     暂停起点(值开始冻结)
///   "TMR RESUMED 首值 b"      暂停解除第一个采样: b≈600=地板重置坐实 / b≈暂停前值=只暂停坐实
///   "PTS Δ±k (a→b)"          积分变化: 同一次击中的积分净增(15 vs 5)
/// </summary>
public class CbtProbe
{
    private FSC? _fcs;
    private TacticalRadar? _radar;
    private CounterBatteryTimer? _timer;

    private float _lastFindTime;
    private float _lastSample;
    private float _lastRemain = -9999f;
    private bool _hasLastRemain;
    private bool _wasRunning;
    private bool _hasRunningState;
    private bool _wasExpired;
    private bool _wasPermStopped;
    private int _lastPts = -1;
    private bool _resumedPending;   // 刚恢复运行(轮询边沿): 下一采样 = 暂停解除首值
    private float _lastHeartbeat;

    private void Log(string msg) => MelonLogger.Msg($"[PROBE] t={Time.realtimeSinceStartup:F1} {msg}");

    public void Bind(FSC fcs, TacticalRadar? radar)
    {
        _fcs = fcs;
        _radar = radar;
    }

    public void Update()
    {
        // 1) 计时器引用(节点图动态 spawn, 首弹落地前不存在; 1Hz 重找)
        if (_timer == null && Time.time - _lastFindTime > 1f)
        {
            _lastFindTime = Time.time;
            try { _timer = Object.FindObjectOfType<CounterBatteryTimer>(); }
            catch { _timer = null; }
            if (_timer != null)
            {
                try
                {
                    float total;
                    try { total = _timer.totalDurationSeconds; } catch { total = -1f; }
                    Log($"FOUND CounterBatteryTimer (totalDuration={total:F0})");
                }
                catch (Exception ex) { Log($"found but threw: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        // fake-null(对象被 Destroy, 如换关卡) → 重置状态
        if (_timer == null && _hasLastRemain)
        {
            _hasLastRemain = false;
            _hasRunningState = false;
            _lastRemain = -9999f;
            Log("timer destroyed (scene/round change?)");
        }

        // 2) 10Hz 采样
        if (_timer == null) return;
        var fcs = _fcs;
        if (fcs == null || !fcs.IsBound) return;
        if (Time.time - _lastSample < 0.1f) return;
        _lastSample = Time.time;
        SampleOnce();
    }

    private void SampleOnce()
    {
        float? r = null;
        try { r = _timer.TimeRemaining; } catch { _timer = null; }
        if (r == null) return;
        float v = r.Value;

        if (_hasLastRemain)
        {
            if (_resumedPending)
            {
                _resumedPending = false;
                Log($"TMR RESUMED 首值 {v:F1} (R2判据: ≈600=地板重置 / ≈暂停前值=只暂停)");
            }
            // 正常运行每秒约 -1(采样 0.1s → Δ≈-0.1), Δ>2 必是 AddTime/SetTime(净加时/重置)
            float d = v - _lastRemain;
            if (d > 2f)
                Log($"TMR JUMP +{d:F1} ({_lastRemain:F1}→{v:F1}) {Ctx()}");
        }
        _lastRemain = v;
        _hasLastRemain = true;

        bool running;
        try { running = _timer.IsRunning; } catch { running = false; }
        if (_hasRunningState)
        {
            if (running && !_wasRunning)
            {
                _resumedPending = true;
                Log($"RUN false→true (恢复) remain={v:F1}");
            }
            else if (!running && _wasRunning)
                Log($"RUN true→false (暂停, 值开始冻结) remain={v:F1} {Ctx()}");
        }
        _wasRunning = running;
        _hasRunningState = true;

        bool expired, perm;
        try { expired = _timer.IsExpired; perm = _timer.IsPermanentlyStopped; }
        catch { expired = false; perm = false; }
        if (expired && !_wasExpired) Log($"EXPIRED=true (归零!) {Ctx()}");
        if (perm && !_wasPermStopped) Log($"PERMANENTLY-STOPPED=true (全灭?) {Ctx()}");
        _wasExpired = expired;
        _wasPermStopped = perm;

        // 积分(同 10Hz 采样): 同一次击中的多个 +5 落在同一 0.1s 窗口 → Δ 直接是净增(15 或 5)
        var fcs = _fcs;
        if (fcs != null)
        {
            int pts;
            try { pts = fcs.Cbt.RequisitionPoints; } catch { pts = -1; }
            if (pts >= 0)
            {
                if (_lastPts >= 0 && pts != _lastPts)
                    Log($"PTS Δ{pts - _lastPts:+#;-#} ({_lastPts}→{pts}) {Ctx()}");
                _lastPts = pts;
            }
        }

        // 30s 心跳(时间轴锚点, 对照游戏内: 敌方炮击何时开始收敛)
        if (Time.time - _lastHeartbeat > 30f)
        {
            _lastHeartbeat = Time.time;
            Log($"HEARTBEAT remain={v:F1} running={running.ToString().ToLower()} pts={_lastPts}");
        }
    }

    /// <summary>战场快照: 存活 FDC/非FDC 数(雷达最近一轮缓存, 最多滞后 ~5s) + 两管任务, 对齐击杀/加时来源。</summary>
    private string Ctx()
    {
        int fdc = -1, others = -1;
        try
        {
            var list = _radar?.AliveHostiles;
            if (list != null)
            {
                fdc = 0;
                others = 0;
                foreach (var t in list)
                {
                    if (t.IsFdc) fdc++; else others++;
                }
            }
        }
        catch { }

        string TaskTxt(ArtilleryTask? t) => t == null ? "-" : $"{t.entityName}/{t.bulletType}";
        var fcs = _fcs;
        return $"CTX[fdc={fdc} nonFdc={others} L={TaskTxt(fcs?.LeftTask)} R={TaskTxt(fcs?.RightTask)}]";
    }

    public void Dispose()
    {
        _fcs = null;
        _radar = null;
        _timer = null;
    }
}
