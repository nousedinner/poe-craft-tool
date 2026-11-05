using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Tools.Clicker;
using ShiKe.Tools.Craft;
using ShiKe.Tools.Hideout;
using ShiKe.Tools.KeyLoop;
using ShiKe.Tools.Settings;

internal static class ConfigurationReviewChecks
{
    public static void DelayNormalizationWaitsForAcknowledgement()
    {
        foreach (var raw in new[] { int.MinValue, -1, 0, 9, 10, 33, 200, 201, 500, int.MaxValue })
        {
            var tool = new CraftTool();
            tool.LoadSettings(JsonSerializer.SerializeToElement(new { delay_ms = raw }));
            var effective = Math.Clamp(raw, 10, 200);
            Require(tool.DelayMs == effective, "运行延迟必须在加载时规范化，与页面是否存在无关");
            Require(SerializedDelay(tool) == raw, "尚未实际告知前必须保留原始持久值");
            var warnings = tool.GetConfigurationWarnings();
            if (raw == effective)
            {
                Require(warnings.Count == 0, "原值在范围内不应产生无关提示");
                continue;
            }
            Require(warnings.Count == 1 && warnings[0].Message.Contains($"{raw} ms", StringComparison.Ordinal) &&
                    warnings[0].Message.Contains($"{effective} ms", StringComparison.Ordinal),
                "修正提示必须明确列出原值和生效值");
            tool.AcknowledgeConfigurationWarning("unknown");
            Require(SerializedDelay(tool) == raw, "无关确认不能消费待告知状态");
            tool.OnShutdown();
            Require(SerializedDelay(tool) == raw && tool.GetConfigurationWarnings().Count == 1,
                "首帧前关闭不能当作已经告知并改写原值");
            tool.AcknowledgeConfigurationWarning(warnings[0].Id);
            Require(SerializedDelay(tool) == effective && tool.GetConfigurationWarnings().Count == 0,
                "实际告知确认后才序列化规范值，且不能重复提示");
        }

        var changed = new CraftTool();
        changed.LoadSettings(JsonSerializer.SerializeToElement(new { delay_ms = 500 }));
        var oldWarning = changed.GetConfigurationWarnings().Single();
        changed.LoadSettings(JsonSerializer.SerializeToElement(new { delay_ms = -20 }));
        changed.AcknowledgeConfigurationWarning(oldWarning.Id);
        Require(changed.DelayMs == 10 && SerializedDelay(changed) == -20 && changed.GetConfigurationWarnings().Count == 1,
            "旧原值对应的提示不能确认后来加载的另一条修正");
        changed.DelayMs = 75;
        Require(SerializedDelay(changed) == 75 && changed.GetConfigurationWarnings().Count == 0,
            "用户显式修改延迟后应采用新值并结束旧提示");
    }

    public static void SettingsGateNamesActiveTools() => RunOnSta(() => WithHost((host, registry) =>
    {
        var background = new NamedRuntimeTool("review_runtime", "后台测试工具") { IsRunning = true };
        var once = new NamedRuntimeTool("hideout", "一键回城");
        var settings = new SettingsTool(new CraftTool(), new ClickerTool(), new KeyLoopTool(), new HideoutTool());
        registry.Register(background);
        registry.Register(once);
        registry.Register(settings);
        settings.Initialize(host);

        var blocked = settings.BeginHotkeyCapture();
        Require(!blocked.Success && blocked.Message.Contains(background.Name, StringComparison.Ordinal),
            "热键捕获被拒绝时应点名真实运行的工具");
        var requested = settings.CaptureDraft() with { TargetProcess = "review-target.exe" };
        blocked = settings.ApplyDraft(requested,
            () => throw new InvalidOperationException("运行中不得进入重新注册"),
            () => throw new InvalidOperationException("运行中不得保存目标变更"));
        Require(!blocked.Success && blocked.Message.Contains(background.Name, StringComparison.Ordinal),
            "目标进程变更被拒绝时也应点名运行工具");

        background.IsRunning = false;
        once.IsRunning = true;
        blocked = settings.BeginHotkeyCapture();
        Require(!blocked.Success && blocked.Message.Contains("一键回城", StringComparison.Ordinal) &&
                blocked.Message.Contains("等待完成", StringComparison.Ordinal) && !blocked.Message.Contains("请停止", StringComparison.Ordinal),
            "回城是一次性操作，应说明等待完成重试而非要求寻找停止按钮");
        background.IsRunning = true;
        blocked = settings.BeginHotkeyCapture();
        Require(!blocked.Success && blocked.Message.Contains(background.Name, StringComparison.Ordinal) &&
                blocked.Message.Contains(once.Name, StringComparison.Ordinal),
            "并行运行时应列出同一次检查中发现的全部工具");
    }));

