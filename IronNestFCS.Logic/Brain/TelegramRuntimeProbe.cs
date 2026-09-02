using System.Collections;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppEnumerable = Il2CppSystem.Collections.Generic.IEnumerable<string>;
using MelonLoader;
using UnityEngine;
using HarmonyInstance = HarmonyLib.Harmony;

namespace IronNestFCS.Logic.Brain;

/// <summary>
/// Main-thread tap for the text passed to Teleprinter.SubmitLines.
/// 
/// SubmitLines is used after the mission text/token expansion boundary, so this
/// probe observes the same line payload that the teleprinter is about to queue.
/// It never inspects State_TeleprinterText replacements, FireMission.Entities,
/// MapEntity, or any other hidden target state.
/// </summary>
public sealed class TelegramRuntimeProbe : IDisposable
{
    private const string HarmonyId = "com.svr2kos2.ironnestfcs.brain.telegram";
    private static TelegramRuntimeProbe? current;

    private readonly Action<string, string, double> onVisibleText;
    private HarmonyInstance? harmony;

    public TelegramRuntimeProbe(Action<string, string, double> onVisibleText)
    {
        this.onVisibleText = onVisibleText ?? throw new ArgumentNullException(nameof(onVisibleText));
    }

    /// <summary>Opt-in switch so ordinary FCS use remains unchanged.</summary>
    public static bool Enabled =>
        string.Equals(
            Environment.GetEnvironmentVariable("IRONNEST_BRAIN_TELEGRAM"),
            "1",
            StringComparison.Ordinal);

    public void Install()
    {
        if (current != null)
            return;

        var method = typeof(Teleprinter).GetMethod(
            "SubmitLines",
            BindingFlags.Public | BindingFlags.Instance);
        if (method == null)
        {
            MelonLogger.Warning("[Brain] Teleprinter.SubmitLines not found; telegram capture disabled");
            return;
        }

        harmony = new HarmonyInstance(HarmonyId);
        harmony.Patch(
            method,
            prefix: new HarmonyMethod(
                typeof(TelegramRuntimeProbe).GetMethod(
                    nameof(SubmitLinesPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static)));
        current = this;
        MelonLogger.Msg("[Brain] player-parity telegram capture enabled");
    }

    private static void SubmitLinesPrefix(string sourceId, Il2CppEnumerable lines)
    {
        var sink = current;
        if (sink == null || string.IsNullOrWhiteSpace(sourceId) || lines is not IEnumerable enumerable)
            return;

        try
        {
            var textLines = new List<string>();
            foreach (var line in enumerable)
            {
                if (line != null)
                    textLines.Add(line.ToString() ?? string.Empty);
            }

            if (textLines.Count > 0)
                sink.onVisibleText(sourceId, string.Join("\n", textLines), Time.time);
        }
        catch (Exception ex)
        {
            // A probe failure must not affect the original teleprinter call.
            MelonLogger.Warning($"[Brain] telegram capture failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(current, this))
            current = null;

        try { harmony?.UnpatchSelf(); }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[Brain] telegram probe unpatch failed: {ex.GetType().Name}: {ex.Message}");
        }
        harmony = null;
    }
}
