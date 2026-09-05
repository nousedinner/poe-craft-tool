using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Tools.Craft;
using ShiKe.Tools.Hideout;

namespace ShiKe;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\ShiKe.PoeCraftTool.SingleInstance";

    private ToolHost? _host;
    private TrayService? _tray;
    private MainWindow? _mainWindow;
    private CraftTool? _craftTool;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private bool _isShuttingDown;
    private string _coordinateHotkey = SettingsDefaults.HotkeySetCoord;
    private bool _autoDetectPoe = SettingsDefaults.AutoDetectPoe;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!TryAcquireSingleInstance())
        {
            MessageBox.Show(
                "拾刻已经在运行。\n\n请从系统托盘打开现有窗口；本次启动将退出。",
                "拾刻已在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Diag.ClearOldLog();
        Diag.Log("=== 拾刻启动 ===");

        // 组装：注册表 → 注册抽屉 → 宿主 → 初始化
        var registry = new ToolRegistry();
        _craftTool = new CraftTool();
        registry.Register(_craftTool);
        registry.Register(new HideoutTool());

        _host = new ToolHost(registry);
        _host.IsCraftRunning = () => _craftTool!.IsRunning; // 关闭窗口拦截判断

        // settings 只读取一次：先应用宿主设置，再按 Id 把对应分节交给每个抽屉。
        var settings = _host.Storage.LoadSettings();
        LoadHostSettings(settings);
        foreach (var tool in registry.Tools)
        {
            tool.Initialize(_host);
            var section = settings[tool.Id] as JsonObject ?? new JsonObject();
            tool.LoadSettings(JsonSerializer.SerializeToElement(section));
        }

        _mainWindow = new MainWindow(registry, _host);
        MainWindow = _mainWindow;

        // 托盘（依赖窗口，App 直接管理）
        _tray = new TrayService(_mainWindow);
        _tray.ExitRequested += RequestShutdown;

        // 用户关闭主窗口始终隐藏到托盘；真正退出只走 RequestShutdown。
        _mainWindow.Closing += (_, e) =>
        {
            if (!_isShuttingDown)
                _tray.OnWindowClosing(e);
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

    private bool TryAcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
        try
        {
            _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // 上一次进程异常退出；当前进程已经取得该互斥锁，可以安全接管。
            _ownsSingleInstanceMutex = true;
        }

        if (_ownsSingleInstanceMutex) return true;
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    private void RequestShutdown()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        Shutdown();
    }

    // ── 热键 ──

    private void RegisterHotkeys()
    {
        if (_host is null) return;
        var requests = _host.RegisteredTools.SelectMany(t => t.GetHotkeyRequests()).ToList();

        // 坐标录制热键（宿主级，仅当存在 ICoordinateProvider 抽屉时注册；默认 F7，host 节可配）
        if (_host.RegisteredTools.Any(t => t is ICoordinateProvider))
        {
            requests.Add(new HotkeyRequest
            {
                Key = _coordinateHotkey,
                DisplayName = "坐标录制",
                CheckForeground = false, // 用户可能已切到游戏（Python 版不检查前台）
                Mode = HotkeyMode.Toggle,
                Handler = () => _host.Coordinates.OnRecordHotkey(),
            });
        }

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

        if (!string.IsNullOrWhiteSpace(_host.Foreground.TargetProcess))
        {
            await Dispatcher.InvokeAsync(() => _mainWindow.SetStatus($"已锁定: {_host.Foreground.TargetProcess}"));
            return; // 对齐 Python：已有保存目标时不自动覆盖
        }
        if (!_autoDetectPoe)
        {
            Diag.Log("[启动] AutoDetectPoe: 已由设置关闭");
            return;
        }

        var matched = _host.Foreground.AutoDetectPoe();
        Diag.Log($"[启动] AutoDetectPoe: {(matched is null ? "未检测到" : matched)}");
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
        RequestShutdown();
    }

    private void SaveHostSetting(string key, string value)
    {
        if (_host is null) return;
        _host.Storage.UpdateSettings(settings =>
        {
            if (settings["host"] is not JsonObject host)
            {
                host = new JsonObject();
                settings["host"] = host;
            }
            host[key] = value;
        });
    }

    private void LoadHostSettings(JsonObject settings)
    {
        if (_host is null || settings["host"] is not JsonObject host) return;

        if (host["target_process"] is JsonValue processValue &&
            processValue.TryGetValue<string>(out var targetProcess) &&
            !string.IsNullOrWhiteSpace(targetProcess))
            _host.Foreground.TargetProcess = targetProcess;

        if (host["auto_detect_poe"] is JsonValue autoValue && autoValue.TryGetValue<bool>(out var autoDetect))
            _autoDetectPoe = autoDetect;

        if (host["hotkeys"] is not JsonObject hotkeys) return;
        if (hotkeys["start"] is JsonValue start && start.TryGetValue<string>(out var startKey) && !string.IsNullOrWhiteSpace(startKey))
            _craftTool!.HotkeyStart = startKey;
        if (hotkeys["stop"] is JsonValue stop && stop.TryGetValue<string>(out var stopKey) && !string.IsNullOrWhiteSpace(stopKey))
            _craftTool!.HotkeyStop = stopKey;
        if (hotkeys["coordinate"] is JsonValue coordinate && coordinate.TryGetValue<string>(out var coordinateKey) && !string.IsNullOrWhiteSpace(coordinateKey))
            _coordinateHotkey = coordinateKey;
    }

    // ── 退出 ──

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;
        Diag.Log("=== 拾刻退出 ===");
        if (_host != null)
        {
            // 1. 先停止并等待后台任务释放输入；CraftTool 同时收集页面最新配置。
            foreach (var tool in _host.RegisteredTools)
                tool.OnShutdown();

            // 2. 保存设置（各抽屉 SaveSettings 写入 [Id] 节）
            _host.Storage.UpdateSettings(settings =>
            {
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
            });

            // 3. 注销宿主资源
            _host.Hotkeys.UnregisterAll();
            _host.EmergencyCts.Cancel();
        }

        _tray?.Dispose();

        if (_ownsSingleInstanceMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
            _ownsSingleInstanceMutex = false;
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows 注销/关机必须放行真实关闭，不能被“隐藏到托盘”逻辑拦截。
        _isShuttingDown = true;
        base.OnSessionEnding(e);
    }
}
