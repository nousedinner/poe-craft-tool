using System.Text.Json.Nodes;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Services.Updates;
using ShiKe.Tools.Craft;

internal static class UpdateIntegrationChecks
{
    public static void StartupRechecksUpdateLeaseAfterInstanceLock()
    {
        WithDirectory(directory =>
        {
            Directory.CreateDirectory(directory);
            var executable = Path.Combine(directory, "拾刻.exe");
            File.WriteAllText(executable, "isolated startup gate fixture");
            var mutexName = @"Local\ShiKe.UpdateGateTest." + Guid.NewGuid().ToString("N");
            using var oldInstance = new Mutex(true, mutexName);
            using var prechecked = new ManualResetEventSlim();
            var rejected = false;
            var hostConstructed = false;
            Exception? failure = null;
            var contender = new Thread(() =>
            {
                try
                {
                    UpdateReadiness.EnsureOrdinaryStartupAllowed(executable);
                    prechecked.Set();
                    using var instance = new Mutex(false, mutexName);
                    if (!instance.WaitOne(TimeSpan.FromSeconds(3))) throw new TimeoutException("未取得测试单实例锁");
                    try
                    {
                        try { UpdateReadiness.EnsureOrdinaryStartupAllowed(executable); }
                        catch (IOException) { rejected = true; return; }
                        hostConstructed = true;
                    }
                    finally { instance.ReleaseMutex(); }
                }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            contender.Start();
            var ownsOld = true;
            try
            {
                Require(prechecked.Wait(TimeSpan.FromSeconds(3)), "普通启动未通过前置检查");
                using var updaterLease = UpdateBootstrap.AcquireLease(executable);
                oldInstance.ReleaseMutex();
                ownsOld = false;
                Require(contender.Join(TimeSpan.FromSeconds(4)), "启动争锁验证超时");
                if (failure is not null) throw failure;
                Require(rejected && !hostConstructed, "取得单实例锁后必须重查更新租约，不能在替换期间初始化宿主");
            }
            finally
            {
                if (ownsOld) oldInstance.ReleaseMutex();
                contender.Join(TimeSpan.FromSeconds(4));
            }
        });
    }

    public static void ExitPermissionRequiresEveryCompletion()
    {
        Require(UpdateReadiness.CanApproveExit(true, true, true, []), "所有步骤成功才应许可更新");
        Require(!UpdateReadiness.CanApproveExit(false, true, true, []), "助手未准备不能许可");
        Require(!UpdateReadiness.CanApproveExit(true, false, true, []), "配置未加载不能许可");
        Require(!UpdateReadiness.CanApproveExit(true, true, false, []), "工作任务未结束不能许可");
        Require(!UpdateReadiness.CanApproveExit(true, true, true, ["保存失败"]), "退出失败不能被当成更新成功");
    }

    public static void StartupValidationDoesNotWritePlayerData()
    {
        WithDirectory(directory =>
        {
            var storage = new StorageService(directory);
            storage.SavePreset("玩家预设", new JsonObject());
            var settings = Path.Combine(directory, "settings.json");
            var original = "{\"delay_ms\":47,\"hotkey_start\":\"F5\"}";
            File.WriteAllText(settings, original);
            storage.ReadOnlyForUpdateValidation = true;
            var loaded = storage.LoadSettings();
            Require(loaded["craft"]!["delay_ms"]!.GetValue<int>() == 47, "旧配置应可在内存加载");
            Require(File.ReadAllText(settings) == original && !File.Exists(settings + ".bak"), "健康确认前不能迁移玩家文件");
            Reject(() => storage.SaveSettings(loaded));
            Reject(() => storage.SaveRules(new JsonObject()));
            Reject(() => storage.SaveCoordinates([]));
            Reject(() => storage.SavePreset("新预设", new JsonObject()));
            Reject(() => storage.DeletePreset("玩家预设"));
            Require(File.Exists(Path.Combine(directory, "presets", "玩家预设.json")) && File.ReadAllText(settings) == original,
                "只读确认中保存/删除失败必须保留玩家内容");
            storage.ReadOnlyForUpdateValidation = false;
            storage.LoadSettings();
            Require(File.ReadAllText(settings + ".bak") == original, "确认后正常迁移仍保留原件");
        });
    }

    public static void StoppingFlagIsNotWorkerExitProof()
    {
        var host = new ToolHost(new ToolRegistry());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new CraftEngine(host, async (_, _, _) =>
        {
            entered.TrySetResult();
            await finish.Task;
        }, _ => Task.CompletedTask, token => Task.Delay(Timeout.Infinite, token));
        try
        {
            var rules = new CraftRules();
            rules.PrimaryAffixes.Add(new AffixRule { Text = "测试" });
            engine.Start(rules, new() { ["item"] = new(100, 100), [Currency.Alteration] = new(200, 200) },
                33, false, false, "", 10, false, false);
            entered.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            engine.Stop();
            Require(!engine.IsRunning && !engine.ShutdownCompleted, "停止标记不代表旧动作已经结束");
            Require(!UpdateReadiness.CanApproveExit(true, true, engine.ShutdownCompleted, []), "更新不得只按IsRunning判断已退出");
            finish.TrySetResult();
            Require(engine.Shutdown(TimeSpan.FromSeconds(3)) && engine.ShutdownCompleted, "完整关闭应能被确认");
        }
        finally
        {
            finish.TrySetResult();
            engine.Shutdown(TimeSpan.FromSeconds(3));
            host.Network.Dispose();
            host.Sound.Dispose();
            host.Notification.Dispose();
        }
    }

    public static void InvalidSignedUpdateStillRequiresExit()
    {
        foreach (var latest in new[] { "99.0.0", "1.0.0", "invalid" })
        {
            using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"latest\":\"" + latest + "\",\"url\":\"https://example.invalid/not-trusted\",\"autoUpdate\":{}}"),
            }));
            using var network = new NetworkService(handler, _ => { });
            var result = network.CheckVersionAsync().GetAwaiter().GetResult();
            Require(result is { Manifest: null, VerificationError: not null }, "根latest被压低或破坏也不能绕过签名门禁");
            Require(result!.DownloadUrl == UpdateManifest.OfficialManualUrl, "无效清单中的下载地址不能获得信任");
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (StorageException) { return; }
        throw new InvalidOperationException("健康确认期间应拒绝写入");
    }
    private static void WithDirectory(Action<string> action)
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = Path.Combine(root, "update-integration-" + Guid.NewGuid().ToString("N"));
        try { action(directory); }
        finally
        {
            if (!Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("测试清理越界");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
