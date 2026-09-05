using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace ShiKe.Services;

/// <summary>
/// 系统托盘（对齐 Python 版 main_window.py _setup_tray / closeEvent）：
/// - NotifyIcon（WinForms 互操作）+ poe.ico
/// - 右键菜单：显示 / 退出；双击：显示窗口
/// - 主窗口关闭按钮始终隐藏到托盘；只有 App 明确 Shutdown 时才真正关闭
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Window _window;
    private bool _disposed;
    private bool _windowClosed;

    /// <summary>托盘"退出"菜单触发（App 层接管：停线程 → 保存 → 注销热键 → 退出）。</summary>
    public event Action? ExitRequested;

    public TrayService(Window window)
    {
        _window = window;
        _icon = new NotifyIcon { Text = "拾刻" };

        // 图标从程序集资源加载（pack URI），不依赖 bin 下是否有 poe.ico 文件
        var icon = LoadAppIcon();
        if (icon is not null)
            _icon.Icon = icon;

        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("显示");
        showItem.Click += (_, _) => ShowWindow();
        var quitItem = new ToolStripMenuItem("退出");
        quitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(showItem);
        menu.Items.Add(quitItem);
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.Visible = true;
        _window.Closed += (_, _) => _windowClosed = true;
    }

    private void ShowWindow()
    {
        if (_disposed || _windowClosed || _window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished)
            return;

        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _windowClosed || _window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished)
                return;

            try
            {
                if (!_window.IsVisible)
                    _window.Show();
                _window.WindowState = WindowState.Normal;
                _window.Activate();
            }
            catch (InvalidOperationException ex)
            {
                // 防御性兜底：窗口一旦进入 Closed 状态，绝不能让托盘回调导致进程崩溃。
                Diag.Log($"[托盘] 恢复窗口失败，窗口已关闭或正在退出: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 用户点击主窗口关闭按钮时隐藏到托盘。
    /// App 明确退出时不会调用本方法，由 App 放行窗口关闭并执行统一清理。
    /// </summary>
    public void OnWindowClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        _window.Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>从程序集嵌入资源加载应用图标（poe.ico）。失败返回 null（托盘仍工作，仅无图标）。</summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/poe.ico"))?.Stream;
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
