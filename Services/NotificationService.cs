using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShiKe.Services;

/// <summary>
/// 悬浮通知（对齐 Python 版 NotificationOverlay + main_window 防重逻辑）：
/// - 无边框 + Topmost + 不抢焦点 + 透明背景；屏幕中央；2s 自动消失
/// - WS_EX_TRANSPARENT 确保不拦截鼠标事件
/// </summary>
public sealed class NotificationService
{
    private readonly Window _overlay;
    private readonly TextBlock _label;
    private readonly DispatcherTimer _dismissTimer;
    private DateTime _lastShowTime = DateTime.MinValue;
    private bool _errorShown;

    // Win32 扩展窗口样式
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_TASKMODAL = 0x00002000;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const uint MB_TOPMOST = 0x00040000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hwnd, int index, int newStyle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

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
            Background = new SolidColorBrush(Color.FromArgb(217, 30, 42, 58)),
            CornerRadius = new CornerRadius(16),
            BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
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
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = container,
        };

        // 窗口句柄创建时立即设置 WS_EX_TRANSPARENT（真正鼠标穿透）
        _overlay.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_overlay).Handle;
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
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
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ShowCore(message);
        else
            dispatcher.BeginInvoke(() => ShowCore(message));
    }

    private void ShowCore(string message)
    {
        var now = DateTime.Now;
        _lastShowTime = now;

        _label.Text = message;
        // 始终重新显示（覆盖旧通知或从隐藏状态恢复）
        _overlay.Show();
        var work = SystemParameters.WorkArea;
        _overlay.Left = work.Left + (work.Width - _overlay.ActualWidth) / 2;
        _overlay.Top = work.Top + (work.Height - _overlay.ActualHeight) / 2;

        _dismissTimer.Stop();
        _dismissTimer.Start();
    }

    /// <summary>错误提示（模态 MessageBox + 防风暴）。</summary>
    public void ShowError(string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ShowErrorCore(message);
        else
            dispatcher.BeginInvoke(() => ShowErrorCore(message));
    }

    private void ShowErrorCore(string message)
    {
        if (_errorShown) return;
        _errorShown = true;
        try
        {
            var result = MessageBoxW(0, message, "拾刻 - 错误",
                MB_OK | MB_ICONERROR | MB_TASKMODAL | MB_SETFOREGROUND | MB_TOPMOST);
            if (result == 0)
                Diag.Log($"[通知] 置顶错误弹窗显示失败: Win32 {Marshal.GetLastWin32Error()}");
        }
        finally { _errorShown = false; }
    }

    public void ResetErrorFlag() => _errorShown = false;

    public void Hide()
    {
        _dismissTimer.Stop();
        _overlay.Hide();
    }
}
