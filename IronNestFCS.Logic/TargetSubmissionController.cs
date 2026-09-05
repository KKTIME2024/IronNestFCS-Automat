using System.Collections;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;

namespace IronNestFCS.Logic;

/// <summary>
/// Reloadable player-intent adapter. Stable Host UI sends only target id + shell intent;
/// marker sampling and ArtilleryTask creation remain inside the TaskSystem that F9 may discard.
/// </summary>
internal sealed class TargetSubmissionController
{
    private readonly FSC _fcs;

    public TargetSubmissionController(FSC fcs)
    {
        _fcs = fcs;
    }

    public bool TrySubmitTarget(int targetId, ShellTypeCode shell)
    {
        if (!_fcs.IsBound || !FcsRuntimeClock.IsFocused || targetId is < 1 or > 4)
            return false;

        var shellValue = (int)shell;
        if (!Enum.IsDefined(typeof(BulletType), shellValue))
        {
            MelonLogger.Warning($"[FCS] UI submitted unsupported shell code {shell}");
            return false;
        }

        _fcs.TrackCoroutine(QueueStableTarget(targetId, (BulletType)shellValue));
        return true;
    }

    private IEnumerator QueueStableTarget(int targetId, BulletType bulletType)
    {
        var clickedAt = FcsRuntimeClock.Now;
        try
        {
            ArtilleryTask? task = null;
            yield return _fcs.MapTable.GetStableMarkTarget(targetId, result => task = result);

            if (task != null)
            {
                yield return FcsRuntimeClock.WaitUntilFocused();
                task.targetId = targetId;
                task.bulletType = bulletType;
                _fcs.EnqueueTask(task);
            }

            var remainingCooldown = 1f - (FcsRuntimeClock.Now - clickedAt);
            if (remainingCooldown > 0f)
                yield return FcsRuntimeClock.WaitForSeconds(remainingCooldown);
            yield return FcsRuntimeClock.WaitUntilFocused();
        }
        finally
        {
            _fcs.ControlUi.CompleteTargetSubmission(targetId);
        }
    }

}
