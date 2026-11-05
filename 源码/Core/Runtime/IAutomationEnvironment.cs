using ShiKe.Tools.Clicker;

namespace ShiKe.Runtime;

/// <summary>运行引擎所需的取消和诊断边界；不拥有宿主、窗口或系统输入服务。</summary>
public interface IAutomationRuntime
{
    CancellationToken EmergencyToken { get; }
    void ResetEmergencyStop();
    void Log(string message);
}

/// <summary>连点引擎请求的实际系统动作，由桌面适配层或无输入测试实现。</summary>
public interface IClickerEnvironment : IAutomationRuntime
{
    bool IsTargetForeground();
    Task ClickAsync(ClickerMouseButton button, CancellationToken token);
    void ReleaseMouseButtons();
}

/// <summary>按键循环请求的实际系统动作与平台按键校验。</summary>
public interface IKeyLoopEnvironment : IAutomationRuntime
{
    bool IsTargetForeground();
    bool IsSupportedKey(string key);
    Task PressAndReleaseAsync(string key, CancellationToken token);
}
