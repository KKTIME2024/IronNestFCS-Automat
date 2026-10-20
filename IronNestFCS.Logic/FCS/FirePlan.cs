using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>当前计划的生命周期。计划状态与炮管机构状态、在飞记录分开保存。</summary>
public enum FirePlanState {
    Queued,
    Executing,
    WaitingForResult,
    Paused,
    Canceled,
    Failed,
    Completed,
}

/// <summary>计划只记录可观测的射击结果，不把扳机请求直接当成出膛或命中。</summary>
public enum FirePlanResult {
    NotFired,
    Pending,
    Unknown,
    Confirmed,
    Failed,
}

/// <summary>
/// P1 的最小计划契约。当前每个 ArtilleryTask 先由一个隐式单发计划承载；
/// 后续的整组、两轮和条件计划可以复用同一身份、修订和授权字段。
/// </summary>
public sealed class FirePlan {
    internal FirePlan(long id, ArtilleryTask task) {
        Id = id;
        Revision = 1;
        Source = task.Source;
        TargetId = task.entityId;
        TargetName = task.entityName;
        TargetPosition = task.position;
        BulletType = task.bulletType;
        State = FirePlanState.Queued;
        Result = FirePlanResult.NotFired;
        FireAuthorized = true;
    }

    public long Id { get; }
    public int Revision { get; }
    public TaskSource Source { get; }
    public string TargetId { get; }
    public string TargetName { get; }
    public Vector3 TargetPosition { get; }
    public BulletType BulletType { get; }
    public FirePlanState State { get; private set; }
    public FirePlanResult Result { get; private set; }
    public bool FireAuthorized { get; private set; }
    public string WaitingReason { get; private set; } = "";

    internal bool Matches(ArtilleryTask task) => task.PlanId == Id && task.PlanRevision == Revision;

    internal void MarkExecuting() {
        if (State is FirePlanState.Queued or FirePlanState.Paused)
            State = FirePlanState.Executing;
    }

    internal void MarkTriggerSubmitted() {
        if (!FireAuthorized) return;
        State = FirePlanState.WaitingForResult;
        Result = FirePlanResult.Pending;
        WaitingReason = "扳机请求已提交，等待出膛确认";
    }

    internal void MarkFireConfirmed() {
        if (!FireAuthorized && Result != FirePlanResult.Pending) return;
        State = FirePlanState.WaitingForResult;
        Result = FirePlanResult.Pending;
        WaitingReason = "已确认出膛，等待落地或效果观测";
    }

    internal void MarkFireUnknown(string reason) {
        FireAuthorized = false;
        State = FirePlanState.Completed;
        Result = FirePlanResult.Unknown;
        WaitingReason = reason ?? "";
    }

    internal void Pause(string reason) {
        if (Result != FirePlanResult.NotFired
            || State is not (FirePlanState.Queued or FirePlanState.Executing or FirePlanState.Paused)) return;
        FireAuthorized = false;
        State = FirePlanState.Paused;
        WaitingReason = reason ?? "";
    }

    internal void Resume() {
        if (State != FirePlanState.Paused) return;
        FireAuthorized = true;
        State = FirePlanState.Queued;
        WaitingReason = "";
    }

    internal void Revoke(string reason) {
        FireAuthorized = false;
        WaitingReason = reason ?? "";
        if (Result == FirePlanResult.Pending) return; // 已提交只观察结果，不再授权。
        State = FirePlanState.Canceled;
        WaitingReason = reason ?? "";
        if (Result == FirePlanResult.NotFired)
            Result = FirePlanResult.Failed;
    }

    internal void Fail(string reason) {
        if (Result == FirePlanResult.Pending) return;
        FireAuthorized = false;
        State = FirePlanState.Failed;
        Result = FirePlanResult.Failed;
        WaitingReason = reason ?? "";
    }

