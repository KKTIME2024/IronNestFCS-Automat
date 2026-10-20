using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public enum Progress {
    Pending,
    Calculating,
    SelectingBullet,
    LoadingBullet,
    LoadingPowder,
    WaitLoading,
    Aiming,
    WaitingForFire,
    /// <summary>扳机请求已提交，但机构尚未给出可靠的出膛结果；禁止复用炮位。</summary>
    FirePending,
    BackToIdle,
    Finished,
    Failed,
    Canceled,
}

public class ArtilleryTask {
    public int targetId;
    public string entityId = "";   // MapEntity key for dedup
    public string entityName = ""; // MapEntity.Name 显示名(面板目标识别用; 空则回退 entityId)
    public float angel;
    public float distance;
    public Vector3 position;
    public BulletType bulletType;
    public Progress progress;
    /// <summary>为 true 时强制满装药量(6包)，覆盖用户全局 maxCharge 设置</summary>
    public bool useMaxCharge;
    /// <summary>遗留兼容字段；停止时清除，不能覆盖击发授权</summary>
    public bool forceFire;
    /// <summary>任务来源: 雷达自动 or 玩家手动(手动任务不被自动清队列清掉)</summary>
    public TaskSource Source = TaskSource.Auto;
    /// <summary>切手动时置位: 未开始装填的自动任务干净放弃, 不碰炮膛</summary>
    public bool Canceled;
    /// <summary>
    /// 当前炮位占用的代次。任务结束、看门狗恢复或热重载后的旧回调只能清理仍由自己持有的代次。
    /// 0 表示尚未派给炮位。
    /// </summary>
    public long SlotLeaseId;
    /// <summary>执行授权代次；停止后旧任务永久失效，不记录装填进度。</summary>
    public long ExecutionGeneration;
    /// <summary>按现场保留弹规划的自动任务只交给这门炮。</summary>
    public LeftRight? PreferredSide;
    public bool UsesRetainedRound;
    /// <summary>已越过扳机提交边界；停止后仅允许观察结果。</summary>
    public bool TriggerSubmitted;
    /// <summary>计划层身份；自动单发也使用隐式计划，避免执行器只认炮位。</summary>
    public long PlanId;
    /// <summary>计划修订号。改派后旧回调不得再授予击发许可。</summary>
    public int PlanRevision;
    /// <summary>明确绑定的共享扳机组；0 表示单发，不能因方位偶然接近而齐射。</summary>
    public long SharedTriggerGroupId;
    /// <summary>提交给执行器的击发授权。取消／暂停时先撤销授权，再处理机构交接。</summary>
    public bool FireAuthorized = true;
    /// <summary>取消或进入恢复状态的可见原因。</summary>
    public string CancelReason = "";
    /// <summary>自动模式暂停留下的可恢复计划；与明确取消区分。</summary>
    public bool Paused;
    /// <summary>从自动暂停恢复后仍需保留的队列计划，防周期扫荡把它当成普通候选清掉。</summary>
    public bool PreserveAfterPause;
    /// <summary>已通过控制台姿态回读确认武装。只要为 true，炮位释放前必须完成解除武装交接。</summary>
    public bool ArmConfirmed;
    /// <summary>扳机请求已提交但出膛未知；保留目标登记和炮位，禁止自动重发或复用机构。</summary>
    public bool FireResultUnknown;
    /// <summary>进入出膛未知状态的游戏时刻，供目标登记的未知观察窗口使用。</summary>
    public float FireResultUnknownAt;
    /// <summary>已击发(Registry 飞行窗口计时依据; 未击发的任务结束时 Release 登记)</summary>
    public bool Fired;
    /// <summary>解算后置位: 射表估计飞行时间(秒)。面板倒计时依据, 归零=估计落地。</summary>
    public float EstimatedToF;
    /// <summary>开火瞬间 Time.time, 面板倒计时起点。</summary>
    public float FiredAt;
    /// <summary>该任务是否按移动目标处理（创建时由雷达 IsMoving 一次判定；手动任务一律 false）</summary>
    public bool IsMoving;
    /// <summary>是否 FDC 目标(暂停组合拳): 击发前需保证无旧弹在 FDC 落地之后落地,
    /// 否则 FDC 触发的暂停被旧弹落地立即解除(2026-08-15 用户提出)。</summary>
    public bool IsFdc;
    /// <summary>创建时目标速度未建立(刚出现/热重载): 装填期从雷达采纳后置 false, 快照重置</summary>
    public bool VelocityUnknown;
    /// <summary>装填时定格的装药数（覆盖全程预测最远距离, 仰角重算用它）</summary>
    public int LoadedCharge;
    /// <summary>移动目标冻结快照: 创建时位置(世界单位) + 速度(世界单位/s) + 参考时刻。
    /// aim(t) = AimP0 + AimVel×(t − AimStartTime + ToF), 匀速假设下闭合式自洽。</summary>
    public Vector3 AimP0;
    public Vector3 AimVel;
    public float AimStartTime;
    /// <summary>该弹毁伤半径(km), 集群/爆区覆盖用。0 = 无爆区(未设置)。</summary>
    public float BlastRadiusKm;
    /// <summary>移动集群覆盖成员(entityId): 击发时按实体登记——爆区几何以落点为中心,
    /// 车列在落点后方(提前点在行进方向前方), 在飞屏蔽需按实体。死亡由 Reconcile 释放。</summary>
    public List<string>? ClusterMembers;
    /// <summary>变速/停车二次采纳的跟踪对象: 集群任务自身 entityId=""(位置提交, 注册表按落点覆盖),
    /// 但停车采纳需要跟踪领队(车列编队同步动停)。单点任务不设, 回退用 entityId。</summary>
    public string TrackEntityId = "";
    /// <summary>移动集群落点相对领队的偏移(世界单位): 停车采纳时落点 = 领队当前位置 + 偏移,
    /// 保持车列几何不变(落点仍在车列中点)——否则落点跳车头, 覆盖判定与爆炸错位(5 节只炸前 3 节)。</summary>
    public Vector3 ClusterOffset;

    public bool MayFire => !Canceled && !Paused && FireAuthorized;
    public bool ShouldReleaseTargetOnExit => !Paused && !TriggerSubmitted && !Fired && !FireResultUnknown;

    public void RevokeFireAuthorization(string reason)
    {
        FireAuthorized = false;
        forceFire = false;
        Canceled = true;
        CancelReason = reason ?? "";
    }
}
