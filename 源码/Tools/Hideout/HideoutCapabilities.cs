using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Hideout;

public sealed partial class HideoutTool : IToolRuntime, IEditableHotkeyProvider
{
    bool IToolRuntime.IsRunning { get { lock (_operationGate) return _operationTask is { IsCompleted: false }; } }
    bool IToolRuntime.ShutdownCompleted => ShutdownCompleted;
    void IToolRuntime.RequestStop() => CancelCurrentOperation();
    void IEditableHotkeyProvider.RefreshSettingsPresentation() => _page?.RefreshFromTool();

    public IReadOnlyList<EditableHotkey> GetEditableHotkeys() =>
    [
        new("hideout", "一键回城", HotkeyMode.Toggle, SettingsDefaults.HideoutHotkey,
            () => _hotkey, key => _hotkey = key, 70),
    ];
}