    // 由AppearanceChecks在已有Application上调用，不启动工具，也不读写用户配置。
    public static void CheckCraftDelayPage(Window owner) => WithHost((host, registry) =>
    {
        host.Storage.SaveSettings(new JsonObject { ["craft"] = new JsonObject { ["delay_ms"] = 500 } });
        var tool = new CraftTool();
        registry.Register(tool);
        tool.Initialize(host);
        tool.LoadSettings(JsonSerializer.SerializeToElement(new { delay_ms = 500 }));
        var page = (CraftPage)tool.CreatePage();
        Require(!page.IsLoaded && tool.DelayMs == 200 && SerializedDelay(tool) == 500,
            "首次页面尚未加载时运行值已规范化，但原始持久值仍需保留");
        var window = new Window
        {
            Owner = owner, Content = page, Width = Math.Min(1000, SystemParameters.WorkArea.Width),
            Height = Math.Min(700, SystemParameters.WorkArea.Height), ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            Pump();
            Require(page.IsLoaded, "延迟显示检查需要真实加载页面");
            var slider = (Slider)page.FindName("DelaySlider");
            var label = (TextBlock)page.FindName("DelayValue");
            Require(slider.Value == 200 && label.Text == "200 ms" && tool.DelayMs == 200,
                "初始滑条、标签、工具运行值必须一致，不能显示原来的500");
            Require(tool.GetConfigurationWarnings().Count == 1 && SerializedDelay(tool) == 500,
                "页面加载及初始状态同步不能隐式确认越界配置提示");

            page.CollectRulesFromUi();
            Require(host.Storage.LoadSettings()["craft"]!["delay_ms"]!.GetValue<int>() == 500 &&
                    tool.GetConfigurationWarnings().Count == 1,
                "普通规则收集和保存不能清除延迟的待告知原值");
            host.Storage.ReadOnlyForUpdateValidation = true;
            Require(SerializedDelay(tool) == 500 && tool.GetConfigurationWarnings().Count == 1,
                "更新只读期间不能提前确认或序列化规范值");
            host.Storage.ReadOnlyForUpdateValidation = false;

            PromptChecks.WithResponse(() => ConfigurationWarningPresenter.Show(window, [tool], () => true),
                MessageBoxResult.OK, dialog =>
                {
                    var message = ((TextBox)dialog.FindName("MessageText")).Text;
                    Require(message.Contains("500 ms", StringComparison.Ordinal) && message.Contains("200 ms", StringComparison.Ordinal),
                        "实际配置提示必须展示原值与生效值");
                    Require(SerializedDelay(tool) == 500, "提示关闭确认前仍需保留原持久值");
                });
            Require(SerializedDelay(tool) == 200 && tool.GetConfigurationWarnings().Count == 0,
                "实际展示并关闭提示后才能确认配置调整");

            slider.Value = 175;
            Require(tool.DelayMs == 175 && label.Text == "175 ms" && tool.GetConfigurationWarnings().Count == 0,
                "显式滑条修改应同步标签、运行值，并结束原值提示");
            page.CollectRulesFromUi();
            Require(host.Storage.LoadSettings()["craft"]!["delay_ms"]!.GetValue<int>() == 175,
                "显式修改后的保存应使用当前生效值");
        }
        finally { window.Close(); tool.OnShutdown(); }
    });

    private static int SerializedDelay(CraftTool tool)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) tool.SaveSettings(writer);
        using var data = JsonDocument.Parse(stream.ToArray());
        return data.RootElement.GetProperty("delay_ms").GetInt32();
    }

    private static void WithHost(Action<ToolHost, ToolRegistry> check)
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = Path.Combine(root, "configuration-review-" + Guid.NewGuid().ToString("N"));
        ToolHost? host = null;
        try
        {
            var registry = new ToolRegistry();
            host = new ToolHost(registry, new StorageService(directory));
            check(host, registry);
        }
        finally
        {
            if (host is not null)
            {
                host.Hotkeys.UnregisterAll(); host.Foreground.Dispose();
                host.Sound.Dispose(); host.Notification.Dispose(); host.Network.Dispose();
            }
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("配置检查目录越界");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private sealed class NamedRuntimeTool(string id, string name) : ITool, IToolRuntime
    {
        public string Id => id;
        public string Name => name;
        public string IconKey => id;
        public bool IsRunning { get; set; }
        public bool ShutdownCompleted => !IsRunning;
        public void RequestStop() => IsRunning = false;
        public void Initialize(ToolHost host) { }
        public FrameworkElement CreatePage() => throw new InvalidOperationException("检查不创建后台工具页面");
        public void OnActivate() { }
        public void OnDeactivate() { }
        public void OnShutdown() => RequestStop();
        public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() => [];
        public void LoadSettings(JsonElement section) { }
        public void SaveSettings(Utf8JsonWriter writer) { writer.WriteStartObject(); writer.WriteEndObject(); }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(20)), "配置提示检查超时");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
