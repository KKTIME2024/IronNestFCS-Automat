using System;
using System.IO;

namespace IronNestFCS.Domain.Tests;

internal static class PlannerModeRemovalTests
{
    public static void NoObsoletePlanningDisableModeOrHotkeyRemains()
    {
        var root = FindRepoRoot();
        var module = File.ReadAllText(root + "/IronNestFCS.Logic/FcsModule.cs");
        AssertEx.DoesNotContain("PlanningDisabled", module);
        AssertEx.DoesNotContain("numpad7Key", module);
        AssertEx.DoesNotContain("PlannerAuthority", module);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(dir.FullName + "/IronNestFCS.sln")) dir = dir.Parent;
        if (dir == null) throw new Exception("repo root with IronNestFCS.sln not found");
        return dir.FullName;
    }
}
