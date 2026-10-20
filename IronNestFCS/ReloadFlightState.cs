namespace IronNestFCS;

/// <summary>新场景没有旧逻辑的在飞历史；同场景重载不能把丢失的历史当作零发。</summary>
internal sealed class ReloadFlightState
{
    public bool Possible { get; private set; }

    public void BeginReload(bool hasPreviousModule, bool newScene)
    {
        Possible = !newScene && (Possible || hasPreviousModule);
    }
}
