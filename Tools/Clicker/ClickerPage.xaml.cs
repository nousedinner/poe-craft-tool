using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ShiKe.Tools.Clicker;

public partial class ClickerPage : UserControl
{
    private readonly ClickerTool _tool;
    private bool _loading = true;

    public ClickerPage(ClickerTool tool)
    {
        _tool = tool;
        InitializeComponent();

        LeftButtonRadio.IsChecked = tool.MouseButton == ClickerMouseButton.Left;
        RightButtonRadio.IsChecked = tool.MouseButton == ClickerMouseButton.Right;
        IntervalSlider.Value = tool.IntervalMs;
        IntervalValue.Text = $"{tool.IntervalMs} ms";
        HotkeyHint.Text = $"切换热键：{tool.Hotkey}    按住热键：{tool.HoldHotkey}";
        ApplyStatus(new ClickerStatus(tool.IsRunning, tool.ClickCount,
            tool.IsRunning ? "连点中..." : "连点器就绪"));
        _loading = false;

        tool.StatusUpdated += status => Dispatcher.BeginInvoke(() => ApplyStatus(status));
    }

    public void CollectSettingsFromUi()
    {
        _tool.MouseButton = RightButtonRadio.IsChecked == true
            ? ClickerMouseButton.Right
            : ClickerMouseButton.Left;
        _tool.IntervalMs = Math.Clamp((int)IntervalSlider.Value, 10, 200);
    }

    internal void RefreshSettingsPresentation()
    {
        HotkeyHint.Text = $"切换热键：{_tool.Hotkey}    按住热键：{_tool.HoldHotkey}";
    }

    private void MouseButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        CollectSettingsFromUi();
        _tool.SaveSettingsToStorage();
    }

    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IntervalValue is null) return;
        IntervalValue.Text = $"{(int)e.NewValue} ms";
        if (!_loading) _tool.IntervalMs = Math.Clamp((int)e.NewValue, 10, 200);
    }

    private void IntervalSlider_Commit(object sender, MouseButtonEventArgs e)
    {
        if (_loading) return;
        CollectSettingsFromUi();
        _tool.SaveSettingsToStorage();
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _tool.ToggleFromPage();
    }

    private void ApplyStatus(ClickerStatus status)
    {
        StatusText.Text = status.Text;
        ClickCountText.Text = status.ClickCount.ToString();
        StatusDot.Foreground = status.Running
            ? new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x3E))
            : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        ToggleButton.Content = status.Running ? "停止连点" : "启动连点";
        LeftButtonRadio.IsEnabled = !status.Running;
        RightButtonRadio.IsEnabled = !status.Running;
        IntervalSlider.IsEnabled = !status.Running;
    }
}
