using System.Collections;
using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

/// <summary>
/// Reloadable TaskSystem physical-click helper. This is execution plumbing, not player UI.
/// F9 releases any held click before the old Logic ALC is unloaded.
/// </summary>
internal static class PhysicalClickHelper
{
    private static readonly List<LookAtTarget> HeldPhysicalClicks = new();

    public static void BeginPhysicalClick(LookAtTarget button)
    {
        button.OnClickDown();
        if (!HeldPhysicalClicks.Contains(button))
            HeldPhysicalClicks.Add(button);
    }

    public static void EndPhysicalClick(LookAtTarget button)
    {
        try { button.OnClickUp(); }
        finally { HeldPhysicalClicks.Remove(button); }
    }

    public static void ReleaseHeldPhysicalClicks(string reason)
    {
        if (HeldPhysicalClicks.Count == 0)
            return;

        var held = HeldPhysicalClicks.ToArray();
        HeldPhysicalClicks.Clear();
        foreach (var button in held)
        {
            try
            {
                button.OnClickUp();
                MelonLogger.Warning($"[FCS] Released TaskSystem click during {reason}: {button.gameObject.name}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[FCS] Failed to release TaskSystem click during {reason}: {ex.Message}");
            }
        }
    }

    public static IEnumerator WaitAndClick(
        LookAtTarget? button,
        float timeoutSeconds = 10f,
        Func<bool>? shouldContinue = null)
    {
        if (button == null || (shouldContinue != null && !shouldContinue()))
            yield break;

        var deadline = FcsRuntimeClock.Now + Mathf.Max(0.1f, timeoutSeconds);
        while (true)
        {
            yield return FcsRuntimeClock.WaitUntilFocused();
            if (shouldContinue != null && !shouldContinue())
                yield break;
            if (button.isActive && button.nextAllowedClickTime <= Time.realtimeSinceStartup)
                break;

            if (FcsRuntimeClock.Now >= deadline)
            {
                MelonLogger.Error($"[FCS] WaitAndClick timeout: {button.gameObject.name}");
                yield break;
            }
            yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        }

        yield return FcsRuntimeClock.WaitForSeconds(0.1f);
        yield return FcsRuntimeClock.WaitUntilFocused();
        if (shouldContinue != null && !shouldContinue())
            yield break;

        BeginPhysicalClick(button);
        try
        {
            yield return new WaitForSeconds(0.1f);
        }
        finally
        {
            EndPhysicalClick(button);
        }
    }
}
