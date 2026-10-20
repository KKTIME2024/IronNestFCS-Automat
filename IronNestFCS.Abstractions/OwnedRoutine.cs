using System.Collections;

namespace IronNestFCS.Abstractions;

/// <summary>
/// Owns the managed iterator as well as the scheduler token. MelonLoader's IL2CPP
/// StopCoroutine does not dispose managed iterators; Stop must do both explicitly.
/// </summary>
public sealed class OwnedRoutine : IEnumerator, IDisposable
{
    private readonly IEnumerator _inner;
    private readonly Action<object> _stop;
    private readonly Action<OwnedRoutine>? _onCompleted;
    private object? _token;
    private bool _started;
    public bool IsCompleted { get; private set; }

    public OwnedRoutine(IEnumerator root, Action<object> stop, Action<OwnedRoutine>? onCompleted = null)
    {
        _inner = CancellableRoutine.Run(root, () => !IsCompleted);
        _stop = stop;
        _onCompleted = onCompleted;
    }

    public void Start(Func<IEnumerator, object> start)
    {
        if (_started || IsCompleted) throw new InvalidOperationException("Routine already started or stopped.");
        _started = true;
        try { _token = start(this); }
        catch { Dispose(); throw; }
    }

    public object? Current => _inner.Current;
    public bool MoveNext()
    {
        if (IsCompleted) return false;
        var moved = false;
        try { return moved = _inner.MoveNext(); }
        finally { if (!moved) Dispose(); }
    }

    public void Reset() => throw new NotSupportedException();

    public void Stop()
    {
        if (IsCompleted) return;
        // Invalidate before calling the scheduler. Even if it fails to stop, a
        // later MoveNext cannot drive this routine or any of its nested actions.
        IsCompleted = true;
        try { if (_token != null) _stop(_token); }
        finally { DisposeInner(); }
    }

    public void Dispose()
    {
        if (IsCompleted) return;
        IsCompleted = true;
        DisposeInner();
    }

    private void DisposeInner()
    {
        try { (_inner as IDisposable)?.Dispose(); }
        finally { _onCompleted?.Invoke(this); }
    }
}
