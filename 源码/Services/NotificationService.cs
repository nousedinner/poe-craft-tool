using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace ShiKe.Services;

/// <summary>
/// 悬浮通知（对齐 Python 版 NotificationOverlay + main_window 防重逻辑）：
/// - 无边框 + Topmost + 不抢焦点 + 透明背景；屏幕中央；2s 自动消失
/// - WS_EX_TRANSPARENT 确保不拦截鼠标事件
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private Window? _overlay;
    private TextBlock? _label;
    private DispatcherTimer? _dismissTimer;
    private bool _errorShown;
    private volatile bool _disposed;
    private bool _overlayClosed;

    // Win32 扩展窗口样式
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
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
        => _dispatcher = Dispatcher.CurrentDispatcher;

    private void EnsureOverlay()
    {
        if (_overlay is not null) return;
        _label = new TextBlock
        {
            Foreground = PromptPalette.Ink,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0),
            MaxWidth = Math.Max(180, Math.Min(440, SystemParameters.WorkArea.Width - 90)),
        };

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "拾刻提醒", Foreground = PromptPalette.Brush("#708898"), FontSize = 11,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 7),
        });
        content.Children.Add(_label);

        var container = new Border
        {
            Background = PromptPalette.CreateSurface(),
            CornerRadius = new CornerRadius(16),
            BorderBrush = PromptPalette.Brush("#CCDDE7"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 18, 24, 18),
            Margin = new Thickness(8),
            MinWidth = 230,
            Effect = new DropShadowEffect { Color = Color.FromRgb(71, 103, 126), BlurRadius = 12, ShadowDepth = 2, Opacity = .15 },
            Child = content,
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
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        _overlay.Closed += (_, _) => _overlayClosed = true;

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _dismissTimer.Tick += DismissTimer_Tick;
    }

    /// <summary>软件配置或文件错误：归属于软件窗口，不使用游戏运行错误的强制置顶标志。</summary>
    public static void ShowConfigurationError(string message, Window? owner = null)
    {
        if (owner is { IsVisible: true })
            ThemedMessageBox.Show(owner, message, "配置文件错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            ThemedMessageBox.Show(message, "配置文件错误", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>显示浮层通知（新消息覆盖旧消息，2s 自动消失）。</summary>
    public void Show(string message)
    {
        DispatchWhileActive(() => ShowCore(message));
    }

    private void ShowCore(string message)
    {
        if (_disposed || _overlayClosed) return;
        EnsureOverlay();
        _label!.Text = message;
        // 始终重新显示（覆盖旧通知或从隐藏状态恢复）
        _overlay!.Show();
        _overlay.UpdateLayout();
        var work = SystemParameters.WorkArea;
        _overlay.Left = work.Left + (work.Width - _overlay.ActualWidth) / 2;
        _overlay.Top = work.Top + (work.Height - _overlay.ActualHeight) / 2;

        _dismissTimer!.Stop();
        _dismissTimer.Start();
    }

    /// <summary>错误提示（模态 MessageBox + 防风暴）。</summary>
    public void ShowError(string message)
    {
        DispatchWhileActive(() => ShowErrorCore(message));
    }

    private void ShowErrorCore(string message)
    {
        if (_disposed || _errorShown) return;
        _errorShown = true;
        try
        {
            ThemedMessageBox.ShowRuntimeError(message);
        }
        catch (Exception error)
        {
            Diag.Log($"[通知] 淡彩错误窗口失败，回退系统置顶提示: {error.Message}");
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
        DispatchWhileActive(() =>
        {
            _dismissTimer?.Stop();
            if (!_overlayClosed) _overlay?.Hide();
        });
    }

    private void DismissTimer_Tick(object? sender, EventArgs e) => Hide();

    private void DispatchWhileActive(Action action)
    {
        var dispatcher = _dispatcher;
        if (_disposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                    action();
            }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var dispatcher = _dispatcher;
        if (dispatcher.CheckAccess())
            DisposeCore();
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            dispatcher.BeginInvoke(new Action(DisposeCore));
    }

    private void DisposeCore()
    {
        if (_dismissTimer is not null)
        {
            _dismissTimer.Stop();
            _dismissTimer.Tick -= DismissTimer_Tick;
        }
        if (!_overlayClosed) _overlay?.Close();
    }
}
