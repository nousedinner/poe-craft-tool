namespace ShiKe.Services;

/// <summary>
/// 全局默认值单一来源（对齐 Python 版 storage.py load_settings 的 defaults）。
/// 各抽屉/宿主解析设置时引用这里，避免分散硬编码。
/// 注意：默认值以 storage.py 为准（用户铁律），非调用处 fallback。
/// </summary>
public static class SettingsDefaults
{
    // 热键（storage.py:106-123）
    public const string HotkeyStart = "F5";        // 启动洗装
    public const string HotkeyStop = "F6";         // 停止洗装
    public const string HotkeySetCoord = "F7";     // 坐标录制
    public const string ClickerHotkey = "F8";      // 连点器 Toggle
    public const string ClickerHoldHotkey = "F11"; // 连点器 Hold
    public const string KeyLoopHotkey = "F9";      // 按键循环
    public const string HideoutHotkey = "F2";      // 一键回城

    // 洗装
    public const int DelayMs = 33;                            // 通货点击后首次服务器同步等待（10-200ms）
    public const bool SoundEnabled = true;
    public const bool PopupEnabled = true;
    public const int ClipboardUnchangedThreshold = 10;        // 连续 N 次剪贴板相同 = 通货耗尽
    public const string SelectedSound = "default_ding.wav";   // 注意：sounds/ 实际是 mp3，走 stem 回退

    // 连点器
    public const int ClickerIntervalMs = 33;
    public const string ClickerButton = "left";               // "left" | "right"
    public const string ClickerMode = "toggle";               // "toggle" | "hold"

    // 按键循环
    // key_loop_slots: []（阶段4 解析）

    // 回城
    public const bool HideoutEnabled = false;
    public const string HideoutCommand = "/hideout";

    // 宿主
    public const string TargetProcess = "";
    public const bool AutoDetectPoe = true;

    // 坐标默认值：全部未设置（null），由抽屉声明槽位
}
