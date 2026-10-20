using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// IL2CPP 游戏状态精确查询工具。基于 IDA 逆向确认的属性名。
/// </summary>
public static class GameStateWatcher
{
    public static int GetReloadStateIndex(GameObject gunRoot)
    {
        var rc = gunRoot.GetComponentInChildren<ArtilleryReloadController>();
        return rc != null ? rc.CurrentStateIndex : -1;
    }

    public static string GetReloadStateName(GameObject gunRoot)
    {
        var rc = gunRoot.GetComponentInChildren<ArtilleryReloadController>();
        return rc != null ? rc.CurrentState.ToString() : "null";
    }

    public static IEnumerator WaitForReloadState(GameObject gunRoot, int targetState, float timeout = 15f)
    {
        float waited = 0f;
        while (GetReloadStateIndex(gunRoot) < targetState && waited < timeout)
        {
            yield return new WaitForSeconds(0.3f);
            waited += 0.3f;
        }
        if (waited >= timeout)
            MelonLogger.Warning($"[GameState] WaitForReloadState timeout at state={GetReloadStateIndex(gunRoot)}, target={targetState}");
    }

    public static bool IsReloading(GunController gc) => gc.IsReloading;
    public static bool CanFire(GunController gc) => gc.CanFire;
    public static float ElevationErrorDeg(GunController gc) => gc.ElevationErrorDeg;
    public static float CurrentElevationSpeed(GunController gc) => gc.CurrentElevationSpeed;
    public static string? ChamberedShell(GunController gc) =>
        gc.ChamberedShellBlueprint?.shellDefinition?.ShellId;
    public static bool IsBreechLocked(GunController gc) =>
        gc.ExternalReloadLoweringLocked;

    public static IEnumerator WaitForReloadComplete(GunController gc, float timeout = 15f)
    {
        float waited = 0f;
        while (gc.IsReloading && waited < timeout)
        {
            yield return new WaitForSeconds(0.3f);
            waited += 0.3f;
        }
    }

    public static IEnumerator WaitForElevationSettled(GunController gc, float tolerance = 0.1f, float timeout = 30f)
    {
        float waited = 0f;
        while (Mathf.Abs(gc.ElevationErrorDeg) > tolerance && waited < timeout)
        {
            yield return new WaitForSeconds(0.3f);
            waited += 0.3f;
        }
    }
}
