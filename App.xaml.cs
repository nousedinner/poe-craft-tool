using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Tools.Clicker;
using ShiKe.Tools.Craft;
using ShiKe.Tools.Hideout;
using ShiKe.Tools.KeyLoop;
using ShiKe.Tools.Settings;

namespace ShiKe;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\ShiKe.PoeCraftTool.SingleInstance";

    private ToolHost? _host;
    private TrayService? _tray;
    private MainWindow? _mainWindow;
    private SettingsTool? _settingsTool;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private bool _isShuttingDown;
    private bool _settingsLoadedSuccessfully;
    private readonly CancellationTokenSource _backgroundCts = new();
    private readonly StartupPerformance _startup;
    private readonly bool _startupProfile;
    private readonly bool _startupLifecycleCheck;

    public App() : this(new StartupPerformance(), false) { }

    internal App(StartupPerformance startup, bool startupProfile, bool startupLifecycleCheck = false)
    {
        _startup = startup;
        _startupProfile = startupProfile;
        _startupLifecycleCheck = startupLifecycleCheck;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            if (args.Exception is not StorageException error) return;
            args.Handled = true;
            NotificationService.ShowConfigurationError(error.Message, _mainWindow);
            if (_mainWindow is null) RequestShutdown();
        };
        _startup.Mark("进入启动函数");

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
        void MarkStartup(string stage) => _startup.Mark(stage);
        MarkStartup("取得单实例锁");

        // 组装：注册表 → 注册抽屉 → 宿主 → 初始化
        var registry = new ToolRegistry();
        var craftTool = new CraftTool();
        var clickerTool = new ClickerTool();
        var keyLoopTool = new KeyLoopTool();
        var hideoutTool = new HideoutTool();
        _settingsTool = new SettingsTool(craftTool, clickerTool, keyLoopTool, hideoutTool);
        registry.Register(craftTool);
        registry.Register(clickerTool);
        registry.Register(keyLoopTool);
        registry.Register(hideoutTool);
        registry.Register(_settingsTool);
        MarkStartup("工具注册表创建完成");

        try
        {
            _host = new ToolHost(registry);
            MarkStartup("公共服务创建完成");

            // 读取失败须中止启动，不能把未完整加载的默认值在退出时写回。
            var settings = _host.Storage.LoadSettings();
            foreach (var tool in registry.Tools)
            {
                tool.Initialize(_host);
                var section = settings[tool.Id] as JsonObject ?? new JsonObject();
                tool.LoadSettings(JsonSerializer.SerializeToElement(section));
            }
            _settingsLoadedSuccessfully = true;
        }
        catch (StorageException error)
        {
            NotificationService.ShowConfigurationError(error.Message);
            Shutdown(1);
            return;
        }
        MarkStartup("工具初始化与设置加载完成");

        _mainWindow = new MainWindow(registry, _host);
        _mainWindow.ContentRendered += OnMainWindowFirstRender;
        if (_startupProfile) _mainWindow.ShowActivated = false;
        MainWindow = _mainWindow;
        MarkStartup("主窗口构造与事件连接完成");

        // 用户关闭主窗口始终隐藏到托盘；真正退出只走 RequestShutdown。
        _mainWindow.Closing += (_, e) =>
        {
            if (!_isShuttingDown)
                EnsureTray().OnWindowClosing(e);
        };

        // 专用副本自动验证：窗口尚未显示时关闭，仍必须保留可用托盘与窗口。
        if (_startupLifecycleCheck)
        {
            _mainWindow.Close();
            if (_tray is null || _mainWindow.IsVisible) { Shutdown(2); return; }
            MarkStartup("首帧前关闭被托盘接管");
        }
        _mainWindow.Show();
        MarkStartup("Show 返回");

        // 热键统一注册（需要窗口句柄）
        if (!_startupProfile)
        {
            _host.Hotkeys.AttachToWindow(_mainWindow);
            RegisterHotkeys();
            MarkStartup("热键注册完成");
        }
        else MarkStartup("隔离测量跳过热键注册");

        // 启动 0.5s 后自动检测 POE 进程（Python QTimer.singleShot(500)）
        // 统一观察启动后台任务；所有请求和延迟都服从应用退出信号。
        if (!_startupProfile)
        {
            var cancellationToken = _backgroundCts.Token;
            // HttpClient 的首个请求可能同步加载代理/网络组件，不能占用首帧的 UI 线程。
            _ = Task.Run(() => RunBackgroundTasksAsync(cancellationToken));
        }
    }

    private void OnMainWindowFirstRender(object? sender, EventArgs e)
    {
        if (!_mainWindow!.IsVisible) return;
        _mainWindow!.ContentRendered -= OnMainWindowFirstRender;
        _startup.Mark("主窗口首帧完成");
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_isShuttingDown) return;
            EnsureTray();
            _startup.Mark("首帧后消息队列空闲");
            _startup.WriteLog();
            if (!_startupProfile) return;
            if (_startupLifecycleCheck)
            {
                _mainWindow.Close();
                if (_mainWindow.IsVisible) { Shutdown(2); return; }
                _mainWindow.Show();
                if (!_mainWindow.IsVisible) { Shutdown(2); return; }
                _mainWindow.Close();
                if (_mainWindow.IsVisible) { Shutdown(2); return; }
                _startup.Mark("恢复窗口与重复关闭验证完成");
            }
            try { _startup.WriteProfile(); }
            catch (Exception error) { Diag.Log($"[启动测量] 报告保存失败: {error}"); Shutdown(1); return; }
            RequestShutdown();
        }));
    }

    private TrayService EnsureTray()
    {
        if (_tray is not null) return _tray;
        _tray = new TrayService(_mainWindow!);
        _tray.ExitRequested += RequestShutdown;
        _startup.Mark("托盘创建完成");
        return _tray;
    }

    private bool TryAcquireSingleInstance()
    {
        var mutexName = _startupProfile ? SingleInstanceMutexName + ".StartupProfile." + Environment.ProcessId : SingleInstanceMutexName;
        _singleInstanceMutex = new Mutex(initiallyOwned: false, mutexName);
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
        _backgroundCts.Cancel();
        Shutdown();
    }

    // ── 热键 ──

    private void RegisterHotkeys()
    {
        if (_host is null) return;
        var conflicts = _host.Hotkeys.RegisterAll(_host.BuildHotkeyRequests());
        if (conflicts.Count > 0)
        {
            MessageBox.Show("检测到热键冲突：\n\n" + string.Join("\n", conflicts),
                "热键冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── 自动检测 POE ──

    private Task RunBackgroundTasksAsync(CancellationToken cancellationToken)
        => Task.WhenAll(
            ObserveBackgroundTaskAsync(AutoDetectPoeAsync(cancellationToken), cancellationToken),
            ObserveBackgroundTaskAsync(RunNetworkTasksAsync(cancellationToken), cancellationToken));

    private async Task ObserveBackgroundTaskAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            Diag.Log($"[启动后台] 任务失败: {error}");
            if (error is StorageException)
            {
                try { await DispatchWhileActiveAsync(() => NotificationService.ShowConfigurationError(error.Message, _mainWindow), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception dispatchError) { Diag.Log($"[启动后台] 配置错误提示未能提交: {dispatchError}"); }
            }
        }
    }

    private async Task DispatchWhileActiveAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isShuttingDown || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (!_isShuttingDown && !cancellationToken.IsCancellationRequested) action();
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken);
    }

    private async Task AutoDetectPoeAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(500, cancellationToken);
        if (_host is null || _mainWindow is null) return;

        if (!string.IsNullOrWhiteSpace(_host.Foreground.TargetProcess))
        {
            await DispatchWhileActiveAsync(_mainWindow.RefreshStatus, cancellationToken);
            return; // 对齐 Python：已有保存目标时不自动覆盖
        }
        if (!_host.AutoDetectPoe)
        {
            Diag.Log("[启动] AutoDetectPoe: 已由设置关闭");
            return;
        }

        var matched = await Task.Run(_host.Foreground.AutoDetectPoe, cancellationToken);
        Diag.Log($"[启动] AutoDetectPoe: {(matched is null ? "未检测到" : matched)}");
        if (matched is null) return;

        await DispatchWhileActiveAsync(() =>
        {
            // 进程枚举期间用户可能手动选择或关闭自动检测，不覆盖新的选择。
            if (!_host.AutoDetectPoe || !string.IsNullOrWhiteSpace(_host.Foreground.TargetProcess)) return;
            _settingsTool?.ApplyDetectedTarget(matched);
            _mainWindow.RefreshStatus();
            _host!.Notification.Show($"已自动锁定游戏进程: {matched}");
        }, cancellationToken);
    }

    // ── 网络（Python _send_daily_ping 顺序：版本 → 签到 → 广告）──

    private async Task RunNetworkTasksAsync(CancellationToken cancellationToken)
    {
        if (_host is null || _mainWindow is null) return;

        // 1. 版本检查（新版 → 强制弹窗 + 打开下载页 + 退出）
        var ver = await _host.Network.CheckVersionAsync(cancellationToken);
        if (ver is not null)
        {
            await DispatchWhileActiveAsync(() => ShowUpdateDialog(ver), cancellationToken);
            return; // 不再执行签到/广告（Python 同）
        }

        // 2. 签到（静默）
        cancellationToken.ThrowIfCancellationRequested();
        await _host.Network.SendPingAsync(cancellationToken);

        // 3. 广告 → UI
        cancellationToken.ThrowIfCancellationRequested();
        var ads = await _host.Network.FetchAdsAsync(cancellationToken);
        await DispatchWhileActiveAsync(() => _mainWindow.ApplyAds(ads), cancellationToken);
    }

    private void ShowUpdateDialog(VersionCheckResult ver)
    {
        MessageBox.Show($"检测到新版本，请下载更新后使用。\n\n最新版本: {ver.Latest}",
            "发现新版本", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (!BrowserLauncher.TryOpen(ver.DownloadUrl, out var error))
            MessageBox.Show(_mainWindow!, $"{error}\n\n请手动访问下载地址：\n{ver.DownloadUrl}",
                "无法打开下载页面", MessageBoxButton.OK, MessageBoxImage.Warning);
        RequestShutdown();
    }

    // ── 退出 ──

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;
        _backgroundCts.Cancel();
        Diag.Log("=== 拾刻退出 ===");
        var saveErrors = new List<string>();
        try
        {
            if (_host is not null)
            {
                // 先广播取消；单个取消回调异常也不能跳过其他抽屉的清理。
                try { _host.EmergencyCts.Cancel(); }
                catch (Exception error) { Diag.Log($"[退出] 全局停止失败: {error}"); saveErrors.Add(error.Message); }
                foreach (var tool in _host.RegisteredTools)
                {
                    try { tool.OnShutdown(); }
                    catch (Exception error)
                    {
                        Diag.Log($"[退出] {tool.Name}关闭失败: {error}");
                        saveErrors.Add($"{tool.Name}：{error.Message}");
                    }
                }

                // 初始化失败时，各抽屉可能只加载了一部分；这时完全跳过退出保存。
                if (_settingsLoadedSuccessfully)
                {
                    try
                    {
                        ToolSettingsPersistence.SaveSections(_host.Storage, _host.RegisteredTools);
                    }
                    catch (Exception error)
                    {
                        Diag.Log($"[退出] 设置保存失败: {error}");
                        saveErrors.Add(error.Message);
                    }
                }
            }
        }
        finally
        {
            // 每项独立清理，某个服务失败不能阻断托盘、音效或互斥锁的释放。
            void Cleanup(string name, Action action)
            {
                try { action(); }
                catch (Exception error)
                {
                    Diag.Log($"[退出] {name}清理失败: {error}");
                    saveErrors.Add($"{name}：{error.Message}");
                }
            }
            Cleanup("热键", () => _host?.Hotkeys.UnregisterAll());
            Cleanup("网络", () => _host?.Network.Dispose());
            Cleanup("音效", () => _host?.Sound.Dispose());
            Cleanup("通知", () => _host?.Notification.Dispose());
            Cleanup("托盘", () => _tray?.Dispose());
            Cleanup("单实例锁", () =>
            {
                if (_ownsSingleInstanceMutex)
                {
                    try { _singleInstanceMutex?.ReleaseMutex(); }
                    finally { _ownsSingleInstanceMutex = false; }
                }
            });
            Cleanup("单实例句柄", () => _singleInstanceMutex?.Dispose());
            _singleInstanceMutex = null;
            Cleanup("后台任务令牌", _backgroundCts.Dispose);
            base.OnExit(e);
        }
        if (_settingsLoadedSuccessfully && saveErrors.Count > 0)
            NotificationService.ShowConfigurationError("退出时部分配置未能保存或清理，请保留 data/debug.log。\n\n" +
                                                       string.Join("\n\n", saveErrors.Distinct()));
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows 注销/关机必须放行真实关闭，不能被“隐藏到托盘”逻辑拦截。
        _isShuttingDown = true;
        _backgroundCts.Cancel();
        base.OnSessionEnding(e);
    }
}
