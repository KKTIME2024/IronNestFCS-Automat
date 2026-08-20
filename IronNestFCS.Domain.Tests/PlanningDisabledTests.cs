using System;
using System.IO;
using System.Linq;
using IronNestFCS.Domain.Planning;

namespace IronNestFCS.Domain.Tests;

/// <summary>PlanningDisabled(紧急停用)的最小自动化覆盖。
/// 边界说明: "停用后不创建自动任务"的完整行为发生在 FcsModule.OnGunIdle(Logic 程序集,
/// 依赖 Unity Time/FSC/雷达), 无法在纯 Domain 单测中直接实例化。本测试做两层可审查验证:
/// 1) 结构性: OnGunIdle 在手动 FIFO 检查之后、任何规划/入队之前短路返回
///    (不 Plan/不入队/不清队列/不取消任务), 且该分支先于 RunBaselineAuthority ——
///    由源码顺序断言保证"不会新建自动计划、不会影响人工 FIFO";
/// 2) 领域侧: BaselinePlanner 对"有合法目标+空闲炮管"的快照必然产出真实计划,
///    证明停用拦截点上游确实存在会被创建的自动计划(即停用开关拦的是真东西)。</summary>
internal static class PlanningDisabledTests
{
    public static void DisabledModeShortCircuitsBeforeAnyPlanning()
    {
        var logicDir = FindRepoRoot() + "/IronNestFCS.Logic";
        var module = File.ReadAllText(logicDir + "/FcsModule.cs");
        var rollout = File.ReadAllText(logicDir + "/Planning/PlannerRollout.cs");

        // 1) 模式枚举只有两个值, 默认 Baseline(热重载后 FcsModule 重新 new PlannerRollout → 仍 Baseline)
        AssertEx.Contains("enum PlannerAuthority { Baseline, PlanningDisabled }", rollout);
        AssertEx.Contains("Authority { get; private set; } = PlannerAuthority.Baseline;", rollout);

        // 2) OnGunIdle 内: 手动 FIFO 检查在前, 停用短路口在后, 规划路径在最后
        var onGunIdle = ExtractMethod(module, "private void OnGunIdle()");
        var manualCheck = onGunIdle.IndexOf("t.Source == TaskSource.Manual", StringComparison.Ordinal);
        var disabledCheck = onGunIdle.IndexOf("PlannerAuthority.PlanningDisabled", StringComparison.Ordinal);
        var planCall = onGunIdle.IndexOf("RunBaselineAuthority(", StringComparison.Ordinal);
        AssertEx.True(manualCheck >= 0, "manual FIFO check missing in OnGunIdle");
        AssertEx.True(disabledCheck > manualCheck, "disabled short-circuit must come after manual FIFO check");
        AssertEx.True(planCall > disabledCheck, "planning path must come after disabled short-circuit");

        // 3) 短路口本体: 只记日志并 return —— 无 Plan/EnqueueTask/ClearPendingTasks/DispatchPending
        var shortCircuit = SegmentBetween(onGunIdle, "PlannerAuthority.PlanningDisabled", "\n        }");
        AssertEx.Contains("return;", shortCircuit);
        AssertEx.DoesNotContain("_planner.Plan", shortCircuit);
        AssertEx.DoesNotContain("EnqueueTask", shortCircuit);
        AssertEx.DoesNotContain("ClearPendingTasks", shortCircuit);
        AssertEx.DoesNotContain("Canceled =", shortCircuit);

        // 4) 旧决策链不得复活(删除后的静态守卫)
        AssertEx.DoesNotContain("PickTarget", module);
        AssertEx.DoesNotContain("CreateAutoTask", module);
        AssertEx.DoesNotContain("LegacyDecisionRecorder", module);
        AssertEx.DoesNotContain("PlannerAuthority.Legacy", module);
        AssertEx.DoesNotContain("TryBuildClusterTask", File.ReadAllText(logicDir + "/FSC.cs"));
    }

    public static void PlannerProducesRealPlanThatDisabledModeWithholds()
    {
        // 停用拦截点上游必须存在真实可规划的自动计划, 否则"停用"无从谈起。
        var snapshot = SnapshotBuilder.Valid(targets: new[] { SnapshotBuilder.Target("gun", priority: 5) });
        var plan = new BaselinePlanner().Plan(snapshot);
        AssertEx.True(plan.HasFire);
        AssertEx.Equal(NoPlanReason.None, plan.NoPlanReason);
        AssertEx.Equal(1, plan.Groups.Count);
    }

    /// <summary>从源码提取方法体(按大括号配平), 用于结构性断言。</summary>
    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) throw new Exception($"signature not found: {signature}");
        var bodyStart = source.IndexOf('{', start);
        if (bodyStart < 0) throw new Exception("no body for " + signature);
        var depth = 0;
        for (var i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(start, i - start + 1);
            }
        }
        throw new Exception("unbalanced braces for " + signature);
    }

    private static string SegmentBetween(string text, string fromMarker, string toMarker)
    {
        var from = text.IndexOf(fromMarker, StringComparison.Ordinal);
        if (from < 0) throw new Exception("marker not found: " + fromMarker);
        var to = text.IndexOf(toMarker, from, StringComparison.Ordinal);
        if (to < 0) throw new Exception("end marker not found: " + toMarker);
        return text.Substring(from, to - from + toMarker.Length);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(dir.FullName + "/IronNestFCS.sln"))
            dir = dir.Parent;
        if (dir == null) throw new Exception("repo root with IronNestFCS.sln not found");
        return dir.FullName;
    }
}
