using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Clicker;

public sealed partial class ClickerTool : IToolRuntime, IEditableHotkeyProvider
{
    bool IToolRuntime.ShutdownCompleted => ShutdownCompleted;
    void IToolRuntime.RequestStop() => Stop();
    void IEditableHotkeyProvider.RefreshSettingsPresentation() => RefreshSettingsPresentation();

    public IReadOnlyList<EditableHotkey> GetEditableHotkeys() =>
    [
        new("clicker_toggle", "连点器切换", HotkeyMode.Toggle, SettingsDefaults.ClickerHotkey,
            () => Hotkey, key => Hotkey = key, 40),
        new("clicker_hold", "连点器按住", HotkeyMode.Hold, SettingsDefaults.ClickerHoldHotkey,
            () => HoldHotkey, key => HoldHotkey = key, 50),
    ];
}
