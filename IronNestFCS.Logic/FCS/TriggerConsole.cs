using System.Collections;
using Il2Cpp;
using Il2CppZagreekie.Tools;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class TriggerConsole {
    private const float ReviewOnZ = 0f;
    private const float ReviewOffZ = -90f;
    private const float ReviewPoseTolerance = 10f;
    private const float ArmTransitionTimeoutSeconds = 2f;

    private LookAtTarget? _taskCheck;
    private LookAtTarget? _bulletCheck;
    private LookAtTarget? _rotationCheck;
    private LookAtTarget? _elevationCheck;
    private LookAtTarget? _readyFire;
    private LookAtTarget? _armLeft;
    private LookAtTarget? _armRight;
    private Transform? _taskPose;
    private Transform? _bulletPose;
    private Transform? _rotationPose;
    private Transform? _elevationPose;
    private Transform? _readyPose;
    private Transform? _armLeftPose;
    private Transform? _armRightPose;
    private SliderEnergyMomentumSpinner? _fire;
    private ArmedFireRelayOneShot? _armRelay;

    // 全局扳机没有炮管参数；这两个值是本进程最近一次成功武装的所有权记录。
    // 物理姿态仍是最终事实，所有权只用于把“已武装的是谁”与当前任务代次关联起来。
    private long _armOwnerLeft;
    private long _armOwnerRight;

    public bool LastArmConfirmed { get; private set; }
    public bool LastDisarmConfirmed { get; private set; }
    public bool LastReviewConfirmed { get; private set; }
    /// <summary>武装动作中曾经可能改变实体状态；调用方必须先走解除武装交接。</summary>
    public bool ArmMayBeOn { get; private set; }
    public bool RecoveryPending { get; private set; }

    public bool TryBind() {
        var console = GameObject.Find(".Review Console Parent").transform;
        var controls = new List<(LookAtTarget button, Transform pose)>();
        
        for (var i = 0; i < console.childCount; ++i) {
            var child = console.GetChild(i);
            if (child.name.StartsWith(".Check Switch")) {
                var button = child.GetComponentInChildren<LookAtTarget>();
                var pose = FindReviewPose(child);
                if (button != null && pose != null)
                    controls.Add((button, pose));
            }
        }

        if (controls.Count != 5) {
            MelonLogger.Error($"[FCS] Can't bind trigger console: expected 5 review switches with physical poses, found {controls.Count}.");
            return false;
        }
        _taskCheck = controls[0].button;
        _taskPose = controls[0].pose;
        _bulletCheck = controls[1].button;
        _bulletPose = controls[1].pose;
        _rotationCheck = controls[2].button;
        _rotationPose = controls[2].pose;
        _elevationCheck = controls[3].button;
        _elevationPose = controls[3].pose;
        _readyFire = controls[4].button;
        _readyPose = controls[4].pose;
        var armLeftObject = GameObject.Find(".ArmingLeverParent Left");
        var armRightObject = GameObject.Find(".ArmingLeverParent Right");
        var triggerCore = GameObject.Find(".Trigger Core");
        _armLeftPose = armLeftObject?.transform;
        _armRightPose = armRightObject?.transform;
        _armLeft = armLeftObject?.GetComponentInChildren<LookAtTarget>();
        _armRight = armRightObject?.GetComponentInChildren<LookAtTarget>();
        _fire = triggerCore?.transform.FindChild(".Generator Spinner")
            ?.GetComponentInChildren<SliderEnergyMomentumSpinner>();
        var relays = UnityEngine.Object.FindObjectsOfType<ArmedFireRelayOneShot>();
        _armRelay = relays.Length == 1 ? relays[0] : null;
        var ok = _armLeft != null && _armRight != null
                 && _armLeftPose != null && _armRightPose != null && _fire != null && _armRelay != null;
        if (!ok)
            MelonLogger.Error($"[FCS] Can't bind trigger console arming controls, fire spinner or unique relay (relays={relays.Length}).");
        _armOwnerLeft = 0;
        _armOwnerRight = 0;
        LastArmConfirmed = false;
        LastDisarmConfirmed = false;
        LastReviewConfirmed = false;
        ArmMayBeOn = false;
        RecoveryPending = true;
        return ok;
    }

    private static Transform? FindReviewPose(Transform root) {
        foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            if (transform != null && transform != root && transform.name.StartsWith("knob_25_003"))
                return transform;
        return null;
    }

    /// <summary>
    /// 在全局扳机动作前核对两管的物理武装状态和任务代次。
    /// 未授权的另一管处于武装、或任一姿态无法读取时，拒绝发射并保持安全。
    /// </summary>
    public bool TryFire(LeftRight ownerSide, long ownerLeaseId,
        Func<bool> isAuthorized, Action onSubmitted)
    {
        if (_fire == null || RecoveryPending || ownerLeaseId == 0) return false;
        var other = ownerSide == LeftRight.Left ? LeftRight.Right : LeftRight.Left;
        var ownerKnown = TryReadArmState(ownerSide, out var ownerArmed, out _);
        var otherKnown = TryReadArmState(other, out var otherArmed, out _);
        return SingleShotGate.TrySubmit(ownerKnown && ownerArmed
                && GetArmOwner(ownerSide) == ownerLeaseId, otherKnown, otherArmed,
            isAuthorized, onSubmitted, () => _fire.AddEnergy(255));
    }

    /// <summary>先把五个确认开关逐一置为 ON 并回读，再执行武装动作。</summary>
    public IEnumerator ArmWithFastConfirm(LeftRight leftRight, long ownerLeaseId = 0) {
        LastArmConfirmed = false;
        LastReviewConfirmed = false;
        ArmMayBeOn = false;
        if (RecoveryPending) {
            MelonLogger.Warning($"[FCS] TriggerConsole: recovery is still pending; refusing to arm {leftRight}.");
            yield break;
        }
        var arm = leftRight == LeftRight.Left ? _armLeft : _armRight;
        var pose = leftRight == LeftRight.Left ? _armLeftPose : _armRightPose;
        if (arm == null || pose == null || ownerLeaseId == 0) {
            MelonLogger.Error($"[FCS] TriggerConsole: cannot arm {leftRight}; missing control, pose, or lease.");
            yield break;
        }

        if (!TryReadArmState(leftRight, out var currentlyArmed, out var angle)) {
            ArmMayBeOn = true;
            MelonLogger.Error($"[FCS] TriggerConsole: cannot read {leftRight} arm relay; refusing to arm.");
            yield break;
        }

        var existingOwner = GetArmOwner(leftRight);
        if (currentlyArmed && existingOwner != ownerLeaseId) {
            // 同一炮位的新代次可以收回自己留下的旧武装；没有本地所有权的物理 ON
            // 可能是玩家操作，不能盲目切换。
            if (existingOwner == 0) {
                MelonLogger.Error($"[FCS] TriggerConsole: {leftRight} arm is physically ON with unknown owner; refusing takeover.");
                yield break;
            }
            yield return Disarm(leftRight, existingOwner);
            if (!LastDisarmConfirmed) yield break;
            currentlyArmed = false;
        }

        if (currentlyArmed) {
            ArmMayBeOn = true;
        }

        yield return ConfirmAllPhysical();
        LastReviewConfirmed = AreReviewChecksConfirmed();
        if (!LastReviewConfirmed) {
            MelonLogger.Error($"[FCS] TriggerConsole: review switch poses not confirmed for {leftRight}; refusing to arm.");
            if (ArmMayBeOn) yield return Disarm(leftRight, ownerLeaseId);
            yield break;
        }

        if (!currentlyArmed) {
            SetArmOwner(leftRight, ownerLeaseId);
            ArmMayBeOn = true;
            try
            {
                arm.OnClickDown();
                yield return new WaitForSeconds(0.2f);
            }
            finally { arm.OnClickUp(); }
        }

        var deadline = Time.realtimeSinceStartup + ArmTransitionTimeoutSeconds;
        while (Time.realtimeSinceStartup < deadline) {
            if (TryReadArmState(leftRight, out var armed, out angle) && armed) {
                _ = SetArmOwner(leftRight, ownerLeaseId);
                LastArmConfirmed = true;
                ArmMayBeOn = true;
                yield return new WaitForSeconds(1f);
                yield break;
            }
            yield return new WaitForSeconds(0.05f);
        }

        MelonLogger.Error($"[FCS] TriggerConsole: {leftRight} relay did not confirm armed; lever X={angle:F1}°.");
        if (ArmMayBeOn) yield return Disarm(leftRight, ownerLeaseId);
    }

    /// <summary>按物理姿态解除指定炮管的武装；未能回读 OFF 时保持失败，不伪造隔离成功。</summary>
    public bool TryDisarmNow(LeftRight side, long ownerLeaseId = 0)
    {
        LastDisarmConfirmed = false;
        var owner = GetArmOwner(side);
        if (ownerLeaseId != 0 && owner != 0 && owner != ownerLeaseId) return false;
        try
        {
            if (_armRelay == null) return false;
            // 原作 OFF 接口幂等；不能靠再次点击臂杆来猜测状态。
            if (side == LeftRight.Left) _armRelay.DisarmLeft();
            else _armRelay.DisarmRight();
            if (!TryReadArmState(side, out var armed, out _) || armed) return false;
            SetArmOwner(side, 0);
            ArmMayBeOn = false;
            return LastDisarmConfirmed = true;
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[FCS] {side} 解除武装未确认: {ex.Message}");
            return false;
        }
    }

    public void StopArmsNow()
    {
        RecoveryPending = true;
        var left = TryDisarmNow(LeftRight.Left);
        var right = TryDisarmNow(LeftRight.Right);
        if (left && right) TryAcknowledgeRecovery();
    }

    public IEnumerator Disarm(LeftRight side, long ownerLeaseId = 0)
    {
        var deadline = Time.realtimeSinceStartup + ArmTransitionTimeoutSeconds;
        do
        {
            if (TryDisarmNow(side, ownerLeaseId)) yield break;
            yield return null;
        } while (Time.realtimeSinceStartup < deadline);
    }

    /// <summary>绑定或热重载后收回遗留武装；未知姿态不会盲点开关。</summary>
    public IEnumerator RecoverArms() {
        try {
            yield return Disarm(LeftRight.Left);
            yield return Disarm(LeftRight.Right);
            yield return ResetReviewState();
        }
        finally {
            RecoveryPending = true;
            TryAcknowledgeRecovery();
        }
    }

    public long GetArmOwner(LeftRight side) => side == LeftRight.Left ? _armOwnerLeft : _armOwnerRight;

    public bool TryAcknowledgeRecovery()
    {
        if (!TryReadArmState(LeftRight.Left, out var left, out _) || left
            || !TryReadArmState(LeftRight.Right, out var right, out _) || right) return false;
        RecoveryPending = false;
        return true;
    }

    public bool TryReadArmState(LeftRight side, out bool armed, out float angle) {
        armed = false;
        angle = float.NaN;
        try
        {
            var pose = side == LeftRight.Left ? _armLeftPose : _armRightPose;
            try { if (pose != null) angle = NormalizeAngle(pose.localEulerAngles.x); }
            catch { /* Optional animation diagnostics must not block relay reads. */ }
            if (_armRelay == null) return false;
            armed = side == LeftRight.Left ? _armRelay.IsLeftArmed() : _armRelay.IsRightArmed();
            return true;
        }
        catch { return false; }
    }

    private bool SetArmOwner(LeftRight side, long owner) {
        if (side == LeftRight.Left) _armOwnerLeft = owner;
        else _armOwnerRight = owner;
        return true;
    }

    private static float NormalizeAngle(float angle) {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }

    private static bool TryReadReviewPose(Transform? pose, out bool on, out float angle) {
        on = false;
        angle = 0f;
        if (pose == null) return false;

        angle = NormalizeAngle(pose.localEulerAngles.z);
        var onDistance = Mathf.Abs(Mathf.DeltaAngle(angle, ReviewOnZ));
        var offDistance = Mathf.Abs(Mathf.DeltaAngle(angle, ReviewOffZ));
        on = onDistance < offDistance;
        return Mathf.Min(onDistance, offDistance) <= ReviewPoseTolerance;
    }

    private IEnumerator EnsureReviewState(LookAtTarget? control, Transform? pose, bool desiredOn, string name) {
        if (control == null || pose == null) {
            MelonLogger.Error($"[FCS] TriggerConsole: missing {name} review control or pose.");
            yield break;
        }

        var deadline = Time.realtimeSinceStartup + ArmTransitionTimeoutSeconds;
        var current = false;
        var angle = 0f;
        while (!TryReadReviewPose(pose, out current, out angle)) {
            if (Time.realtimeSinceStartup >= deadline) {
                MelonLogger.Error($"[FCS] TriggerConsole: {name} review pose ambiguous at Z={angle:F1}°; refusing blind toggle.");
                yield break;
            }
            yield return new WaitForSeconds(0.05f);
        }

        if (current == desiredOn) yield break;

        try
        {
            control.OnClickDown();
            yield return new WaitForSeconds(0.1f);
        }
        finally { control.OnClickUp(); }
        deadline = Time.realtimeSinceStartup + ArmTransitionTimeoutSeconds;
        while (Time.realtimeSinceStartup < deadline) {
            if (TryReadReviewPose(pose, out var after, out angle) && after == desiredOn)
                yield break;
            yield return new WaitForSeconds(0.05f);
        }

        MelonLogger.Error($"[FCS] TriggerConsole: {name} review pose did not reach "
                          + $"{(desiredOn ? "ON" : "OFF")}; Z={angle:F1}°.");
    }

    private IEnumerator ConfirmAllPhysical() {
        yield return EnsureReviewState(_taskCheck, _taskPose, true, "TaskCheck");
        yield return EnsureReviewState(_bulletCheck, _bulletPose, true, "BulletCheck");
        yield return EnsureReviewState(_rotationCheck, _rotationPose, true, "RotationCheck");
        yield return EnsureReviewState(_elevationCheck, _elevationPose, true, "ElevationCheck");
        yield return EnsureReviewState(_readyFire, _readyPose, true, "ReadyToFire");
    }

    private bool AreReviewChecksConfirmed() {
        return TryReadReviewPose(_taskPose, out var taskOn, out _)
               && taskOn
               && TryReadReviewPose(_bulletPose, out var bulletOn, out _)
               && bulletOn
               && TryReadReviewPose(_rotationPose, out var rotationOn, out _)
               && rotationOn
               && TryReadReviewPose(_elevationPose, out var elevationOn, out _)
               && elevationOn
               && TryReadReviewPose(_readyPose, out var readyOn, out _)
               && readyOn;
    }

    private IEnumerator ResetReviewState() {
        yield return EnsureReviewState(_readyFire, _readyPose, false, "ReadyToFire");
        yield return EnsureReviewState(_elevationCheck, _elevationPose, false, "ElevationCheck");
        yield return EnsureReviewState(_rotationCheck, _rotationPose, false, "RotationCheck");
        yield return EnsureReviewState(_bulletCheck, _bulletPose, false, "BulletCheck");
        yield return EnsureReviewState(_taskCheck, _taskPose, false, "TaskCheck");
    }
}
