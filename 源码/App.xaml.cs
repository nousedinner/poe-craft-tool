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
using ShiKe.Services.Updates;

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
    private readonly UpdateStartup? _updateStartup;
    private readonly UpdateAcceptanceContext? _updateAcceptance;
    private UpdateSession? _installUpdate;
    private bool _updatePrepared;
    private bool _updateHealthConfirmed;

    public App() : this(new StartupPerformance(), false) { }

    internal App(StartupPerformance startup, bool startupProfile, bool startupLifecycleCheck = false, UpdateStartup? updateStartup = null)
    {
        _startup = startup;
        _startupProfile = startupProfile;
        _startupLifecycleCheck = startupLifecycleCheck;
        _updateStartup = updateStartup;
        _updateAcceptance = UpdateAcceptanceContext.Current;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _updateAcceptance?.Record("app-startup");
        DispatcherUnhandledException += (_, args) =>
        {
            if (args.Exception is not StorageException error) return;
            args.Handled = true;
            if (_updateStartup is { IsRollback: false } && !_updateHealthConfirmed)
            {
                Diag.Log($"[更新启动] 只读确认失败：{error}");
                Shutdown(1);
                return;
            }
            NotificationService.ShowConfigurationError(error.Message, _mainWindow);
            if (_mainWindow is null) RequestShutdown();
        };
        _startup.Mark("进入启动函数");

        if (!TryAcquireSingleInstance())
        {
            ThemedMessageBox.Show(
                "拾刻已经在运行。\n\n请从系统托盘打开现有窗口；本次启动将退出。",
                "拾刻已在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 前置检查与单实例认领之间可能经历旧程序退出；取得锁后必须再确认没有更新事务。
        // 这只是文件更新门禁，不重复网络版本检查，也不创建任何工具/输入服务。
        if (!_startupProfile && _updateStartup is null)
        {
            try { UpdateReadiness.EnsureOrdinaryStartupAllowed(Environment.ProcessPath!); }
            catch (Exception error)
            {
                Diag.Log($"[更新启动门禁] 拒绝初始化宿主：{error}");
                ThemedMessageBox.Show(error.Message, "拾刻更新", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(1);
                return;
            }
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
            var validation = _updateStartup is { IsRollback: false };
            var storage = new StorageService { ReadOnlyForUpdateValidation = validation };
            _host = new ToolHost(registry, storage, _updateAcceptance?.CreateNetwork())
                { AutomationSuspendedForUpdate = validation || _updateAcceptance is not null };
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
            _updateAcceptance?.Record("configuration-loaded", new { readOnly = storage.ReadOnlyForUpdateValidation });
        }
        catch (StorageException error)
        {
            if (_updateStartup is null) NotificationService.ShowConfigurationError(error.Message);
            else Diag.Log($"[更新启动] 配置加载失败：{error}");
            Shutdown(1);
            return;
        }
        MarkStartup("工具初始化与设置加载完成");

        _mainWindow = new MainWindow(registry, _host);
        if (_updateStartup is { IsRollback: false } || _updateAcceptance is not null) _mainWindow.IsEnabled = false;
        _mainWindow.ContentRendered += OnMainWindowFirstRender;
        if (_startupProfile || _updateAcceptance is not null) _mainWindow.ShowActivated = false;
        MainWindow = _mainWindow;
        MarkStartup("主窗口构造与事件连接完成");

        // X / Alt+F4 关闭主窗口后由 OnMainWindowClose 正式退出；先取消后台任务。
        _mainWindow.Closing += (_, e) =>
        {
            _startup.Mark("主窗口关闭退出请求");
            _isShuttingDown = true;
            _backgroundCts.Cancel();
        };

        // 专用副本自动验证：首帧前关闭也必须正式退出，不恢复或重建窗口。
        if (_startupLifecycleCheck)
        {
            MarkStartup("首帧前关闭请求");
            _mainWindow.Close();
            return;
        }
        _mainWindow.Show();
        MarkStartup("Show 返回");

        // 热键统一注册（需要窗口句柄）
        if (!_startupProfile && _updateStartup is null && _updateAcceptance is null)
        {
            _host.Hotkeys.AttachToWindow(_mainWindow);
            RegisterHotkeys();
            MarkStartup("热键注册完成");
        }
        else MarkStartup("隔离测量跳过热键注册");
        _updateAcceptance?.Record("native-hotkeys-suppressed");

        // 启动 0.5s 后自动检测 POE 进程（Python QTimer.singleShot(500)）
        // 统一观察启动后台任务；所有请求和延迟都服从应用退出信号。
        if (!_startupProfile && _updateStartup is null)
        {
            var cancellationToken = _backgroundCts.Token;
            // HttpClient 的首个请求可能同步加载代理/网络组件，不能占用首帧的 UI 线程。
            _ = Task.Run(() => _updateAcceptance is null
                ? RunBackgroundTasksAsync(cancellationToken)
                : ObserveBackgroundTaskAsync(RunNetworkTasksAsync(cancellationToken), cancellationToken));
        }
    }

    private void OnMainWindowFirstRender(object? sender, EventArgs e)
    {
        if (!_mainWindow!.IsVisible) return;
        _mainWindow!.ContentRendered -= OnMainWindowFirstRender;
        _startup.Mark("主窗口首帧完成");
        _updateAcceptance?.RecordMainRendered(_settingsLoadedSuccessfully,
            _host!.Storage.ReadOnlyForUpdateValidation, _mainWindow.IsEnabled);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(async () =>
        {
            if (_isShuttingDown) return;
            EnsureTray();
            _startup.Mark("首帧后消息队列空闲");
            _startup.WriteLog();
            if (_updateStartup is { IsRollback: false })
            {
                try
                {
                    if (_updateAcceptance?.FailBeforeHealthy == true)
                    {
                        _updateAcceptance.Record("controlled-failure-before-health");
                        Shutdown(72);
                        return;
                    }
                    await _updateStartup.MarkHealthyAsync(_backgroundCts.Token);
                    _updateHealthConfirmed = true;
                    if (_isShuttingDown) return;
                    _host!.Storage.ReadOnlyForUpdateValidation = false;
                    if (_updateAcceptance is not null)
                    {
                        _updateAcceptance.Record("health-committed", new
                        {
                            committed = _updateStartup.Session.HasCommitAcknowledgement,
                            readonlyReleased = !_host.Storage.ReadOnlyForUpdateValidation,
                            nativeServicesStillSuppressed = _host.AutomationSuspendedForUpdate,
                        });
                        _mainWindow.Close();
                        return;
                    }
                    _host.AutomationSuspendedForUpdate = false;
                    _mainWindow.IsEnabled = true;
                    _host.Hotkeys.AttachToWindow(_mainWindow);
                    RegisterHotkeys();
                    var token = _backgroundCts.Token;
                    _ = Task.Run(() => RunBackgroundTasksAsync(token));
                }
                catch (Exception error)
                {
                    Diag.Log($"[更新启动] 健康确认失败：{error}");
                    Shutdown(1);
                }
                return;
            }
            if (_updateAcceptance?.IsWarmup == true)
            {
                try { await _updateAcceptance.VersionChecked.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (Exception error) { _updateAcceptance.Record("warmup-check-failed", error.Message); Shutdown(73); return; }
                _mainWindow.Close();
                return;
            }
            if (!_startupProfile && _updateStartup is null && _updateAcceptance is null)
                _ = Task.Run(() =>
                {
                    try { UpdateBootstrap.CleanupCompleted(Environment.ProcessPath!); }
                    catch (Exception error) { Diag.Log($"[更新] 延迟清理失败：{error.Message}"); }
                });
            if (!_startupProfile) return;
            _mainWindow.Close(); // 与用户 X 共用真正的关闭路径，不直接调用 Shutdown 代替验证。
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
        var mutexName = _updateAcceptance is not null ? SingleInstanceMutexName + _updateAcceptance.MutexSuffix :
            _startupProfile ? SingleInstanceMutexName + ".StartupProfile." + Environment.ProcessId : SingleInstanceMutexName;
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
            ThemedMessageBox.Show("检测到热键冲突：\n\n" + string.Join("\n", conflicts),
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
        _updateAcceptance?.Record("version-check-completed", new { found = ver is not null, verificationError = ver?.VerificationError is not null });
        _updateAcceptance?.VersionChecked.TrySetResult();
        if (ver is not null)
        {
            await Dispatcher.InvokeAsync(() => ShowUpdateDialogAsync(ver, cancellationToken)).Task.Unwrap();
            return; // 不再执行签到/广告（Python 同）
        }

        if (_updateAcceptance is not null) return; // 仅替换外部网络端口，验收不会上报统计或拉取广告。

        // 2. 签到（静默）
        cancellationToken.ThrowIfCancellationRequested();
        await _host.Network.SendPingAsync(cancellationToken);

        // 3. 广告 → UI
        cancellationToken.ThrowIfCancellationRequested();
        var ads = await _host.Network.FetchAdsAsync(cancellationToken);
        await DispatchWhileActiveAsync(() => _mainWindow.ApplyAds(ads), cancellationToken);
    }

    private async Task ShowUpdateDialogAsync(VersionCheckResult ver, CancellationToken cancellationToken)
    {
        if (_isShuttingDown || cancellationToken.IsCancellationRequested) return;
        // 沿用已有启动发现更新入口；一旦确认有新版，始终阻止继续使用旧版。
        UpdateProgressWindow? progress = null;
        UpdateSession? session = null;
        using var updateCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            _host!.AutomationSuspendedForUpdate = true;
            _host.Hotkeys.UnregisterAll();
            _host.TriggerEmergencyStop();
            foreach (var runtime in _host.RegisteredTools.OfType<IToolRuntime>()) runtime.RequestStop();
            _mainWindow!.IsEnabled = false;
            _mainWindow.Hide();
            _updateAcceptance?.Record("forced-update-blocked-old-ui", new { visible = _mainWindow.IsVisible,
                enabled = _mainWindow.IsEnabled, automationSuspended = _host.AutomationSuspendedForUpdate });
            if (ver.VerificationError is { } verificationError)
            {
                ThemedMessageBox.Show($"{verificationError}\n程序将退出。",
                    "更新验证失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (ver.Manifest is not { } manifest)
            {
                ThemedMessageBox.Show($"检测到新版本，请下载更新后使用。\n\n最新版本: {ver.Latest}",
                    "发现新版本", MessageBoxButton.OK, MessageBoxImage.Warning);
                // 未签名的旧清单只保留既有人工升级入口，不信任其可变下载地址。
                const string legacyDownload = "https://open.cancanneed.top/download/shike-latest.zip";
                if (!BrowserLauncher.TryOpen(legacyDownload, out var error))
                    ThemedMessageBox.Show($"{error}\n\n请手动访问：\n{legacyDownload}", "无法打开下载页面", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ThemedMessageBox.Show($"检测到新版本，程序将退出并自动更新。\n\n最新版本: {ver.Latest}\n用户配置会保留。",
                "发现新版本", MessageBoxButton.OK, MessageBoxImage.Information);
            if (!manifest.CanApply(NetworkService.CurrentVersion))
                throw new InvalidOperationException("当前更新器、运行时或配置格式不符合该版本要求。请使用维护者提供的兼容升级方式；本次未替换文件。");
            UpdateBootstrap.EnsureReleaseAllowed(Environment.ProcessPath!, manifest);
            if (UpdateBootstrap.IsFailedRelease(Environment.ProcessPath!, manifest))
            {
                var retry = ThemedMessageBox.Show("该版本上次更新未成功。是否重新尝试？选择否将退出，现有文件保持。",
                    "更新重试", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (retry != MessageBoxResult.Yes) return;
            }
            progress = new UpdateProgressWindow(ver.Latest);
            progress.Cancelled += updateCancellation.Cancel;
            progress.SetMessage("正在下载并校验更新包…");
            progress.Show();
            session = UpdateSession.Create(manifest, Environment.ProcessPath!);
            using var downloader = _updateAcceptance?.CreateDownloader() ?? new UpdatePackageDownloader();
            var candidate = await downloader.PrepareAsync(manifest, session.DownloadDirectory, updateCancellation.Token);
            await session.PrepareCandidateAsync(candidate, updateCancellation.Token);
            updateCancellation.Token.ThrowIfCancellationRequested();
            progress.SetMessage("正在保存配置并准备退出…");
            if (!_settingsLoadedSuccessfully || _host.Storage.ReadOnlyForUpdateValidation)
                throw new InvalidOperationException("配置尚未完整加载或当前禁止写入，不能准备更新退出。");
            ToolCapabilities.SaveAuxiliaryStates(_host.RegisteredTools);
            ToolSettingsPersistence.SaveSections(_host.Storage, _host.RegisteredTools);
            _host.Hotkeys.UnregisterAll();
            await UpdateBootstrap.StartHelperAsync(session, updateCancellation.Token);
            _installUpdate = session;
            _updatePrepared = true;
            _updateAcceptance?.Record("helper-ready", new { sessionPath = session.SessionPath });
            _updateAcceptance?.BeforeExitPreparation();
        }
        catch (OperationCanceledException)
        {
            try { session?.Abort("更新已取消"); } catch { }
        }
        catch (Exception error)
        {
            Diag.Log($"[更新] 安装准备失败：{error}");
            try { session?.Abort(error.Message); } catch { }
            ThemedMessageBox.Show($"更新未完成，程序将退出。\n\n{error.Message}", "更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progress?.CompleteAndClose();
            RequestShutdown();
        }
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
                    try
                    {
                        // 工具关闭只负责停止运行；配置保存由宿主在允许写入时统一执行。
                        tool.OnShutdown();
                    }
                    catch (Exception error)
                    {
                        Diag.Log($"[退出] {tool.Name}关闭失败: {error}");
                        saveErrors.Add($"{tool.Name}：{error.Message}");
                    }
                }

                // 初始化失败时，各抽屉可能只加载了一部分；这时完全跳过退出保存。
                if (_settingsLoadedSuccessfully && !_host.Storage.ReadOnlyForUpdateValidation)
                {
                    foreach (var auxiliary in _host.RegisteredTools.OfType<IToolAuxiliaryState>())
                    {
                        try { auxiliary.SaveAuxiliaryState(); }
                        catch (Exception error)
                        {
                            Diag.Log($"[退出] 工具额外数据保存失败: {error}");
                            saveErrors.Add(error.Message);
                        }
                    }
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
            if (_installUpdate is not null)
            {
                var stopped = _host is not null && ToolCapabilities.AllShutdownCompleted(_host.RegisteredTools);
                try
                {
                    if (UpdateReadiness.CanApproveExit(_updatePrepared, _settingsLoadedSuccessfully, stopped, saveErrors))
                        _installUpdate.ApproveExit();
                    else _installUpdate.Abort("运行任务未完全结束或配置保存／退出清理失败，禁止替换");
                    _updateAcceptance?.Record("exit-update-permission", new { approved = _installUpdate.IsApproved,
                        aborted = _installUpdate.IsAborted, workersStopped = stopped, saveErrors = saveErrors.Count });
                }
                catch (Exception error) { Diag.Log($"[更新] 退出握手失败，助手不得替换：{error}"); e.ApplicationExitCode = 3; }
            }
            if (_updateStartup is { IsRollback: false } && !_updateHealthConfirmed) e.ApplicationExitCode = 4;
            _updateAcceptance?.WriteExitReport(e.ApplicationExitCode, _settingsLoadedSuccessfully, _updateHealthConfirmed);
            if (_startupProfile)
            {
                _startup.Mark("关闭流程清理完成");
                try { _startup.WriteProfile(); }
                catch (Exception error) { Diag.Log($"[启动测量] 报告保存失败: {error}"); e.ApplicationExitCode = 1; }
                if (saveErrors.Count > 0) e.ApplicationExitCode = 2;
            }
            base.OnExit(e);
        }
        if (!_startupProfile && _updateAcceptance is null && _settingsLoadedSuccessfully && saveErrors.Count > 0 &&
            !(_updateStartup is { IsRollback: false } && !_updateHealthConfirmed))
            NotificationService.ShowConfigurationError("退出时部分配置未能保存或清理，请保留 data/debug.log。\n\n" +
                                                       string.Join("\n\n", saveErrors.Distinct()));
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows 注销/关机与主窗口关闭一样，正式停止并退出。
        _isShuttingDown = true;
        _backgroundCts.Cancel();
        base.OnSessionEnding(e);
    }
}
