namespace ShiKe.Host;

/// <summary>热键触发模式。</summary>
public enum HotkeyMode
{
    /// <summary>RegisterHotKey：按下触发，再按停止。</summary>
    Toggle,

    /// <summary>WH_KEYBOARD_LL 低级钩子：按下开始，松开停止。</summary>
    Hold
}

/// <summary>
/// 抽屉声明的热键请求，由 HotkeyManager 统一注册。
/// 宿主保证 Handler 在 UI 线程调用。
/// </summary>
public sealed class HotkeyRequest
{
    /// <summary>键名，如 "F5"。</summary>
    public required string Key { get; init; }

    /// <summary>显示名，如 "启动洗装"。</summary>
    public required string DisplayName { get; init; }

    /// <summary>触发时是否检查前台进程（停止类热键 = false）。</summary>
    public required bool CheckForeground { get; init; }

    /// <summary>Toggle（普通）/ Hold（按住）。</summary>
    public required HotkeyMode Mode { get; init; }

    /// <summary>回调（宿主保证在 UI 线程调用）。</summary>
    public required Action Handler { get; init; }
}
