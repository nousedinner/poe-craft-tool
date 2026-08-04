using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShiKe.Services;

/// <summary>
/// 悬浮通知（对齐 Python 版 NotificationOverlay + main_window 防重逻辑）：
/// - 无边框 + Topmost + 不抢焦点（ShowActivated=false）+ 透明背景；屏幕中央；2s 自动消失
/// - 成功/普通提示走浮层 Show()；防重：2200ms 内不重复弹（Python _popup_shown）
/// - 错误走模态 MessageBox（Python QMessageBox.critical）+ _errorShown 防风暴（打开期间后续错误忽略）
/// </summary>
public sealed class NotificationService
{
    private readonly Window _overlay;
    private readonly TextBlock _label;
    private readonly DispatcherTimer _dismissTimer;
    private DateTime _lastShowTime = DateTime.MinValue;
    private bool _errorShown;

    public NotificationService()
    {
        _label = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0),
        };

        var container = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(217, 30, 42, 58)), // rgba(30,42,58,0.85)
            CornerRadius = new CornerRadius(16),
            BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)), // rgba(255,255,255,0.15)
            BorderThickness = new Thickness(1),
            Padding = new Thickness(32, 18, 32, 18),
            Child = _label,
        };

        _overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,   // 不抢焦点（Python WA_ShowWithoutActivating）
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = container,
        };

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _dismissTimer.Tick += (_, _) =>
        {
            _dismissTimer.Stop();
            _overlay.Hide();
        };
    }

    /// <summary>显示浮层通知（2s 自动消失；2200ms 防重）。</summary>
    public void Show(string message)
    {
        // 防重：距上次弹窗 < 2200ms 则跳过（Python _popup_shown 行为）
        if (DateTime.Now - _lastShowTime < TimeSpan.FromMilliseconds(2200))
            return;
        _lastShowTime = DateTime.Now;

        _label.Text = message;
        _overlay.Show();
        // 居中于工作区（Show 后 ActualWidth 已由 SizeToContent 完成布局）
        var work = SystemParameters.WorkArea;
        _overlay.Left = work.Left + (work.Width - _overlay.ActualWidth) / 2;
        _overlay.Top = work.Top + (work.Height - _overlay.ActualHeight) / 2;

        _dismissTimer.Stop();
        _dismissTimer.Start();
    }

    /// <summary>
    /// 错误提示（模态 MessageBox + 防风暴，对齐 Python _on_error）。
    /// 对话框打开期间后续错误被忽略；关闭后复位。调用方须保证线程已停止（坑 #17）。
    /// </summary>
    public void ShowError(string message)
    {
        if (_errorShown)
            return;
        _errorShown = true;
        try
        {
            MessageBox.Show(message, "拾刻 - 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _errorShown = false;
        }
    }

    /// <summary>新一轮运行开始时重置防风暴标志。</summary>
    public void ResetErrorFlag() => _errorShown = false;

    public void Hide()
    {
        _dismissTimer.Stop();
        _overlay.Hide();
    }
}
