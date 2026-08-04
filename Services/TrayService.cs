using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace ShiKe.Services;

/// <summary>
/// 系统托盘（对齐 Python 版 main_window.py _setup_tray / closeEvent）：
/// - NotifyIcon（WinForms 互操作）+ poe.ico
/// - 右键菜单：显示 / 退出；双击：显示窗口
/// - 关闭拦截：洗装运行中 → 忽略关闭 + 隐藏到托盘；否则放行（App 层做保存/OnShutdown）
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Window _window;

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
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>
    /// 关闭窗口拦截（对齐 Python closeEvent）：洗装运行中 → 拦截并最小化到托盘。
    /// 返回 true = 已拦截（关闭被取消）。App 层在返回 false 时执行保存退出。
    /// </summary>
    public bool OnWindowClosing(CancelEventArgs e, bool isCraftRunning)
    {
        if (isCraftRunning)
        {
            e.Cancel = true;
            _window.Hide();
            return true;
        }
        return false;
    }

    public void Dispose()
    {
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
