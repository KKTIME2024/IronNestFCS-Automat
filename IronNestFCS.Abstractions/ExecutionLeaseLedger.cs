namespace IronNestFCS.Abstractions;

/// <summary>
/// 主线程执行资源的代次登记。新占用会得到不可复用的身份；旧回调只能释放仍属于自己的代次。
/// 该类型不依赖 Unity，便于对交接和乱序回调做回归检查。
/// </summary>
public sealed class ExecutionLeaseLedger
{
    private readonly Dictionary<string, long> _owners = new();
    private long _nextLeaseId;

    public long Acquire(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
            throw new ArgumentException("Resource name is required.", nameof(resource));

        var leaseId = ++_nextLeaseId;
        _owners[resource] = leaseId;
        return leaseId;
    }

    public bool IsOwner(string resource, long leaseId)
    {
        return leaseId != 0
               && _owners.TryGetValue(resource, out var current)
               && current == leaseId;
    }

    public bool Release(string resource, long leaseId)
    {
        if (!IsOwner(resource, leaseId)) return false;
        _owners.Remove(resource);
        return true;
    }

    public void Clear() => _owners.Clear();
}
