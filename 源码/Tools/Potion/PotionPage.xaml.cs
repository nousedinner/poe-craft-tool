using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ShiKe.Services;

namespace ShiKe.Tools.Potion;

public partial class PotionPage : UserControl
{
    private const string RecordingHint = "Esc 取消 · 全局热键已暂停";

    private readonly PotionTool _tool;
    private readonly ToggleButton[] _slotSwitches;
    private readonly Button[] _keyButtons;
    private int? _captureIndex;
    private bool _loading = true;
    private bool _inputMethodWasEnabled;
    private Button? _inputMethodTarget;
    private Window? _ownerWindow;

    public PotionPage(PotionTool tool)
    {
        _tool = tool;
        InitializeComponent();
        _slotSwitches = [Slot1Switch, Slot2Switch, Slot3Switch, Slot4Switch, Slot5Switch];
        _keyButtons = [Slot1KeyButton, Slot2KeyButton, Slot3KeyButton, Slot4KeyButton, Slot5KeyButton];
        RefreshSettingsPresentation();
        RefreshEnabledPresentation();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    internal void RefreshSettingsPresentation()
    {
        var hotkey = _tool.Hotkey;
        HotkeyHint.Text = string.IsNullOrWhiteSpace(hotkey)
            ? "尚未设置快捷键"
            : $"{HotkeySetting.Display(hotkey)} 触发已启用的药剂槽";
    }

    internal void RefreshEnabledPresentation()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => RefreshEnabledPresentation());
            return;
        }

        _loading = true;
        try
        {
            var capturing = _captureIndex is not null;
            var canEditSlots = !_tool.IsRunning;
            EnableToolSwitch.IsChecked = _tool.IsEnabled;
            EnableToolSwitch.IsEnabled = !capturing;
            for (var index = 0; index < _slotSwitches.Length; index++)
            {
                var key = _tool.SlotKey(index);
                var recording = _captureIndex == index;
                _slotSwitches[index].IsChecked = _tool.IsSlotEnabled(index);
                _slotSwitches[index].IsEnabled = canEditSlots && !capturing;
                _keyButtons[index].Content = recording ? "按一个键或鼠标侧键…"
                    : string.IsNullOrWhiteSpace(key) ? "点击设置" : HotkeySetting.Display(key);
                _keyButtons[index].IsEnabled = canEditSlots && (!capturing || recording);
            }
            CaptureHint.Text = capturing ? RecordingHint : string.Empty;
            CaptureHint.Visibility = capturing ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }
    }

    internal void CancelCapture(bool interactive)
    {
        _captureIndex = null;
        RestoreInputMethodAfterCapture();
        var issues = _tool.EndSlotCapture(notify: !interactive);
        RefreshEnabledPresentation();
        if (interactive && issues.Count > 0)
            ShowFailure("取消录制后恢复热键失败：\n" + string.Join("\n", issues));
    }

    private void EnableToolSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var result = _tool.SetEnabled(EnableToolSwitch.IsChecked == true);
        if (result.Success) return;
        RefreshEnabledPresentation();
        ShowFailure(result.Message);
    }

    private void SlotSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleButton slot || slot.Tag is not string rawIndex ||
            !int.TryParse(rawIndex, out var index)) return;
        try
        {
            _tool.SetSlotEnabled(index, slot.IsChecked == true);
        }
        catch (Exception error) when (error is StorageException or InvalidOperationException)
        {
            RefreshEnabledPresentation();
            ShowFailure(error.Message);
        }
    }

    private void CaptureKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string rawIndex ||
            !int.TryParse(rawIndex, out var index)) return;
        if (_captureIndex is not null)
        {
            CancelCapture(interactive: true);
            return;
        }
        if (_tool.TryBeginSlotCapture() is { } refusal)
        {
            ShowFailure(refusal);
            return;
        }

        _captureIndex = index;
        _inputMethodTarget = button;
        _inputMethodWasEnabled = InputMethod.GetIsInputMethodEnabled(button);
        InputMethod.SetIsInputMethodEnabled(button, false);
        if (button.Focus()) RefreshEnabledPresentation();
        else CancelCapture(interactive: true);
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_captureIndex is not { } index) return;
        var key = KeyboardKeyName.Resolve(e);
        e.Handled = true;
        if (key == Key.Escape)
        {
            CancelCapture(interactive: true);
            return;
        }
        if (key == Key.None)
        {
            Diag.Log($"[一键吃药] 槽位按键捕获未解析：raw={e.Key}, ime={e.ImeProcessedKey}, dead={e.DeadCharProcessedKey}");
            CaptureHint.Text = $"请切换英文输入模式后重试 · {RecordingHint}";
            return;
        }

        if (KeyboardKeyName.FromKey(key) is not { } name)
        {
            CaptureHint.Text = $"暂不支持 {key}，请换一个键 · {RecordingHint}";
            return;
        }
        CommitCapturedKey(index, name);
    }

    private void Page_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_captureIndex is not { } index) return;
        var vk = e.ChangedButton switch
        {
            MouseButton.XButton1 => MouseButtonKey.XButton1,
            MouseButton.XButton2 => MouseButtonKey.XButton2,
            _ => (ushort)0,
        };
        if (vk == 0) return;
        e.Handled = true;
        CommitCapturedKey(index, MouseButtonKey.KeyName(vk));
    }

    private void CommitCapturedKey(int index, string key)
    {
        _captureIndex = null;
        RestoreInputMethodAfterCapture();
        string? failure = null;
        try { _tool.SetSlotKey(index, key); }
        catch (Exception error) { failure = error.Message; }
        var restoreIssues = _tool.EndSlotCapture(notify: false);
        RefreshEnabledPresentation();
        if (failure is not null)
            ShowFailure(restoreIssues.Count == 0 ? failure : failure + "\n\n" + string.Join("\n", restoreIssues));
        else if (restoreIssues.Count > 0)
            ShowFailure("录制完成，但恢复热键失败：\n" + string.Join("\n", restoreIssues));
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow is not null) return;
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is null) return;
        _ownerWindow.Deactivated += (_, _) => CancelCapture(interactive: false);
        _ownerWindow.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is false) CancelCapture(interactive: false);
        };
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => CancelCapture(interactive: false);

    private void Page_IsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && _captureIndex is not null) CancelCapture(interactive: false);
    }

    private void RestoreInputMethodAfterCapture()
    {
        if (_inputMethodTarget is null) return;
        InputMethod.SetIsInputMethodEnabled(_inputMethodTarget, _inputMethodWasEnabled);
        _inputMethodTarget = null;
    }

    private void ShowFailure(string message)
    {
        var owner = Window.GetWindow(this);
        if (owner is null)
            ThemedMessageBox.Show(message, "一键吃药设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            ThemedMessageBox.Show(owner, message, "一键吃药设置", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
