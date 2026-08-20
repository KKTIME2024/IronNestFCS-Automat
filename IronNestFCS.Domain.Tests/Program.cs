using IronNestFCS.Domain.Tests;

var tests = new (string Name, Action Run)[]
{
    ("test runner reports a passing assertion", () => AssertEx.Equal(2, 1 + 1))
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}

Console.WriteLine($"RESULT total={tests.Length} failed={failed}");
return failed == 0 ? 0 : 1;
