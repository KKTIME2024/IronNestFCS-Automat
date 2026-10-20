using System;
using System.Collections;
using System.Collections.Generic;

namespace IronNestFCS.Abstractions;

/// <summary>由同一个句柄驱动嵌套动作；撤销授权时停止所有子迭代器并执行清理。</summary>
public static class CancellableRoutine
{
    public static IEnumerator Run(IEnumerator root, Func<bool> isAuthorized)
    {
        var stack = new Stack<IEnumerator>();
        stack.Push(root);
        try
        {
            while (stack.Count > 0 && isAuthorized())
            {
                var current = stack.Peek();
                if (!current.MoveNext())
                {
                    stack.Pop();
                    (current as IDisposable)?.Dispose();
                    continue;
                }
                if (current.Current is IEnumerator child)
                    stack.Push(child);
                else
                    yield return current.Current;
            }
        }
        finally
        {
            List<Exception>? errors = null;
            while (stack.Count > 0)
            {
                try { (stack.Pop() as IDisposable)?.Dispose(); }
                catch (Exception error) { (errors ??= new()).Add(error); }
            }
            if (errors != null) throw new AggregateException("Nested action cleanup failed", errors);
        }
    }
}
