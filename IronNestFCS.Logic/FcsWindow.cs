using System;
using IronNestFCS.Logic.FCS;
using UnityEngine;

namespace IronNestFCS.Logic;

public class FcsWindow
{
    private readonly FSC fcs;
    private Rect panelRect = new(18, 18, 310, 116);
    private bool expanded;
    private LeftRight? confirmUnload;
    private string actionMessage = "";
    public bool AutoSweepEnabled { get; set; }
    public Action? ToggleAutoModeRequested { get; set; }
    public Action? StopRequested { get; set; }
    public Action? StartAutomaticRequested { get; set; }

    public FcsWindow(FSC fcs) => this.fcs = fcs;

    public bool PointerInsidePanel()
    {
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null) return false;
        var position = mouse.position.ReadValue();
        return panelRect.Contains(new Vector2(position.x, Screen.height - position.y));
    }

    public void OnGui()
    {
        const float row = 24;
        float x = panelRect.x + 8, y = panelRect.y + 6, w = panelRect.width - 16;
        var extraRows = expanded ? 3 + fcs.PendingCount : 0;
        if (expanded && fcs.AutomaticExclusions.Count > 0) extraRows++;
        if (expanded && actionMessage.Length > 0) extraRows++;
        if (fcs.UnobservedFlightPossible || (AutoSweepEnabled && fcs.AutomaticExecutionPaused)
            || fcs.TriggerConsole.RecoveryPending) extraRows++;
        else if (AutoSweepEnabled && fcs.ExecutionBlockReason.Length > 0) extraRows++;
        if (fcs.LeftTask?.FireResultUnknown == true) extraRows++;
        if (fcs.RightTask?.FireResultUnknown == true) extraRows++;
        panelRect.height = 108 + row * extraRows;
        GUI.Box(panelRect, "");
        GUI.Label(new Rect(x, y, 42, 22), "FCS");
        if (GUI.Button(new Rect(x + 48, y, w - 146, 22), AutoSweepEnabled ? "AUTO" : "MANUAL"))
            ToggleAutoModeRequested?.Invoke();
        if (GUI.Button(new Rect(x + w - 92, y, 60, 22), "STOP")) StopRequested?.Invoke();
        if (GUI.Button(new Rect(x + w - 28, y, 28, 22), expanded ? "-" : "+"))
        {
            expanded = !expanded;
            confirmUnload = null;
        }
        y += row;
        if (!fcs.IsBound)
        {
            GUI.Label(new Rect(x, y, w, 22), "Waiting for scene");
            return;
        }
        DrawGun("L", fcs.LeftTask, x, y, w);
        y += row;
        DrawGun("R", fcs.RightTask, x, y, w);
        y += row;
        GUI.Label(new Rect(x, y, w, 22), $"Queue {fcs.PendingCount}");
        y += row;

        if (fcs.UnobservedFlightPossible)
        {
            if (GUI.Button(new Rect(x, y, w, 22), "After reload: confirm flight clear / resume"))
            {
                if (fcs.AcknowledgeReloadSafety() && AutoSweepEnabled) StartAutomaticRequested?.Invoke();
            }
            y += row;
        }
        else if (AutoSweepEnabled && fcs.AutomaticExecutionPaused)
        {
            if (GUI.Button(new Rect(x, y, w, 22), "Restart automatic from current state")) StartAutomaticRequested?.Invoke();
            y += row;
        }
        else if (fcs.TriggerConsole.RecoveryPending)
        {
            GUI.Label(new Rect(x, y, w, 22), "Waiting for disarm confirmation");
            y += row;
        }
        else if (AutoSweepEnabled && fcs.ExecutionBlockReason.Length > 0)
        {
            GUI.Label(new Rect(x, y, w, 22), fcs.ExecutionBlockReason);
            y += row;
        }
        y = DrawUnknown(LeftRight.Left, fcs.LeftTask, x, y, w);
        y = DrawUnknown(LeftRight.Right, fcs.RightTask, x, y, w);
        if (!expanded) return;

        y = DrawCancel(fcs.LeftTask, "L", x, y, w);
        y = DrawCancel(fcs.RightTask, "R", x, y, w);
        foreach (var task in fcs.QueueCan) y = DrawCancel(task, "Queued", x, y, w);
        DrawUnload(LeftRight.Left, x, y, w / 2 - 3);
        DrawUnload(LeftRight.Right, x + w / 2 + 3, y, w / 2 - 3);
        y += row;
        if (fcs.AutomaticExclusions.Count > 0)
        {
            if (GUI.Button(new Rect(x, y, w, 22), $"Restore excluded targets ({fcs.AutomaticExclusions.Count})"))
                fcs.AutomaticExclusions.Clear();
            y += row;
        }
        if (actionMessage.Length > 0) GUI.Label(new Rect(x, y, w, 22), actionMessage);
    }

    private void DrawGun(string side, ArtilleryTask? task, float x, float y, float w)
    {
        var gun = side == "L" ? fcs.LeftGun : fcs.RightGun;
        var status = task == null ? IdleGunStatus(gun) : task.progress.ToString();
        var target = task == null ? "" : $"  {task.entityName} {task.distance:F1}km";
        GUI.Label(new Rect(x, y, w, 22), $"{side}  {status}{target}");
    }

    private string IdleGunStatus(GunSystem gun)
    {
        var state = gun.AssessChamber();
        if (state == GunSystem.ChamberState.Empty) return "Idle";
        if (state == GunSystem.ChamberState.ReadyToFire)
            return $"Retained {gun.BulletInChamber()} + {gun.ChamberCharges()}"
                + (AutoSweepEnabled ? " / waiting for suitable target" : "");
        return "Check chamber / loading mechanism";
    }

    private float DrawUnknown(LeftRight side, ArtilleryTask? task, float x, float y, float w)
    {
        if (task?.FireResultUnknown != true) return y;
        if (GUI.Button(new Rect(x, y, w, 22), $"{side}: check empty chamber / take over"))
            fcs.TryAcknowledgeUnknownFire(side);
        return y + 24;
    }

    private float DrawCancel(ArtilleryTask? task, string label, float x, float y, float w)
    {
        if (task != null && !task.Fired && !task.FireResultUnknown)
        {
            if (GUI.Button(new Rect(x, y, w, 22), $"Cancel {label}: {task.entityName}"))
                fcs.RevokeTask(task, "玩家取消计划");
        }
        return y + 24;
    }

    private void DrawUnload(LeftRight side, float x, float y, float w)
    {
        if (!GUI.Button(new Rect(x, y, w, 22), confirmUnload == side ? "Confirm ammo loss" : $"Unload {side}")) return;
        if (confirmUnload != side) { confirmUnload = side; return; }
        actionMessage = fcs.RequestUnload(side) ? $"{side}: unloading" : $"{side}: mechanism busy";
        confirmUnload = null;
    }
}
