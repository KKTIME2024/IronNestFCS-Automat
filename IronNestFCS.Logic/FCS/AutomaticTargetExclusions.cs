namespace IronNestFCS.Logic.FCS;

/// <summary>观察到旧 ID 消失后，后续同名单位不继承明确取消的排除。</summary>
public sealed class AutomaticTargetExclusions
{
    private readonly HashSet<string> _ids = new();
    public int Count => _ids.Count;
    public bool Contains(string id) => _ids.Contains(id);
    public string[] Snapshot() => _ids.ToArray();
    public void Restore(IEnumerable<string> ids)
    {
        _ids.Clear();
        foreach (var id in ids) if (!string.IsNullOrEmpty(id)) _ids.Add(id);
    }
    public void Exclude(ArtilleryTask task)
    {
        if (!string.IsNullOrEmpty(task.entityId)) _ids.Add(task.entityId);
        if (!string.IsNullOrEmpty(task.TrackEntityId)) _ids.Add(task.TrackEntityId);
        if (task.ClusterMembers != null)
            foreach (var id in task.ClusterMembers) _ids.Add(id);
    }
    public void ObserveAliveIds(IEnumerable<string> ids)
    {
        var alive = new HashSet<string>(ids);
        _ids.RemoveWhere(id => !alive.Contains(id));
    }
    public void Clear() => _ids.Clear();
}
