using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShiKe.Services.Updates;

// 只在已发现强制更新后使用；主窗口保持禁用，关闭此窗口会取消更新并退出。
internal sealed class UpdateProgressWindow : Window
{
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private bool _completed;
    internal event Action? Cancelled;

    internal UpdateProgressWindow(string version)
    {
        Title = "拾刻更新";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = $"正在更新至 {version}", FontSize = 18, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(_message);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 18, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "完成后自动启动。关闭此窗口将退出。", Foreground = Brushes.Gray, FontSize = 12 });
        Content = panel;
        Closed += (_, _) => { if (!_completed) Cancelled?.Invoke(); };
    }

    internal void SetMessage(string text) => _message.Text = text;
    internal void CompleteAndClose() { _completed = true; Close(); }
}
