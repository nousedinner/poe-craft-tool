using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.KeyLoop;

public sealed partial class KeyLoopTool : IToolRuntime, IEditableHotkeyProvider
{
    bool IToolRuntime.ShutdownCompleted => ShutdownCompleted;
    void IToolRuntime.RequestStop() => Stop();
    void IEditableHotkeyProvider.RefreshSettingsPresentation() => RefreshSettingsPresentation();

    public IReadOnlyList<EditableHotkey> GetEditableHotkeys() =>
    [
        new("keyloop", "按键循环", HotkeyMode.Toggle, SettingsDefaults.KeyLoopHotkey,
            () => Hotkey, key => Hotkey = key, 60),
    ];
}
