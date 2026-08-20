using System;

namespace IronNestFCS.Domain.Tests;

internal static class AssertEx
{
    public static void True(bool value, string? message = null)
    { if (!value) throw new InvalidOperationException(message ?? "Expected true."); }

    public static void False(bool value, string? message = null) => True(!value, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected <{expected}> but got <{actual}>."); }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    { if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"Expected [{string.Join(",", expected)}] but got [{string.Join(",", actual)}]."); }

    public static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T ex) { return ex; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

    public static void Contains(string needle, string haystack)
    { if (!haystack.Contains(needle, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected to find <{needle}>."); }

    public static void DoesNotContain(string needle, string haystack)
    { if (haystack.Contains(needle, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected NOT to find <{needle}>."); }
}
