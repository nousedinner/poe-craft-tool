using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShiKe.Services;

namespace ShiKe.Tools.Settings;

public partial class SettingsPage : UserControl
{
    private const string StatisticsUrl = "https://open.cancanneed.top/share/poe-craft-tool-5c9767d3";

    private readonly SettingsTool _tool;
    private readonly Dictionary<string, Button> _hotkeyButtons;
    private readonly Dictionary<string, string> _hotkeyValues = new(StringComparer.OrdinalIgnoreCase);
    private string? _capturing;
    private bool _capturedMainKey;
    private bool _refreshing;
    private Window? _ownerWindow;

    private sealed record NamedOption(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    public SettingsPage(SettingsTool tool)
    {
        _tool = tool;
        InitializeComponent();
        _hotkeyButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase)
        {
            ["craft_start"] = CraftStartButton,
            ["craft_stop"] = CraftStopButton,
            ["coordinate"] = CoordinateButton,
            ["clicker_toggle"] = ClickerToggleButton,
            ["clicker_hold"] = ClickerHoldButton,
            ["keyloop"] = KeyLoopButton,
            ["hideout"] = HideoutButton,
        };
        RefreshFromTool(refreshLists: true);
        Loaded += SettingsPage_Loaded;
    }

    internal void ActivatePage()
    {
        RefreshFromTool(refreshLists: true);
        HideResult();
    }