    internal void Finish(ArtilleryTask task) {
        if (!Matches(task)) return;
        // 暂停请求可能与已经提交的扳机竞速；真实出膛或未知结果优先于
        // 软件暂停，不能把已发生的不可逆事件改记成可恢复的排队计划。
        if (task.FireResultUnknown) {
            MarkFireUnknown(task.CancelReason);
        }
        else if (task.Fired) {
            State = FirePlanState.WaitingForResult;
            Result = FirePlanResult.Pending;
            WaitingReason = "已确认出膛，等待落地或效果观测";
        }
        else if (task.Paused) {
            Pause(task.CancelReason);
        }
        else if (task.Canceled || !task.FireAuthorized) {
            Revoke(task.CancelReason);
        }
        else if (task.progress == Progress.Failed) {
            Fail(task.CancelReason);
        }
        else if (task.progress == Progress.Finished) {
            State = FirePlanState.Completed;
        }
    }

    internal void MarkFlightWindowExpired(string reason) {
        if (Result != FirePlanResult.Pending) return;
        State = FirePlanState.Completed;
        Result = FirePlanResult.Unknown;
        WaitingReason = reason;
    }
}

/// <summary>
/// 计划注册表只做身份、授权和结果生命周期管理，不直接访问 Unity 机构。
/// 所有读写都发生在 Unity 主线程，因此不引入线程锁来掩盖旧回调问题。
/// </summary>
public sealed class FirePlanRegistry {
    private readonly Dictionary<long, FirePlan> _plans = new();
    private long _nextPlanId;

    public FirePlan Admit(ArtilleryTask task) {
        var plan = new FirePlan(++_nextPlanId, task);
        _plans.Add(plan.Id, plan);
        task.PlanId = plan.Id;
        task.PlanRevision = plan.Revision;
        return plan;
    }

    public FirePlan? Get(long planId) => _plans.TryGetValue(planId, out var plan) ? plan : null;

    public IReadOnlyList<FirePlan> Snapshot() => _plans.Values.ToList();

    public bool IsAuthorized(ArtilleryTask task) {
        return task.FireAuthorized
               && task.PlanId != 0
               && _plans.TryGetValue(task.PlanId, out var plan)
               && plan.Matches(task)
               && plan.FireAuthorized
               && plan.State is FirePlanState.Queued or FirePlanState.Executing or FirePlanState.WaitingForResult;
    }

    public void MarkDispatched(ArtilleryTask task) {
        if (_plans.TryGetValue(task.PlanId, out var plan) && plan.Matches(task))
            plan.MarkExecuting();
    }

    public void MarkTriggerSubmitted(ArtilleryTask task) {
        if (_plans.TryGetValue(task.PlanId, out var plan) && plan.Matches(task))
            plan.MarkTriggerSubmitted();
    }

    public void MarkFireConfirmed(ArtilleryTask task) {
        if (_plans.TryGetValue(task.PlanId, out var plan) && plan.Matches(task))
            plan.MarkFireConfirmed();
    }

    public void MarkFireUnknown(ArtilleryTask task, string reason) {
        if (_plans.TryGetValue(task.PlanId, out var plan) && plan.Matches(task))
            plan.MarkFireUnknown(reason);
    }

    public void Revoke(ArtilleryTask task, string reason) {
        if (_plans.TryGetValue(task.PlanId, out var plan) && plan.Matches(task))
            plan.Revoke(reason);
    }

    public void Pause(ArtilleryTask task, string reason) {
        if (_plans.TryGetValue(task.PlanId, out var plan) && plan.Matches(task))
            plan.Pause(reason);
    }

    public void Resume(ArtilleryTask task) {
        if (_plans.TryGetValue(task.PlanId, out var plan) && plan.Matches(task))
            plan.Resume();
    }

    public void Fail(ArtilleryTask task, string reason) {
        if (_plans.TryGetValue(task.PlanId, out var plan) && plan.Matches(task))
            plan.Fail(reason);
    }

    public void Finish(ArtilleryTask task) {
        if (_plans.TryGetValue(task.PlanId, out var plan) && plan.Matches(task))
            plan.Finish(task);
    }

    public void MarkFlightWindowExpired(ArtilleryTask task, string reason) {
        if (_plans.TryGetValue(task.PlanId, out var plan) && plan.Matches(task))
            plan.MarkFlightWindowExpired(reason);
    }

    public void Clear() => _plans.Clear();
}
