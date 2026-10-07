using System.Windows;
using System.Windows.Media;

namespace ShiKe.Services;

/// <summary>共用淡彩确认窗口；保留原按钮结果、默认选项、模态及所有者语义。</summary>
public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
        => ShowCore(null, message, caption, buttons, image, defaultResult, false);

    public static MessageBoxResult Show(Window? owner, string message, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
        => ShowCore(owner, message, caption, buttons, image, defaultResult, false);

    internal static MessageBoxResult ShowRuntimeError(string message)
        => ShowCore(null, message, "运行提示", MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, true);

    private static MessageBoxResult ShowCore(Window? owner, string message, string caption, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult, bool topmost)
    {
        var dispatcher = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return MessageBoxResult.None;
        owner ??= Application.Current?.MainWindow;
        if (owner is not { IsVisible: true }) owner = null;
        try
        {
            var dialog = new MessageDialog(message, caption, buttons, image, defaultResult, topmost);
            if (owner is not null) dialog.Owner = owner;
            dialog.WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
            dialog.ShowDialog();
            return dialog.Result;
        }
        catch (Exception error) when (!topmost)
        {
            Diag.Log($"[提示] 淡彩窗口创建失败，回退系统弹窗: {error.Message}");
            return owner is null
                ? MessageBox.Show(message, caption, buttons, image, defaultResult)
                : MessageBox.Show(owner, message, caption, buttons, image, defaultResult);
        }
    }
}

internal static class PromptPalette
{
    internal static readonly Brush Accent = Brush("#397FA2");
    internal static readonly Brush Ink = Brush("#294F66");
    internal static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    internal static Brush CreateSurface()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new((Color)ColorConverter.ConvertFromString("#F3F8FD"), 0),
                new((Color)ColorConverter.ConvertFromString("#F1FAFA"), .55),
                new((Color)ColorConverter.ConvertFromString("#F8F3FD"), 1),
            },
        };
        brush.Freeze();
        return brush;
    }
}
