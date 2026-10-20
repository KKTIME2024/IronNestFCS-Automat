using System.Collections;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 协程级互斥锁。MelonCoroutines 全部在 Unity 主线程上协作式调度，没有真正并发，
/// 每次占用使用独立 lease，旧 finally 不能释放后继持有者；不引入线程锁。
///
/// 用法（务必配 try/finally，保证协程被 Stop / yield break 时也能释放）：
/// <code>
/// var lease = deskLock.CreateLease();
/// yield return deskLock.Acquire(lease);
/// try { /* 临界区，可含 yield return */ }
/// finally { deskLock.Release(lease); }
/// </code>
/// 停止时必须由 OwnedRoutine 显式 Dispose 托管迭代器；不能依赖 MelonCoroutines.Stop 执行 finally。
/// </summary>
public sealed class CoroutineLock {
    public sealed class Lease { internal int Epoch; internal Lease(int epoch) { Epoch = epoch; } }
    private Lease? _owner;
    private int _epoch;
    public Lease CreateLease() => new(_epoch);

    /// <summary>等待直到拿到锁。拿到后立即占用，调用方负责在 finally 里 Release。</summary>
    public IEnumerator Acquire(Lease lease) {
        // 每帧重试一次。持锁方在主线程推进，释放后下一帧本协程即可抢到。
        while (_owner != null && lease.Epoch == _epoch) {
            yield return null;
        }
        if (lease.Epoch != _epoch) throw new InvalidOperationException("Lock lease invalidated by rebinding");
        _owner = lease;
    }

    public bool Release(Lease lease) {
        if (!ReferenceEquals(_owner, lease)) return false;
        _owner = null;
        return true;
    }

    /// <summary>重绑定（热重载）时强制复位，防止上一轮异常残留导致死锁。</summary>
    public void Reset() {
        _owner = null;
        _epoch++;
    }
}
