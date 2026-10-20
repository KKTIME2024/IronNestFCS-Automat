namespace IronNestFCS.Abstractions;

/// <summary>
/// Host 与可热重载的 Logic 程序集之间的契约。
/// 这个接口所在的程序集只加载一份（在默认/Host 上下文），
/// 因此它是唯一能安全跨 AssemblyLoadContext 边界传递的类型。
/// Logic 程序集实现它；Host 通过它驱动火控逻辑，而不直接引用 Logic 的具体类型。
/// </summary>
public interface IFcsModule
{
    /// <summary>重载后调用一次。返回 false 表示初始化失败（例如没找到目标物体）。</summary>
    bool Initialize();

    /// <summary>每帧调用，对应 MelonMod.OnUpdate。</summary>
    void Update();

    /// <summary>IMGUI 绘制回调，对应 MelonMod.OnGUI。在这里画按钮。</summary>
    void OnGui();

    /// <summary>卸载前调用：撤销 Harmony 补丁、注销回调、释放引用。</summary>
    void Shutdown();
}
