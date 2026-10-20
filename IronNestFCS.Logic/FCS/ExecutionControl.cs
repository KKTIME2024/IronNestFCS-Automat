namespace IronNestFCS.Logic.FCS;

/// <summary>执行授权，不记录装填进度。停止后的旧命令永远不能因重新 AUTO 而复活。</summary>
public sealed class ExecutionControl
{
    public long Generation { get; private set; } = 1;
    public bool AutomaticEnabled { get; private set; }

    public void Admit(ArtilleryTask task) => task.ExecutionGeneration = Generation;
    public void StartAutomatic() => AutomaticEnabled = true;
    public bool IsAuthorized(ArtilleryTask task) => task.ExecutionGeneration == Generation
        && task.MayFire && !NeedsShotObservation(task) && (task.Source == TaskSource.Manual || AutomaticEnabled);

    public void Stop(IEnumerable<ArtilleryTask> tasks, FirePlanRegistry plans, string reason)
    {
        Generation++;
        AutomaticEnabled = false;
        foreach (var task in tasks)
        {
            task.Paused = false;
            task.PreserveAfterPause = false;
            task.RevokeFireAuthorization(reason);
            plans.Revoke(task, reason);
            if (!NeedsShotObservation(task)) task.progress = Progress.Canceled;
        }
    }

    public static bool NeedsShotObservation(ArtilleryTask task) =>
        task.TriggerSubmitted || task.Fired || task.FireResultUnknown;
}
