using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShiKe.Services;

namespace ShiKe.Tools.Clicker;

public partial class ClickerPage : UserControl
{
    private readonly ClickerTool _tool;
    private bool _loading = true;

    public ClickerPage(ClickerTool tool)
    {
        _tool = tool;
        InitializeComponent();

        EnableToolSwitch.IsChecked = tool.IsEnabled;
        LeftButtonRadio.IsChecked = tool.MouseButton == ClickerMouseButton.Left;
        RightButtonRadio.IsChecked = tool.MouseButton == ClickerMouseButton.Right;
        IntervalSlider.Value = tool.IntervalMs;
        IntervalValue.Text = $"{tool.IntervalMs} ms";
        RefreshSettingsPresentation();
        ApplyStatus(new ClickerStatus(tool.IsRunning, tool.ClickCount,
            tool.IsRunning ? "连点中..." : "连点器就绪"));
        _loading = false;
        RefreshEnabledPresentation();

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
        HotkeyHint.Text = $"切换 {HotkeySetting.Display(_tool.Hotkey)} · 按住 {HotkeySetting.Display(_tool.HoldHotkey)}";
    }

    internal void RefreshEnabledPresentation()
    {
        _loading = true;
        try
        {
            EnableToolSwitch.IsChecked = _tool.IsEnabled;
            HotkeyHint.Opacity = _tool.IsEnabled ? 1.0 : 0.55;
            if (!_tool.IsEnabled && !_tool.IsRunning) StatusText.Text = "功能已停用";
            else if (_tool.IsEnabled && !_tool.IsRunning && StatusText.Text == "功能已停用") StatusText.Text = "连点器就绪";
        }
        finally
        {
            _loading = false;
        }
    }

    private void EnableToolSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var result = _tool.SetEnabled(EnableToolSwitch.IsChecked == true);
        if (result.Success) return;
        RefreshEnabledPresentation();
        var owner = Window.GetWindow(this);
        if (owner is null)
            MessageBox.Show(result.Message, "无法修改启用状态", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(owner, result.Message, "无法修改启用状态", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void ApplyStatus(ClickerStatus status)
    {
        StatusText.Text = !_tool.IsEnabled && !status.Running ? "功能已停用" : status.Text;
        ClickCountText.Text = status.ClickCount.ToString();
        StatusDot.Foreground = status.Running
            ? new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x3E))
            : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        LeftButtonRadio.IsEnabled = !status.Running;
        RightButtonRadio.IsEnabled = !status.Running;
        IntervalSlider.IsEnabled = !status.Running;
    }
}
