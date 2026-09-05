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
    }

    internal void RefreshFromTool(bool refreshLists)
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
        HideoutEnableCheck.IsChecked = draft.HideoutEnabled;
        HideoutCommandText.Text = draft.HideoutCommand;
        CancelCapture();
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
        _hotkeyButtons[id].Content = value;
    }

    private void BeginHotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        CancelCapture();
        _capturing = id;
        _capturedMainKey = false;
        button.Content = "请按新热键…";
        button.BorderBrush = Brushes.Orange;
        ResultText.Text = "正在捕获热键；按 Esc 取消";
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x6B, 0x2D));
        Focus();
        Keyboard.Focus(this);
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is null) return;
        var key = ResolveKey(e);
        if (key == Key.Escape)
        {
            CancelCapture();
            ShowResult("已取消热键修改", success: true);
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
        _capturing = null;
        _hotkeyValues[id] = value;
        _hotkeyButtons[id].Content = value;
        _hotkeyButtons[id].BorderBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0xD0, 0xE2));

        var result = _tool.ApplyDraft(BuildDraft());
        ShowResult(result.Message, result.Success);
        if (!result.Success)
            RefreshFromTool(refreshLists: false);
    }

    private void CancelCapture()
    {
        if (_capturing is not null && _hotkeyButtons.TryGetValue(_capturing, out var button))
        {
            button.Content = _hotkeyValues.GetValueOrDefault(_capturing, "未设置");
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0xD0, 0xE2));
        }
        _capturing = null;
        _capturedMainKey = false;
    }

    private SettingsDraft BuildDraft()
    {
        return new SettingsDraft(
            new HotkeySettings(
                _hotkeyValues["craft_start"],
                _hotkeyValues["craft_stop"],
                _hotkeyValues["coordinate"],
                _hotkeyValues["clicker_toggle"],
                _hotkeyValues["clicker_hold"],
                _hotkeyValues["keyloop"],
                _hotkeyValues["hideout"]),
            SelectedValue(ProcessCombo),
            AutoDetectCheck.IsChecked == true,
            CraftSoundCheck.IsChecked == true,
            CraftPopupCheck.IsChecked == true,
            SelectedValue(SoundCombo),
            ClickerNotificationCheck.IsChecked == true,
            KeyLoopNotificationCheck.IsChecked == true,
            HideoutEnableCheck.IsChecked == true,
            HideoutCommandText.Text);
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        var result = _tool.ApplyDraft(BuildDraft());
        ShowResult(result.Message, result.Success);
        RefreshFromTool(refreshLists: false);
    }

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        PopulateProcesses(SelectedValue(ProcessCombo));
        ShowResult("进程列表已刷新", success: true);
    }

    private void RefreshSounds_Click(object sender, RoutedEventArgs e)
    {
        PopulateSounds(SelectedValue(SoundCombo));
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
        try
        {
            Process.Start(new ProcessStartInfo(StatisticsUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowResult($"无法打开统计页面：{ex.Message}", success: false);
        }
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
        ResultText.Text = message;
        ResultText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(0x1B, 0x8A, 0x3E)
            : Color.FromRgb(0xD0, 0x45, 0x55));
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
