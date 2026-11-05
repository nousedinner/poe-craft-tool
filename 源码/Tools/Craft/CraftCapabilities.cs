using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Craft;

public sealed partial class CraftTool : IToolRuntime, IToolAuxiliaryState, IEditableHotkeyProvider
{
    bool IToolRuntime.ShutdownCompleted => _engine?.ShutdownCompleted ?? true;
    void IToolRuntime.RequestStop() => Stop();
    void IEditableHotkeyProvider.RefreshSettingsPresentation() => RefreshSettingsPresentation();

    public IReadOnlyList<EditableHotkey> GetEditableHotkeys() =>
    [
        new("craft_start", "启动洗装", HotkeyMode.Toggle, SettingsDefaults.HotkeyStart,
            () => HotkeyStart, key => HotkeyStart = key, 10),
        new("craft_stop", "停止洗装", HotkeyMode.Toggle, SettingsDefaults.HotkeyStop,
            () => HotkeyStop, key => HotkeyStop = key, 20),
    ];
}
