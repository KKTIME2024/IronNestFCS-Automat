using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IronNestFCS;

/// <summary>
/// Stable Host-side click dispatcher for the in-world FCS control panel.
/// It owns only Host callbacks, so no delegate retained here can keep a reloadable Logic ALC alive.
/// </summary>
internal sealed class StableClickRaycaster
{
    private readonly List<(Collider Collider, Action OnClick)> _targets = new();

    public void Register(Collider collider, Action onClick)
    {
        if (collider != null)
            _targets.Add((collider, onClick));
    }

    public void Unregister(Collider collider)
    {
        if (collider == null)
            return;
        _targets.RemoveAll(item => item.Collider == null || item.Collider.Equals(collider));
    }

    public void Update()
    {
        if (!Application.isFocused)
            return;

        var mouse = Mouse.current;
        if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
            return;

        var cam = Camera.main;
        if (cam == null)
            return;

        var mousePos = mouse.position.ReadValue();
        var ray = cam.ScreenPointToRay(new Vector3(mousePos.x, mousePos.y, 0f));
        if (!Physics.Raycast(ray, out var hit, 1000f))
            return;

        var hitCollider = hit.collider;
        foreach (var (collider, onClick) in _targets)
        {
            if (collider == null || !collider.Equals(hitCollider))
                continue;
            try { onClick(); }
            catch (Exception ex) { MelonLogger.Error($"[Click] Callback exception: {ex}"); }
            break;
        }
    }

    public void Clear() => _targets.Clear();
}
