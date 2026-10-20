namespace IronNestFCS.Abstractions;

/// <summary>只传值数据；宿主在同一场景热重载间保留明确取消的目标排除。</summary>
public interface IFcsSessionState
{
    string[] CaptureAutomaticExclusions();
    void RestoreAutomaticExclusions(string[] ids);
    void RestoreReloadFlightRisk(bool possible);
}
