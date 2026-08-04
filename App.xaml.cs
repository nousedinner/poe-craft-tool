using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Tools.Hideout;

namespace ShiKe;

public partial class App : Application
{
    private ToolHost? _host;
    private TrayService? _tray;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 组装：注册表 → 注册抽屉 → 宿主 → 初始化
        var registry = new ToolRegistry();
        registry.Register(new HideoutTool());

        _host = new ToolHost(registry);

        foreach (var tool in registry.Tools)
            tool.Initialize(_host);

        _mainWindow = new MainWindow(registry, _host);
        MainWindow = _mainWindow;

        // 托盘（依赖窗口，App 直接管理）
        _tray = new TrayService(_mainWindow);
        _tray.ExitRequested += () => Shutdown();

        // 关闭拦截：洗装运行中 → 最小化到托盘（Python closeEvent）
        _mainWindow.Closing += (_, e) =>
        {
            if (_host is not null)
                _tray.OnWindowClosing(e, _host.IsCraftRunning());
        };

        _mainWindow.Show();

        // 热键统一注册（需要窗口句柄）
        _host.Hotkeys.AttachToWindow(_mainWindow);
        RegisterHotkeys();

        // 启动 0.5s 后自动检测 POE 进程（Python QTimer.singleShot(500)）
        _ = AutoDetectPoeAsync();

        // 后台网络：版本检查 → 签到 → 广告（Python _send_daily_ping 顺序）
        _ = RunNetworkTasksAsync();
    }

    // ── 热键 ──

    private void RegisterHotkeys()
    {
        if (_host is null) return;
        var requests = _host.RegisteredTools.SelectMany(t => t.GetHotkeyRequests()).ToList();
        var conflicts = _host.Hotkeys.RegisterAll(requests);
        if (conflicts.Count > 0)
        {
            MessageBox.Show("检测到热键冲突：\n\n" + string.Join("\n", conflicts),
                "热键冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 自动检测 POE ──

    private async Task AutoDetectPoeAsync()
    {
        await Task.Delay(500);
        if (_host is null || _mainWindow is null) return;

        var matched = _host.Foreground.AutoDetectPoe();
        if (matched is null) return;

        _host.Foreground.TargetProcess = matched;
        await Dispatcher.InvokeAsync(() =>
        {
            _mainWindow.SetStatus($"已锁定: {matched}");
            _host!.Notification.Show($"已自动锁定游戏进程: {matched}");
        });
        SaveHostSetting("target_process", matched);
    }

    // ── 网络（Python _send_daily_ping 顺序：版本 → 签到 → 广告）──

    private async Task RunNetworkTasksAsync()
    {
        if (_host is null || _mainWindow is null) return;

        // 1. 版本检查（新版 → 强制弹窗 + 打开下载页 + 退出）
        var ver = await _host.Network.CheckVersionAsync();
        if (ver is not null)
        {
            await Dispatcher.InvokeAsync(() => ShowUpdateDialog(ver));
            return; // 不再执行签到/广告（Python 同）
        }

        // 2. 签到（静默）
        await _host.Network.SendPingAsync();

        // 3. 广告 → UI
        var ads = await _host.Network.FetchAdsAsync();
        await Dispatcher.InvokeAsync(() => _mainWindow.ApplyAds(ads));
    }

    private void ShowUpdateDialog(VersionCheckResult ver)
    {
        MessageBox.Show($"检测到新版本，请下载更新后使用。\n\n最新版本: {ver.Latest}",
            "发现新版本", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (!string.IsNullOrEmpty(ver.DownloadUrl))
            Process.Start(new ProcessStartInfo(ver.DownloadUrl) { UseShellExecute = true });
        Shutdown();
    }

    private void SaveHostSetting(string key, string value)
    {
        if (_host is null) return;
        var settings = _host.Storage.LoadSettings();
        if (settings["host"] is not JsonObject host)
        {
            host = new JsonObject();
            settings["host"] = host;
        }
        host[key] = value;
        _host.Storage.SaveSettings(settings);
    }

    // ── 退出 ──

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            // 1. 保存设置（各抽屉 SaveSettings 写入 [Id] 节）
            var settings = _host.Storage.LoadSettings();
            foreach (var tool in _host.RegisteredTools)
            {
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    tool.SaveSettings(writer);
                }
                if (JsonNode.Parse(ms.ToArray()) is JsonObject section)
                    settings[tool.Id] = section;
            }
            _host.Storage.SaveSettings(settings);

            // 2. 抽屉清理（停线程 → 注销热键）
            foreach (var tool in _host.RegisteredTools)
                tool.OnShutdown();
            _host.Hotkeys.UnregisterAll();
            _host.EmergencyCts.Cancel();
        }

        _tray?.Dispose();
        base.OnExit(e);
    }
}
