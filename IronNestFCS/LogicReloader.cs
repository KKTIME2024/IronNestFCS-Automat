using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using IronNestFCS.Abstractions;
using MelonLoader;

namespace IronNestFCS;

/// <summary>
/// 负责把 Logic 程序集加载进一个可回收的 AssemblyLoadContext，
/// 并在需要时卸载 + 重新加载，实现热重载。
/// </summary>
internal sealed class LogicReloader
{
    private readonly string logicDllPath;
    private readonly string logicTypeName;

    private AssemblyLoadContext? alc;
    private IFcsModule? current;
    private WeakReference? alcWeakRef;

    public IFcsModule? Current => current;
    public bool CurrentInitialized { get; private set; }

    private DateTime lastWriteTime;
    private string[] excludedTargets = Array.Empty<string>();
    private readonly ReloadFlightState reloadFlightState = new();

    public LogicReloader(string logicDllPath, string logicTypeName)
    {
        this.logicDllPath = logicDllPath;
        this.logicTypeName = logicTypeName;
    }


    /// <summary>检测Logic文件是否有更新</summary>
    public bool CheckDllUpdated()
    {
        return current != null && lastWriteTime != File.GetLastWriteTime(logicDllPath);
    }

    /// <summary>卸载当前 Logic（若有），从磁盘重新加载并初始化。</summary>
    public bool Reload(bool newScene = false)
    {
        reloadFlightState.BeginReload(CurrentInitialized, newScene);
        Unload();
        if (newScene)
        {
            excludedTargets = Array.Empty<string>();
        }

        if (!File.Exists(logicDllPath))
        {
            MelonLogger.Error($"[Reload] Logic dll doesn't exist.: {logicDllPath}");
            return false;
        }

        try
        {
            alc = new AssemblyLoadContext("IronNestFCS.Logic", isCollectible: true);
            alcWeakRef = new WeakReference(alc, trackResurrection: true);

            // 从内存字节加载，避免锁住磁盘上的 dll（Rider 重编译时需要能覆盖它）。
            byte[] bytes = File.ReadAllBytes(logicDllPath);
            string pdbPath = Path.ChangeExtension(logicDllPath, ".pdb");
            Assembly asm;
            using (var dllStream = new MemoryStream(bytes))
            {
                if (File.Exists(pdbPath))
                {
                    using var pdbStream = new MemoryStream(File.ReadAllBytes(pdbPath));
                    asm = alc.LoadFromStream(dllStream, pdbStream);
                }
                else
                {
                    asm = alc.LoadFromStream(dllStream);
                }
            }

            Type? type = asm.GetType(logicTypeName);
            if (type == null)
            {
                MelonLogger.Error($"[Reload] Can't find type {logicTypeName} in Logic assembly.");
                Unload();
                return false;
            }

            if (Activator.CreateInstance(type) is not IFcsModule module)
            {
                MelonLogger.Error($"[Reload] {logicTypeName} doesn't implement IFcsModule");
                Unload();
                return false;
            }

            current = module;
            if (module is IFcsSessionState session)
            {
                session.RestoreAutomaticExclusions(excludedTargets);
                session.RestoreReloadFlightRisk(reloadFlightState.Possible);
            }
            lastWriteTime = File.GetLastWriteTime(logicDllPath);
            var ok = current.Initialize();
            CurrentInitialized = ok;
            if (!ok)
            {
                MelonLogger.Warning("[Reload] Logic.Initialize() returns false。");
            }
            else
            {
                lastWriteTime = File.GetLastWriteTime(logicDllPath);
                MelonLogger.Msg("[Reload] Logic loaded and initialized successfully.");
            }
            return ok;
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[Reload] Load Logic failed: {ex}");
            Unload();
            return false;
        }
    }

    /// <summary>关闭当前 Logic 并卸载其 AssemblyLoadContext。</summary>
    public void Unload()
    {
        CurrentInitialized = false;
        if (current != null)
        {
            if (current is IFcsSessionState session)
            {
                try { excludedTargets = session.CaptureAutomaticExclusions(); }
                catch (Exception ex) { MelonLogger.Error($"[Reload] Capture session state failed: {ex}"); }
            }
            try { current.Shutdown(); }
            catch (Exception ex) { MelonLogger.Error($"[Reload] Logic.Shutdown() exception: {ex}"); }
            current = null;
        }

        if (alc != null)
        {
            try { alc.Unload(); }
            catch (Exception ex) { MelonLogger.Error($"[Reload] ALC.Unload() exception: {ex}"); }
            alc = null;
        }

        // 提示 GC 回收旧上下文。卸载是异步的——不强求立即完成，
        // 仅在诊断时观察 alcWeakRef.IsAlive。
        CollectOldContext();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CollectOldContext()
    {
        for (int i = 0; i < 2 && alcWeakRef is { IsAlive: true }; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
