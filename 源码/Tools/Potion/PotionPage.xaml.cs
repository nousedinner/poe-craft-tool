using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShiKe.Services;

namespace ShiKe.Tools.Potion;

public partial class PotionPage : UserControl
{
    private readonly PotionTool _tool;
    private readonly CheckBox[] _slotChecks;
    private readonly Button[] _keyButtons;
    private readonly Button[] _clearButtons;
    private int? _captureIndex;
    private bool _loading = true;
    private bool _inputMethodStateSaved;
    private bool _inputMethodWasEnabled;
    private Button? _inputMethodTarget;
    private Window? _ownerWindow;

    public PotionPage(PotionTool tool)
    {
        _tool = tool;
        InitializeComponent();
        _slotChecks = [Slot1Check, Slot2Check, Slot3Check, Slot4Check, Slot5Check];
        _keyButtons = [Slot1KeyButton, Slot2KeyButton, Slot3KeyButton, Slot4KeyButton, Slot5KeyButton];
        _clearButtons = [Slot1ClearButton, Slot2ClearButton, Slot3ClearButton, Slot4ClearButton, Slot5ClearButton];
        RefreshHotkeyPresentation();
        RefreshEnabledPresentation();
        _loading = false;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    internal void RefreshHotkeyPresentation()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => RefreshHotkeyPresentation());
            return;
        }
        HotkeyHint.Text = string.IsNullOrWhiteSpace(_tool.Hotkey)
            ? "尚未设置快捷键"
            : $"{HotkeySetting.Display(_tool.Hotkey)} 一次触发所选药剂槽";
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
            EnableToolSwitch.IsChecked = _tool.IsEnabled;
            EnableToolSwitch.IsEnabled = _captureIndex is null;
            for (var index = 0; index < _slotChecks.Length; index++)
            {
                _slotChecks[index].IsChecked = _tool.IsSlotEnabled(index);
                _keyButtons[index].Content = _captureIndex == index
                    ? "请按键或侧键（Esc取消）"
                    : string.IsNullOrWhiteSpace(_tool.SlotKey(index))
                        ? "点击设置"
                        : HotkeySetting.Display(_tool.SlotKey(index));
                _slotChecks[index].IsEnabled = !_tool.IsRunning && _captureIndex is null;
                _keyButtons[index].IsEnabled = !_tool.IsRunning && (_captureIndex is null || _captureIndex == index);
                _clearButtons[index].IsEnabled = !_tool.IsRunning && _captureIndex is null;
            }
            StatusText.Text = _captureIndex is { } capture
                ? $"正在录制药剂槽 {capture + 1} 按键：Esc 取消；全局热键已暂停"
                : _tool.IsRunning ? "一键吃药执行中…"
                : _tool.IsEnabled ? "一键吃药已启用" : "一键吃药已停用";
        }
        finally
        {
            _loading = false;
        }
    }

    internal void RefreshRunningPresentation() => RefreshEnabledPresentation();

    internal void CancelCapture(bool interactive)
    {
        var index = _captureIndex;
        _captureIndex = null;
        RestoreInputMethodAfterCapture();
        if (index is { } captured) RefreshKeyButton(captured);
        var issues = _tool.EndSlotCapture(notify: !interactive);
        RefreshEnabledPresentation();
        if (interactive && issues.Count > 0)
            ShowFailure("取消录制后恢复热键失败：\n" + string.Join("\n", issues));
    }

    private void EnableToolSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var result = _tool.SetEnabled(EnableToolSwitch.IsChecked == true);
        RefreshEnabledPresentation();
        if (!result.Success) ShowFailure(result.Message);
    }

    private void SlotCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox check || check.Tag is not string rawIndex ||
            !int.TryParse(rawIndex, out var index)) return;
        try
        {
            _tool.SetSlotEnabled(index, check.IsChecked == true);
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
        CancelCapture(interactive: true);
        if (_tool.TryBeginSlotCapture() is { } refusal)
        {
            ShowFailure(refusal);
            return;
        }

        _captureIndex = index;
        _inputMethodTarget = button;
        _inputMethodWasEnabled = InputMethod.GetIsInputMethodEnabled(button);
        _inputMethodStateSaved = true;
        InputMethod.SetIsInputMethodEnabled(button, false);
        button.Content = "请按键或侧键（Esc取消）";
        button.Focus();
        RefreshEnabledPresentation();
        button.Focus();
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string rawIndex ||
            !int.TryParse(rawIndex, out var index)) return;
        CancelCapture(interactive: false);
        try
        {
            _tool.SetSlotKey(index, string.Empty);
            RefreshEnabledPresentation();
        }
        catch (Exception error) when (error is StorageException or InvalidOperationException)
        {
            RefreshEnabledPresentation();
            ShowFailure(error.Message);
        }
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
            StatusText.Text = "输入法仍拦截了此键，请切换英文输入模式后重试";
            return;
        }

        if (KeyboardKeyName.FromKey(key) is not { } name)
        {
            ShowFailure($"暂不支持录制此按键：{key}（VK 0x{KeyInterop.VirtualKeyFromKey(key):X2}）");
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

    private void RefreshKeyButton(int index)
        => _keyButtons[index].Content = string.IsNullOrWhiteSpace(_tool.SlotKey(index))
            ? "点击设置"
            : HotkeySetting.Display(_tool.SlotKey(index));

    private void RestoreInputMethodAfterCapture()
    {
        if (!_inputMethodStateSaved || _inputMethodTarget is null) return;
        InputMethod.SetIsInputMethodEnabled(_inputMethodTarget, _inputMethodWasEnabled);
        _inputMethodStateSaved = false;
        _inputMethodTarget = null;
    }

    private void ShowFailure(string message)
    {
        var owner = Window.GetWindow(this);
        if (owner is null)
            ThemedMessageBox.Show(message, "一键吃药未生效", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            ThemedMessageBox.Show(owner, message, "一键吃药未生效", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
