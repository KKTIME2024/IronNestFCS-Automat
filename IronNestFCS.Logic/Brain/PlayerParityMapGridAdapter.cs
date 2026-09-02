using Il2Cpp;
using IronNestFCS.Abstractions.Brain;
using UnityEngine;

namespace IronNestFCS.Logic.Brain;

/// <summary>
/// Resolves a grid code that was explicitly printed to the player through the
/// game's own public map geometry. It never enumerates FireMission.Entities and
/// never reads a target object, so it is safe to use as a PlayerParity adapter.
/// </summary>
public sealed class PlayerParityMapGridAdapter
{
    private FireMission? fireMission;
    private RectTransform? coordinateRoot;
    private float cellWidth;
    private float cellHeight;
    private bool yIncreasesUp;
    private float distanceToKmScale;

    public bool TryResolve(string? gridCode, out MapPoint mapPositionKm)
    {
        mapPositionKm = default;
        if (!TryResolveLocal(gridCode, out var local))
            return false;

        mapPositionKm = new MapPoint(
            local.x * distanceToKmScale,
            local.y * distanceToKmScale);
        return true;
    }

    /// <summary>
    /// Converts a previously accepted player-visible map point back to world
    /// space. The point must have been produced from this map's public scale;
    /// this method never looks up an entity at that position.
    /// </summary>
    public bool TryMapPointToWorld(MapPoint mapPositionKm, out Vector3 worldPosition)
    {
        worldPosition = default;
        if (!EnsureMapGeometry() || coordinateRoot == null || distanceToKmScale <= 0f)
            return false;

        try
        {
            var local = new Vector3(
                (float)(mapPositionKm.XKm / distanceToKmScale),
                (float)(mapPositionKm.YKm / distanceToKmScale),
                0f);
            worldPosition = coordinateRoot.TransformPoint(local);
            return true;
        }
        catch (Exception)
        {
            fireMission = null;
            coordinateRoot = null;
            return false;
        }
    }

    /// <summary>
    /// Resolve the same visible grid designation into the game's world space.
    /// This is for the future high-level actuator only; it still starts from a
    /// printed grid and public map geometry, never from an entity transform.
    /// </summary>
    public bool TryResolveWorldPosition(string? gridCode, out Vector3 worldPosition)
    {
        worldPosition = default;
        if (!TryResolveLocal(gridCode, out var local) || coordinateRoot == null)
            return false;

        try
        {
            worldPosition = coordinateRoot.TransformPoint(new Vector3(local.x, local.y, 0f));
            return true;
        }
        catch (Exception)
        {
            fireMission = null;
            coordinateRoot = null;
            return false;
        }
    }

    private bool TryResolveLocal(string? gridCode, out Vector2 local)
    {
        local = default;
        if (string.IsNullOrWhiteSpace(gridCode) || !EnsureMapGeometry())
            return false;

        try
        {
            local = GridCodeConverter.CodeToLocal(
                gridCode.Trim(),
                cellWidth,
                cellHeight,
                yIncreasesUp);
            return !(float.IsNaN(local.x) || float.IsNaN(local.y)
                || float.IsInfinity(local.x) || float.IsInfinity(local.y));
        }
        catch (Exception)
        {
            // Scene reloads can invalidate IL2CPP references between events.
            fireMission = null;
            coordinateRoot = null;
            return false;
        }
    }

    private bool EnsureMapGeometry()
    {
        try
        {
            if (fireMission != null && coordinateRoot != null)
                return true;

            var root = GameObject.Find("Fire Mission Root");
            fireMission = root?.GetComponent<FireMission>();
            coordinateRoot = fireMission?.coordinateRoot;
            if (fireMission == null || coordinateRoot == null)
                return false;

            cellWidth = fireMission.cellWidth;
            cellHeight = fireMission.cellHeight;
            yIncreasesUp = fireMission.yIncreasesUp;
            distanceToKmScale = fireMission.distanceToKmScale;
            return cellWidth > 0f && cellHeight > 0f && distanceToKmScale > 0f;
        }
        catch (Exception)
        {
            fireMission = null;
            coordinateRoot = null;
            return false;
        }
    }
}