    internal void DeactivatePage()
    {
        CancelCapture();
        HideResult();
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow is not null) return;
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is null) return;
        _ownerWindow.Deactivated += (_, _) => CancelCaptureBecauseWindowInactive();
        _ownerWindow.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is false) CancelCaptureBecauseWindowInactive();
        };
    }

    private void CancelCaptureBecauseWindowInactive()
    {
        if (_capturing is null) return;
        var restoreIssues = CancelCapture();
        if (restoreIssues.Count == 0)
            HideResult();
        else
            ShowResult("热键捕获中断后恢复失败：\n" + string.Join("\n", restoreIssues), success: false);
    }

    internal void RefreshFromTool(bool refreshLists)
    {
        _refreshing = true;
        try
        {
            var draft = _tool.CaptureDraft();
            SetHotkey("craft_start", draft.Hotkeys.CraftStart);
            SetHotkey("craft_stop", draft.Hotkeys.CraftStop);
            SetHotkey("coordinate", draft.Hotkeys.Coordinate);
            SetHotkey("clicker_toggle", draft.Hotkeys.ClickerToggle);
            SetHotkey("clicker_hold", draft.Hotkeys.ClickerHold);
            SetHotkey("keyloop", draft.Hotkeys.KeyLoop);
            SetHotkey("hideout", draft.Hotkeys.Hideout);

            if (refreshLists)
            {
                PopulateProcesses(draft.TargetProcess);
                PopulateSounds(draft.SelectedSound);
            }
            else
            {
                SelectOption(ProcessCombo, draft.TargetProcess);
                SelectOption(SoundCombo, draft.SelectedSound);
            }

            AutoDetectCheck.IsChecked = draft.AutoDetectPoe;
            CraftSoundCheck.IsChecked = draft.CraftSoundEnabled;
            CraftPopupCheck.IsChecked = draft.CraftPopupEnabled;
            ClickerNotificationCheck.IsChecked = draft.ClickerNotificationsEnabled;
            KeyLoopNotificationCheck.IsChecked = draft.KeyLoopNotificationsEnabled;
            CancelCapture();
        }
        finally
        {
            _refreshing = false;
        }
    }

    internal void ShowSoundStatus(SoundPlaybackStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowSoundStatus(status));
            return;
        }
        ShowResult(status.Message, status.Success);
    }

    private void SetHotkey(string id, string value)
    {
        _hotkeyValues[id] = value;
        _hotkeyButtons[id].Content = HotkeySetting.Display(value);
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        var restoreIssues = CancelCapture();
        if (restoreIssues.Count > 0)
        {
            var message = "结束捕获后恢复热键失败：\n" + string.Join("\n", restoreIssues);
            ShowResult(message, success: false);
            ShowFailureDialog(message);
            return;
        }

        var result = _tool.ApplyHotkey(id, string.Empty);
        ShowResult(result.Message, result.Success);
        if (!result.Success)
        {
            RefreshFromTool(refreshLists: false);
            ShowFailureDialog(result.Message);
        }
    }

    private void BeginHotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        CancelCapture();
        var begin = _tool.BeginHotkeyCapture();
        if (!begin.Success)
        {
            ShowResult(begin.Message, success: false);
            ShowFailureDialog(begin.Message);
            return;
        }
        _capturing = id;
        _capturedMainKey = false;
        button.Content = "请按新热键…";
        button.BorderBrush = Brushes.Orange;
        ShowCaptureStatus();
        Focus();
        Keyboard.Focus(this);
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is null) return;
        var key = ResolveKey(e);
        if (key == Key.Escape)
        {
            var restoreIssues = CancelCapture();
            if (restoreIssues.Count == 0)
                ShowResult("已取消热键修改", success: true);
            else
                ShowResult("取消录入后恢复热键失败：\n" + string.Join("\n", restoreIssues), success: false);
            e.Handled = true;
            return;
        }
        if (IsModifier(key))
        {
            e.Handled = true;
            return;
        }

        var value = FormatHotkey(key, Keyboard.Modifiers);
        if (value is null)
        {
            ShowResult("这个按键暂不支持，请换一个按键", success: false);
            e.Handled = true;
            return;
        }

        _capturedMainKey = true;
        CompleteHotkeyCapture(value);
        e.Handled = true;
    }

    private void Page_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_capturing is null || _capturedMainKey) return;
        var key = ResolveKey(e);
        if (!IsModifier(key)) return;
        if (!string.Equals(_capturing, "clicker_hold", StringComparison.OrdinalIgnoreCase))
        {
            ShowResult("这个热键还需要一个主键，例如 Ctrl+F8", success: false);
            e.Handled = true;
            return;
        }

        var value = ModifierName(key);
        if (value is not null)
            CompleteHotkeyCapture(value);
        e.Handled = true;
    }

    private void CompleteHotkeyCapture(string value)
    {
        var id = _capturing!;
        var previousValue = _hotkeyValues.GetValueOrDefault(id, string.Empty);
        var valueChanged = !string.Equals(previousValue, value, StringComparison.OrdinalIgnoreCase);
        _capturing = null;
        _hotkeyValues[id] = value;
        _hotkeyButtons[id].Content = HotkeySetting.Display(value);
        _hotkeyButtons[id].BorderBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0xD0, 0xE2));

        SettingsApplyResult result;
        try
        {
            result = _tool.ApplyHotkey(id, value);
        }
        catch (Exception ex)
        {
            result = SettingsApplyResult.Fail($"热键设置发生异常：{ex.Message}");
        }

        var resumeIssues = _tool.EndHotkeyCapture(
            hotkeysAlreadyRegistered: result.Success && valueChanged);
        if (resumeIssues.Count > 0)
            result = SettingsApplyResult.Fail("热键恢复失败：\n" + string.Join("\n", resumeIssues));

        ShowResult(result.Message, result.Success);
        if (!result.Success)
        {
            RefreshFromTool(refreshLists: false);
            ShowFailureDialog(result.Message);
        }
    }

    private IReadOnlyList<string> CancelCapture()
    {
        var wasCapturing = _capturing is not null;
        if (wasCapturing && _hotkeyButtons.TryGetValue(_capturing!, out var button))
        {
            button.Content = HotkeySetting.Display(_hotkeyValues.GetValueOrDefault(_capturing!, string.Empty));
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0xD0, 0xE2));
        }
        _capturing = null;
        _capturedMainKey = false;
        return wasCapturing
            ? _tool.EndHotkeyCapture(hotkeysAlreadyRegistered: false)
            : [];
    }

    private SettingsDraft BuildDraft()
    {
        var current = _tool.CaptureDraft();
        return current with
        {
            Hotkeys = new HotkeySettings(
                _hotkeyValues["craft_start"],
                _hotkeyValues["craft_stop"],
                _hotkeyValues["coordinate"],
                _hotkeyValues["clicker_toggle"],
                _hotkeyValues["clicker_hold"],
                _hotkeyValues["keyloop"],
                _hotkeyValues["hideout"]),
            TargetProcess = SelectedValue(ProcessCombo),
            AutoDetectPoe = AutoDetectCheck.IsChecked == true,
            CraftSoundEnabled = CraftSoundCheck.IsChecked == true,
            CraftPopupEnabled = CraftPopupCheck.IsChecked == true,
            SelectedSound = SelectedValue(SoundCombo),
            ClickerNotificationsEnabled = ClickerNotificationCheck.IsChecked == true,
            KeyLoopNotificationsEnabled = KeyLoopNotificationCheck.IsChecked == true,
        };
    }

    private void RealtimeSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        CancelCapture();
        var result = _tool.ApplyDraft(BuildDraft());
        ShowResult(result.Message, result.Success);
        if (!result.Success)
        {
            RefreshFromTool(refreshLists: false);
            ShowFailureDialog(result.Message);
        }
    }

    private void RestoreDefaultHotkeys_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        var defaults = new HotkeySettings(
            SettingsDefaults.HotkeyStart,
            SettingsDefaults.HotkeyStop,
            SettingsDefaults.HotkeySetCoord,
            SettingsDefaults.ClickerHotkey,
            SettingsDefaults.ClickerHoldHotkey,
            SettingsDefaults.KeyLoopHotkey,
            SettingsDefaults.HideoutHotkey);
        var result = _tool.ApplyDraft(_tool.CaptureDraft() with { Hotkeys = defaults });
        ShowResult(result.Success ? "默认热键已恢复并立即生效" : result.Message, result.Success);
        RefreshFromTool(refreshLists: false);
        if (!result.Success) ShowFailureDialog(result.Message);
    }

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        _refreshing = true;
        try { PopulateProcesses(SelectedValue(ProcessCombo)); }
        finally { _refreshing = false; }
        ShowResult("进程列表已刷新", success: true);
    }

    private void RefreshSounds_Click(object sender, RoutedEventArgs e)
    {
        _refreshing = true;
        try { PopulateSounds(SelectedValue(SoundCombo)); }
        finally { _refreshing = false; }
        ShowResult("音效列表已刷新", success: true);
    }

    private void PreviewSound_Click(object sender, RoutedEventArgs e)
    {
        var result = _tool.PreviewSound(SelectedValue(SoundCombo));
        ShowResult(result.Message, result.Success);
    }

    private void OpenSoundsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_tool.SoundDirectory);
            Process.Start(new ProcessStartInfo(_tool.SoundDirectory) { UseShellExecute = true });
            ShowResult("已打开音效目录", success: true);
        }
        catch (Exception ex)
        {
            ShowResult($"无法打开音效目录：{ex.Message}", success: false);
        }
    }

    private void OpenStatistics_Click(object sender, RoutedEventArgs e)
    {
        if (!BrowserLauncher.TryOpen(StatisticsUrl, out var error))
            ShowResult(error, success: false);
    }

    private void PopulateProcesses(string selected)
    {
        var values = _tool.GetRunningProcesses().ToList();
        if (!string.IsNullOrWhiteSpace(selected) && !values.Contains(selected, StringComparer.OrdinalIgnoreCase))
            values.Add(selected);
        values.Sort(StringComparer.OrdinalIgnoreCase);
        ProcessCombo.ItemsSource = new[] { new NamedOption("不选择（禁止自动输入）", string.Empty) }
            .Concat(values.Select(value => new NamedOption(value, value)))
            .ToList();
        SelectOption(ProcessCombo, selected);
    }

    private void PopulateSounds(string selected)
    {
        var values = _tool.GetSounds().ToList();
        if (!string.IsNullOrWhiteSpace(selected) && !values.Contains(selected, StringComparer.OrdinalIgnoreCase))
            values.Add(selected);
        values.Sort(StringComparer.OrdinalIgnoreCase);
        SoundCombo.ItemsSource = values.Select(value => new NamedOption(value, value)).ToList();
        SelectOption(SoundCombo, selected);
        if (SoundCombo.SelectedIndex < 0 && SoundCombo.Items.Count > 0)
            SoundCombo.SelectedIndex = 0;
    }

    private static void SelectOption(ComboBox combo, string value)
    {
        var option = combo.Items.Cast<NamedOption>()
            .FirstOrDefault(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase));
        if (option is not null) combo.SelectedItem = option;
    }

    private static string SelectedValue(ComboBox combo)
        => (combo.SelectedItem as NamedOption)?.Value ?? string.Empty;

    private void ShowResult(string message, bool success)
    {
        ResultPanel.Visibility = Visibility.Visible;
        ResultPanel.Background = new SolidColorBrush(success
            ? Color.FromRgb(0xEA, 0xF6, 0xEF)
            : Color.FromRgb(0xFD, 0xEC, 0xEE));
        ResultPanel.BorderBrush = new SolidColorBrush(success
            ? Color.FromRgb(0xB9, 0xDD, 0xC6)
            : Color.FromRgb(0xEF, 0xB9, 0xC0));
        ResultText.Text = message;
        ResultText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(0x1B, 0x8A, 0x3E)
            : Color.FromRgb(0xD0, 0x45, 0x55));
    }

    private void ShowCaptureStatus()
    {
        ResultPanel.Visibility = Visibility.Visible;
        ResultPanel.Background = new SolidColorBrush(Color.FromRgb(0xEA, 0xF3, 0xFA));
        ResultPanel.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xE4, 0xF0));
        ResultText.Text = "正在捕获热键；按 Esc 取消";
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x6B, 0x2D));
    }

    private void HideResult()
    {
        ResultPanel.Visibility = Visibility.Collapsed;
        ResultText.Text = string.Empty;
    }

    private void ShowFailureDialog(string message)
    {
        var owner = Window.GetWindow(this);
        if (owner is null)
            MessageBox.Show(message, "设置未生效", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(owner, message, "设置未生效", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static Key ResolveKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    private static bool IsModifier(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string? ModifierName(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LWin or Key.RWin => "Win",
        _ => null,
    };

    private static string? FormatHotkey(Key key, ModifierKeys modifiers)
    {
        var main = KeyName(key);
        if (main is null) return null;
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(main);
        return string.Join('+', parts);
    }

    private static string? KeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString().ToUpperInvariant();
        if (key is >= Key.D0 and <= Key.D9) return ((int)key - (int)Key.D0).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((int)key - (int)Key.NumPad0).ToString();
        if (key is >= Key.F1 and <= Key.F24) return key.ToString().ToUpperInvariant();
        return key switch
        {
            Key.Enter or Key.Return => "Enter",
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Escape => "Esc",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.Oem6 => "]",
            Key.Oem5 => "\\",
            Key.Oem1 => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Oem2 => "/",
            _ => null,
        };
    }
}
