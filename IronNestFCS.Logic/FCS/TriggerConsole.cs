using System;
using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class TriggerConsole {
    private LookAtTarget? _taskCheck;
    private LookAtTarget? _bulletCheck;
    private LookAtTarget? _rotationCheck;
    private LookAtTarget? _elevationCheck;
    private LookAtTarget? _readyFire;
    private LookAtTarget? _armLeft;
    private LookAtTarget? _armRight;
    private SliderEnergyMomentumSpinner? _fire;

    /// <summary>快速确认点击节奏(秒)。开关是纯 0/1 flag, 一口气连点即可。
    /// 若游戏点击识别需要更长按下时间, 构建机实测后调大。</summary>
    private const float FastConfirmTapSeconds = 0.01f;

    public bool TryBind() {
        var console = GameObject.Find(".Review Console Parent").transform;
        var buttons = new List<LookAtTarget>();
        
        for (var i = 0; i < console.childCount; ++i) {
            var child = console.GetChild(i);
            if (child.name.StartsWith(".Check Switch")) {
                buttons.Add(child.GetComponentInChildren<LookAtTarget>());
            }
        }

        if (buttons.Count != 5) {
            MelonLogger.Error("Can't bind trigger console.");
        }
        _taskCheck = buttons[0];
        _bulletCheck = buttons[1];
        _rotationCheck = buttons[2];
        _elevationCheck = buttons[3];
        _readyFire = buttons[4];
        _armLeft = GameObject.Find(".ArmingLeverParent Left").GetComponentInChildren<LookAtTarget>();
        _armRight = GameObject.Find(".ArmingLeverParent Right").GetComponentInChildren<LookAtTarget>();
        _fire = GameObject.Find(".Trigger Core").transform.FindChild(".Generator Spinner")
            .GetComponentInChildren<SliderEnergyMomentumSpinner>();
        ProbeConsoleState(buttons);
        return true;
    }

    /// <summary>
    /// 一次性诊断探针(定位拒发后删除): 确认开关/臂杆/击发旋钮是翻转式(点击=翻转), 无状态可读 →
    /// 中止残留"已武装"会使下次武装翻回"未武装" → 游戏拒发。枚举组件与字段, 找可读的开关状态。
    /// </summary>
    private static void ProbeConsoleState(List<LookAtTarget> buttons) {
        try {
            for (var i = 0; i < buttons.Count; i++) {
                var go = buttons[i] == null ? null : buttons[i].gameObject;
                MelonLogger.Msg($"[FCS][探针] 确认开关 {i} ({go?.name ?? "null"}):");
                DumpComponents(go);
            }
            var armLeft = GameObject.Find(".ArmingLeverParent Left")?.GetComponentInChildren<LookAtTarget>();
            MelonLogger.Msg($"[FCS][探针] 左臂杆 ({armLeft?.name ?? "null"}):");
            DumpComponents(armLeft?.gameObject);
            var spin = GameObject.Find(".Trigger Core")?.transform.FindChild(".Generator Spinner");
            MelonLogger.Msg($"[FCS][探针] 击发旋钮 ({spin?.name ?? "null"}):");
            DumpComponents(spin?.gameObject);
        }
        catch (Exception ex) {
            MelonLogger.Error($"[FCS][探针] 失败: {ex.Message}");
        }
    }

    private static void DumpComponents(GameObject? go) {
        if (go == null) return;
        foreach (var comp in go.GetComponents<Component>()) {
            MelonLogger.Msg($"[FCS][探针]   组件 {comp.GetType().FullName}");
            foreach (var f in comp.GetType().GetFields(
                         System.Reflection.BindingFlags.Instance
                         | System.Reflection.BindingFlags.Public
                         | System.Reflection.BindingFlags.NonPublic)) {
                MelonLogger.Msg($"[FCS][探针]     字段 {f.FieldType.Name} {f.Name}");
            }
        }
    }

    public void Fire() {
        _fire?.AddEnergy(255);
    }

    /// <summary>臂杆按下与 5 个快速确认并行(开关是纯 0/1 flag, 无值语义, 顺序无关)。
    /// 确认在臂杆保持期内一口气点完, 总时长不变(~1.2s), 但确认不再占独立时序。</summary>
    public IEnumerator ArmWithFastConfirm(LeftRight leftRight) {
        var arm = leftRight == LeftRight.Left ? _armLeft : _armRight;
        arm?.OnClickDown();
        var holdStart = Time.realtimeSinceStartup;
        yield return ConfirmAllFast();
        var held = Time.realtimeSinceStartup - holdStart;
        if (held < 0.2f) yield return new WaitForSeconds(0.2f - held);
        arm?.OnClickUp();
        yield return new WaitForSeconds(1f);
    }

    /// <summary>5 个确认开关一口气点完(任务/弹种/方位/仰角/就绪)。重复点击幂等。</summary>
    private IEnumerator ConfirmAllFast() {
        var switches = new[] { _taskCheck, _bulletCheck, _rotationCheck, _elevationCheck, _readyFire };
        foreach (var s in switches) {
            s?.OnClickDown();
            yield return new WaitForSeconds(FastConfirmTapSeconds);
            s?.OnClickUp();
            yield return new WaitForSeconds(FastConfirmTapSeconds);
        }
    }

    /// <summary>
    /// 复位武装: 臂杆与 5 个确认开关各点一次回到中立态。任务武装后未击发即中止
    /// (看门狗取消/游戏拒发放弃)时调用——开关是 0/1 翻转式, 残留"已武装"会让下一次
    /// ArmWithFastConfirm 把开关翻回"未武装", 游戏拒发(2026-08-16 实测: 拒一次、第三次才成功)。
    /// </summary>
    public IEnumerator Unarm(LeftRight leftRight) {
        var arm = leftRight == LeftRight.Left ? _armLeft : _armRight;
        arm?.OnClickDown();
        yield return new WaitForSeconds(0.1f);
        arm?.OnClickUp();
        yield return ConfirmAllFast();
        yield return new WaitForSeconds(0.5f);
    }
}