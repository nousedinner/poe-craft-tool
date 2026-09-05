using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ShiKe.Tools.KeyLoop;

public partial class KeyLoopPage : UserControl
{
    private sealed class SlotRow
    {
        public required Border Root { get; init; }
        public required CheckBox Enabled { get; init; }
        public required Button KeyButton { get; init; }
        public required TextBox Delay { get; init; }
        public required TextBlock Count { get; init; }
    }

    private readonly KeyLoopTool _tool;
    private readonly List<SlotRow> _rows = [];
    private int? _captureIndex;
    private bool _allSlotsShown;
    private bool _loading = true;

    public KeyLoopPage(KeyLoopTool tool)
    {
        _tool = tool;
        InitializeComponent();
        HotkeyText.Text = tool.Hotkey;
        BuildRows();
        ApplyStatus(new KeyLoopStatus(tool.IsRunning, Enumerable.Repeat(0, KeyLoopEngine.MaxSlots).ToArray(),
            tool.IsRunning ? "按键循环运行中..." : "按键循环就绪"));
        _loading = false;
        tool.StatusUpdated += status => Dispatcher.BeginInvoke(() => ApplyStatus(status));
    }

    private void BuildRows()
    {
        for (var index = 0; index < KeyLoopEngine.MaxSlots; index++)
        {
            var capturedIndex = index;
            var slot = _tool.Slots[index];
            var enabled = new CheckBox
            {
                IsChecked = slot.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "启用此槽位",
            };
            var keyButton = new Button
            {
                Content = string.IsNullOrWhiteSpace(slot.Key) ? "点击设置" : slot.Key,
                Width = 92,
                Height = 27,
                Margin = new Thickness(10, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x50, 0xA0)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x6F, 0xB5)),
                Cursor = Cursors.Hand,
            };
            var clearButton = new Button
            {
                Content = "×",
                Width = 25,
                Height = 25,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)),
                Cursor = Cursors.Hand,
            };
            var delay = new TextBox
            {
                Text = slot.DelaySeconds.ToString("0.0", CultureInfo.InvariantCulture),
                Width = 70,
                Height = 27,
                Margin = new Thickness(8, 0, 4, 0),
                Padding = new Thickness(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var count = new TextBlock
            {
                Text = "0",
                Width = 42,
                TextAlignment = TextAlignment.Right,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x6F, 0xB5)),
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = $"#{index + 1}", Width = 28, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x9A, 0xAA)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(enabled);
            row.Children.Add(keyButton);
            row.Children.Add(clearButton);
            row.Children.Add(new TextBlock { Text = "间隔", Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x77, 0x88)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            row.Children.Add(delay);
            row.Children.Add(new TextBlock { Text = "秒", Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x77, 0x88)), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = "次数", Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x99, 0xAA)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });
            row.Children.Add(count);

            var root = new Border
            {
                Child = row,
                Background = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0x00, 0x50, 0xA0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 5),
                Visibility = index < 5 ? Visibility.Visible : Visibility.Collapsed,
            };
            SlotPanel.Children.Add(root);
            _rows.Add(new SlotRow { Root = root, Enabled = enabled, KeyButton = keyButton, Delay = delay, Count = count });

            enabled.Checked += (_, _) => SaveRowChange();
            enabled.Unchecked += (_, _) => SaveRowChange();
            keyButton.Click += (_, _) => StartCapture(capturedIndex);
            clearButton.Click += (_, _) => ClearKey(capturedIndex);
            delay.LostFocus += (_, _) => CommitDelay(capturedIndex);
            delay.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                CommitDelay(capturedIndex);
                Keyboard.ClearFocus();
                e.Handled = true;
            };
        }
    }

    public void CollectSettingsFromUi()
    {
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            var slot = _tool.Slots[index];
            slot.Enabled = row.Enabled.IsChecked == true;
            slot.Key = row.KeyButton.Tag as string ?? slot.Key;
            if (TryParseDelay(row.Delay.Text, out var seconds))
                slot.DelaySeconds = Math.Clamp(seconds, 0.1, 999.0);
        }
    }

    private void SaveRowChange()
    {
        if (_loading) return;
        CollectSettingsFromUi();
        _tool.SaveSettingsToStorage();
    }

    private void StartCapture(int index)
    {
        if (_tool.IsRunning) return;
        if (_captureIndex is { } previous && previous != index)
            RefreshKeyButton(previous);
        _captureIndex = index;
        _rows[index].KeyButton.Content = "按下按键...";
        _rows[index].KeyButton.Focus();
    }

    private void ClearKey(int index)
    {
        if (_tool.IsRunning) return;
        _captureIndex = null;
        _tool.Slots[index].Key = string.Empty;
        _rows[index].KeyButton.Tag = string.Empty;
        RefreshKeyButton(index);
        SaveRowChange();
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_captureIndex is not { } index) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var name = KeyToName(key);
        e.Handled = true;
        if (name is null) return;

        _tool.Slots[index].Key = name;
        _rows[index].KeyButton.Tag = name;
        _captureIndex = null;
        RefreshKeyButton(index);
        SaveRowChange();
    }

    private void RefreshKeyButton(int index)
    {
        var key = _tool.Slots[index].Key;
        _rows[index].KeyButton.Tag = key;
        _rows[index].KeyButton.Content = string.IsNullOrWhiteSpace(key) ? "点击设置" : key;
    }

    private void CommitDelay(int index)
    {
        var row = _rows[index];
        if (!TryParseDelay(row.Delay.Text, out var seconds))
            seconds = _tool.Slots[index].DelaySeconds;
        seconds = Math.Clamp(seconds, 0.1, 999.0);
        _tool.Slots[index].DelaySeconds = seconds;
        row.Delay.Text = seconds.ToString("0.0", CultureInfo.InvariantCulture);
        SaveRowChange();
    }

    private static bool TryParseDelay(string text, out double seconds)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds) ||
           double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);

    private static string? KeyToName(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString().ToLowerInvariant();
        if (key is >= Key.D0 and <= Key.D9) return ((int)key - (int)Key.D0).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((int)key - (int)Key.NumPad0).ToString();
        if (key is >= Key.F1 and <= Key.F24) return key.ToString().ToUpperInvariant();
        return key switch
        {
            Key.LeftCtrl or Key.RightCtrl => "ctrl",
            Key.LeftAlt or Key.RightAlt => "alt",
            Key.LeftShift or Key.RightShift => "shift",
            Key.LWin or Key.RWin => "win",
            Key.Enter or Key.Return => "enter",
            Key.Space => "space",
            Key.Tab => "tab",
            Key.Escape => "esc",
            Key.Back => "backspace",
            Key.Delete => "delete",
            Key.Insert => "insert",
            Key.Home => "home",
            Key.End => "end",
            Key.PageUp => "pageup",
            Key.PageDown => "pagedown",
            Key.Up => "up",
            Key.Down => "down",
            Key.Left => "left",
            Key.Right => "right",
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

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        _allSlotsShown = !_allSlotsShown;
        for (var index = 5; index < _rows.Count; index++)
            _rows[index].Root.Visibility = _allSlotsShown ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Content = _allSlotsShown ? "▲ 收起 (10/10)" : "▼ 加载更多 (5/10)";
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        CollectSettingsFromUi();
        _tool.ToggleFromPage();
    }

    private void ApplyStatus(KeyLoopStatus status)
    {
        StatusText.Text = status.Text;
        StatusDot.Foreground = status.Running
            ? new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x3E))
            : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        ToggleButton.Content = status.Running ? "停止循环" : "启动循环";
        for (var index = 0; index < _rows.Count; index++)
        {
            _rows[index].Count.Text = index < status.PressCounts.Count ? status.PressCounts[index].ToString() : "0";
            _rows[index].Enabled.IsEnabled = !status.Running;
            _rows[index].KeyButton.IsEnabled = !status.Running;
            _rows[index].Delay.IsEnabled = !status.Running;
        }
    }
}
