using ShiKe.Runtime;

namespace ShiKe.Services;

/// <summary>
/// 洗装的 Windows 编排边界。规则和模式循环在 Core，系统输入与剪贴板仍由桌面层执行；
/// 编排器只取得自身需要的能力，不接触页面、注册表或配置写入服务。
/// </summary>
public interface ICraftEnvironment : IAutomationRuntime
{
    InputSimulator Input { get; }
    ForegroundDetector Foreground { get; }
    string DataDirectory { get; }
    void PlayCompletionSound(string name);
    void ShowCompletionNotification(string message);
}
