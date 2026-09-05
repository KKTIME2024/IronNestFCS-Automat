using IronNestFCS.Abstractions;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(IronNestFCS.FcsHostMod), "IronNestFCS Smart", "1.2.8", "svr2kos2")]
[assembly: MelonGame()]

namespace IronNestFCS;

/// <summary>
/// Stable Host. F9 reloads TaskSystem only; PersistentLoadingSystem and the FCS control panel remain alive.
/// Already-accepted physical loading and player UI intent therefore survive TaskSystem replanning.
/// </summary>
public class FcsHostMod : MelonMod
{
    private const string ReloadKeyName = "F9";
    private const string HudToggleKeyName = "F8";
    private const string LogicTypeName = "IronNestFCS.Logic.FcsModule";
    private const float InitialBindDelaySeconds = 1f;
    private const float SceneBindDelaySeconds = 3f;
    private const float BindRetryDelaySeconds = 1f;

    private readonly FcsHostServices _hostServices = new();
    private LogicReloader? _reloader;
    private FcsControlPanel? _controlPanel;
    private bool _sceneBindPending;
    private float _nextBindAttemptAt;
    private bool _shutdownStarted;
    private bool _hudHidden;

    public override void OnInitializeMelon()
    {
        var logicDir = Path.Combine(
            MelonEnvironment.UserDataDirectory,
            "IronNestFCS");
        Directory.CreateDirectory(logicDir);
        var logicDll = Path.Combine(logicDir, "IronNestFCS.Logic.dll");

        MelonLogger.Msg($"IronNestFCS Smart Host Started. Logic path: {logicDll}");
        MelonLogger.Msg($"Press {ReloadKeyName} to hot reload TaskSystem.");

        _controlPanel = new FcsControlPanel(TrySubmitTargetFromUi, OnAutoFireEnabledFromUi);
        _hostServices.ControlUiRuntime = _controlPanel;

        _reloader = new LogicReloader(
            logicDll,
            LogicTypeName,
            _hostServices);

        // Do not instantiate TaskSystem before the persistent physical runtime is bound. During process start
        // the game objects commonly do not exist yet; loading Logic at that point only creates a false failed
        // diagnostic session which immediately has to be replaced.
        ScheduleSceneBind(InitialBindDelaySeconds);
    }

    private static bool ReloadKeyPressed()
    {
        var kb = Keyboard.current;
        return kb != null && kb.f9Key.wasPressedThisFrame;
    }

    private static bool HudToggleKeyPressed()
    {
        var kb = Keyboard.current;
        return kb != null && kb.f8Key.wasPressedThisFrame;
    }

    private bool TrySubmitTargetFromUi(int targetId, ShellTypeCode shell)
    {
        try { return _reloader?.Current?.TrySubmitTarget(targetId, shell) ?? false; }
        catch (Exception ex)
        {
            MelonLogger.Error($"FCS UI target submission failed: {ex}");
            return false;
        }
    }

    private void OnAutoFireEnabledFromUi()
    {
        try { _reloader?.Current?.OnAutoFireEnabled(); }
        catch (Exception ex) { MelonLogger.Error($"FCS UI AutoFire notification failed: {ex}"); }
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        // A real scene transition invalidates both TaskSystem handles and persistent physical handles.
        // Stop TaskSystem immediately and perform one serialized delayed rebind instead of stacking coroutines.
        _reloader?.Unload();
        _controlPanel?.OnSceneChanged();
        _hostServices.LoadingRuntime.OnSceneChanged();
        ScheduleSceneBind(SceneBindDelaySeconds);
    }

    private void ScheduleSceneBind(float delaySeconds)
    {
        _sceneBindPending = true;
        _nextBindAttemptAt = Time.unscaledTime + Math.Max(0f, delaySeconds);
    }

    private void TryActivateScene()
    {
        if (!_sceneBindPending || _reloader == null || Time.unscaledTime < _nextBindAttemptAt)
            return;

        if (!_hostServices.LoadingRuntime.IsBound && !_hostServices.LoadingRuntime.TryBindScene())
        {
            _nextBindAttemptAt = Time.unscaledTime + BindRetryDelaySeconds;
            return;
        }

        if (_reloader.Reload())
        {
            _controlPanel?.Activate();
            _sceneBindPending = false;
            return;
        }

        // Loading may already be ready while another scene-owned console is still appearing. Retry Logic only;
        // do not tear down a successfully bound persistent loader just because TaskSystem binding was early.
        _nextBindAttemptAt = Time.unscaledTime + BindRetryDelaySeconds;
    }

    public override void OnUpdate()
    {
        // Run persistent physical ownership before deciding whether this frame reloads Logic.
        _hostServices.LoadingRuntime.Update();

        if (HudToggleKeyPressed())
            _hudHidden = !_hudHidden;

        if (_reloader == null)
            return;

        if (_sceneBindPending)
        {
            TryActivateScene();
            return;
        }

        if (ReloadKeyPressed() || _reloader.CheckDllUpdated())
        {
            MelonLogger.Msg($"[{ReloadKeyName}] Hot reloading TaskSystem; loading transactions and FCS controls stay alive.");
            var reloaded = _reloader.Reload();
            _controlPanel?.ResetTransientInputs();
            if (reloaded)
                _controlPanel?.Activate();
            return;
        }

        try { _reloader.Current?.Update(); }
        catch (Exception ex) { MelonLogger.Error($"Logic.Update() exception: {ex}"); }

        _controlPanel?.Update();
    }

    public override void OnGUI()
    {
        if (_hudHidden || _reloader?.Current == null)
            return;

        try { _reloader.Current.OnGui(); }
        catch (Exception ex) { MelonLogger.Error($"Logic.OnGui() exception: {ex}"); }
    }

    private void ShutdownRuntime()
    {
        if (_shutdownStarted)
            return;

        _shutdownStarted = true;
        _sceneBindPending = false;

        // OnApplicationQuit runs before Unity tears down scene objects. Release TaskSystem and persistent
        // physical-click ownership here while LookAtTarget components are still valid. OnDeinitializeMelon
        // remains a fallback for non-application unload paths and is guarded against double disposal.
        _reloader?.Unload();
        _reloader = null;
        _controlPanel?.Dispose();
        _controlPanel = null;
        _hostServices.ControlUiRuntime = null;
        _hostServices.LoadingRuntime.Dispose();
    }

    public override void OnApplicationQuit()
    {
        ShutdownRuntime();
    }

    public override void OnDeinitializeMelon()
    {
        ShutdownRuntime();
    }

    private sealed class FcsHostServices : IFcsHostServices
    {
        internal PersistentLoadingSystem LoadingRuntime { get; } = new();
        internal IFcsControlUi? ControlUiRuntime { get; set; }

        public ILoadingSystem Loading => LoadingRuntime;
        public IFcsControlUi ControlUi => ControlUiRuntime
            ?? throw new InvalidOperationException("FCS control UI is not initialized.");
    }
}
