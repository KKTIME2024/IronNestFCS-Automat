using MelonLoader.Utils;

namespace IronNestFCS.Logic.Brain;

/// <summary>
/// Opt-in switches that survive Steam's launcher process boundary. Environment
/// variables remain supported for command-line runs; an absent marker keeps all
/// Brain runtime features off by default.
/// </summary>
internal static class BrainExperimentFlags
{
    public static bool IsEnabled(string environmentVariable, string markerFileName)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(environmentVariable),
                "1",
                StringComparison.Ordinal))
            return true;

        try
        {
            var markerPath = Path.Combine(
                MelonEnvironment.UserDataDirectory,
                "IronNestFCS",
                markerFileName);
            return File.Exists(markerPath);
        }
        catch
        {
            return false;
        }
    }
}
