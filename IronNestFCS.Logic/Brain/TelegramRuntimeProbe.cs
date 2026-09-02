using System.Collections;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
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
    private readonly Dictionary<long, PendingMessage> pendingJobs = new();
    private readonly HashSet<long> completedJobs = new();
    private HarmonyInstance? harmony;
    private PropertyInfo? jobProperty;

    private sealed record PendingMessage(PrintJob Job, string SourceId, string VisibleText);

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

        // IL2CPP renames compiler-generated state machines and changes their
        // numeric suffix between game builds (for example d__91 -> d__98).
        // Locate the state machine semantically instead of pinning that suffix.
        var runQueueType = typeof(Teleprinter)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(x => x.Name.Contains("RunQueue", StringComparison.OrdinalIgnoreCase));
        var moveNext = runQueueType?.GetMethod(
            "MoveNext",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (moveNext == null)
        {
            // Do not fall back to SubmitLines timing: it is before the player sees the
            // message and would violate the HumanTiming parity boundary.
            var nested = string.Join(", ", typeof(Teleprinter)
                .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Select(x => x.FullName ?? x.Name));
            MelonLogger.Msg($"[Brain] Teleprinter nested types: {nested}");
            MelonLogger.Warning("[Brain] Teleprinter RunQueue completion hook not found; capture disabled");
            return;
        }

        harmony = new HarmonyInstance(HarmonyId);
        harmony.Patch(
            method,
            postfix: new HarmonyMethod(
                typeof(TelegramRuntimeProbe).GetMethod(
                    nameof(SubmitLinesPostfix),
                    BindingFlags.NonPublic | BindingFlags.Static)));
        harmony.Patch(
            moveNext,
            postfix: new HarmonyMethod(
                typeof(TelegramRuntimeProbe).GetMethod(
                    nameof(RunQueueMoveNextPostfix),
                    BindingFlags.NonPublic | BindingFlags.Static)));
        current = this;
        MelonLogger.Msg("[Brain] player-parity telegram capture enabled (job-complete timing)");
    }

    private static void SubmitLinesPostfix(PrintJob __result)
    {
        var sink = current;
        if (sink == null || __result == null || string.IsNullOrWhiteSpace(__result.sourceId))
            return;

        try
        {
            var text = JoinLines(__result.lines);
            if (text.Length > 0)
                sink.pendingJobs[__result.jobId] = new PendingMessage(__result, __result.sourceId, text);
        }
        catch (Exception ex)
        {
            // A probe failure must not affect the original teleprinter call.
            MelonLogger.Warning($"[Brain] telegram capture failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RunQueueMoveNextPostfix(object __instance, bool __result)
    {
        var sink = current;
        if (sink == null)
            return;

        try
        {
            var job = sink.ReadCurrentJob(__instance);
            if (job != null && job.complete)
                sink.PublishCompleted(job);

            // The final MoveNext may clear the state-machine job before returning
            // false. Flush any completed jobs that were observed through PrintJob.
            if (!__result)
                sink.FlushCompletedJobs();
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[Brain] telegram completion probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private PrintJob? ReadCurrentJob(object stateMachine)
    {
        jobProperty ??= stateMachine.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(x => x.Name.Contains("job", StringComparison.OrdinalIgnoreCase)
                && typeof(PrintJob).IsAssignableFrom(x.PropertyType));
        return jobProperty?.GetValue(stateMachine) as PrintJob;
    }

    private void PublishCompleted(PrintJob job)
    {
        if (!completedJobs.Add(job.jobId)
            || !pendingJobs.Remove(job.jobId, out var message))
            return;

        onVisibleText(message.SourceId, message.VisibleText, Time.time);
    }

    private void FlushCompletedJobs()
    {
        foreach (var pending in pendingJobs.Values.ToArray())
        {
            // There is no hidden entity read here; this only consults the PrintJob
            // returned by SubmitLines and its public completion flag.
            // The state-machine path normally calls PublishCompleted first.
            if (pending.Job.complete)
                PublishCompleted(pending.Job);
        }
    }

    private static string JoinLines(Il2CppSystem.Collections.Generic.List<string> lines)
    {
        if (lines is not IEnumerable enumerable)
            return string.Empty;

        var textLines = new List<string>();
        foreach (var line in enumerable)
        {
            if (line != null)
                textLines.Add(line.ToString() ?? string.Empty);
        }

        return textLines.Count == 0 ? string.Empty : string.Join("\n", textLines);
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
        pendingJobs.Clear();
        completedJobs.Clear();
        jobProperty = null;
    }
}
