using IronNestFCS.Logic;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>发射请求与炮位任务分离的结果状态。</summary>
public enum FireRecordState
{
    Submitted,
    Launched,
    ResultUnknown,
    FlightWindowExpired,
    Canceled,
}

/// <summary>
/// 不随炮位槽位复用的发射记录。它只保存值数据和计划身份，供 CBT、结果观察和热重载诊断使用。
/// </summary>
public sealed class FireRecord
{
    internal FireRecord(ArtilleryTask task, LeftRight side, FireRecordState state, float eventTime, string reason = "")
    {
        PlanId = task.PlanId;
        PlanRevision = task.PlanRevision;
        TargetId = task.entityId;
        TargetName = task.entityName;
        Side = side;
        BulletType = task.bulletType;
        Charge = task.LoadedCharge;
        Origin = task.position;
        AimSnapshot = task.AimP0;
        EstimatedToF = task.EstimatedToF;
        EventTime = eventTime;
        State = state;
        Reason = reason ?? "";
    }

    public long PlanId { get; }
    public int PlanRevision { get; }
    public string TargetId { get; }
    public string TargetName { get; }
    public LeftRight Side { get; }
    public BulletType BulletType { get; }
    public int Charge { get; }
    public Vector3 Origin { get; }
    public Vector3 AimSnapshot { get; }
    public float EstimatedToF { get; }
    public float EventTime { get; private set; }
    public FireRecordState State { get; private set; }
    public string Reason { get; private set; }

    internal void MarkLaunched(ArtilleryTask task)
    {
        State = FireRecordState.Launched;
        EventTime = task.FiredAt;
        Reason = "";
    }

    internal void MarkUnknown(float eventTime, string reason)
    {
        State = FireRecordState.ResultUnknown;
        EventTime = eventTime;
        Reason = reason ?? "";
    }

    internal void MarkFlightWindowExpired(string reason)
    {
        if (State != FireRecordState.Launched) return;
        State = FireRecordState.FlightWindowExpired;
        Reason = reason ?? "";
    }

    internal void MarkCanceled(string reason)
    {
        if (State != FireRecordState.Submitted) return;
        State = FireRecordState.Canceled;
        Reason = reason ?? "";
    }
}
