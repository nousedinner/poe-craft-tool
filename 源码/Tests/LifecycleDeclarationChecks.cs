using System.Text.Json;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;

internal static class LifecycleDeclarationChecks
{
    public static void RegistrationRequiresExactlyOneLifecycleDeclaration()
    {
        var registry = new ToolRegistry();
        Reject<InvalidOperationException>(() => registry.Register(new UndeclaredTool("missing")));
        Reject<InvalidOperationException>(() => registry.Register(new ConflictingTool("both")));
        Require(registry.Tools.Count == 0, "声明失败不能部分注册工具");
        var passive = new PassiveTool("settings-example");
        var active = new RuntimeTool("worker-example");
        registry.Register(passive);
        registry.Register(active);
        Require(registry.Tools.Count == 2, "纯页面与运行工具都应正常注册");
        Require(!ToolCapabilities.AllShutdownCompleted(registry.Tools), "未结束的运行工具必须阻止更新许可");
        active.Completed = true;
        Require(ToolCapabilities.AllShutdownCompleted(registry.Tools), "运行任务结束后纯页面不阻止退出");
    }

    public static void UndeclaredLifecyclesFailClosedEvenWithoutRegistry()
    {
        foreach (var tool in new ITool[] { new UndeclaredTool("missing"), new ConflictingTool("both") })
        {
            Require(!ToolCapabilities.AllShutdownCompleted([tool]), "未声明或矛盾的能力不得默认判为已清理");
            Require(ToolCapabilities.HasActiveAutomation([tool]), "不能允许未知生命周期下修改运行设置");
        }
    }

    public static void LegacySettingsNamesCannotBeUsedAsToolIds()
    {
        foreach (var id in new[] { "delay_ms", "target_process", "key_loop_slots", "hotkey_start", "schema_version", "DeLaY_Ms" })
        {
            var registry = new ToolRegistry();
            Reject<ArgumentException>(() => registry.Register(new PassiveTool(id)));
            Require(registry.Tools.Count == 0, "配置保留字段不能进入工具注册表");
        }

        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = Path.Combine(root, "reserved-section-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new StorageService(directory);
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"delay_ms\":47,\"target_process\":\"fixture.exe\"}");
            var before = File.ReadAllBytes(path);
            Reject<ArgumentException>(() => storage.RegisterToolSections(["example", "delay_ms"]));
            Require(File.ReadAllBytes(path).SequenceEqual(before) && !File.Exists(path + ".bak"),
                "注册校验不得读取、迁移或覆盖原配置");
            var migrated = storage.LoadSettings();
            Require(migrated["craft"]?["delay_ms"]?.GetValue<int>() == 47 &&
                    migrated["host"]?["target_process"]?.GetValue<string>() == "fixture.exe",
                "拒绝冲突名称后正常旧配置迁移必须保持");
        }
        finally
        {
            if (!Path.GetFullPath(directory).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试路径越界");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    public static void ConfigurationWarningRequiresActualPresentation()
    {
        var tool = new ShiKe.Tools.Craft.CraftTool();
        tool.LoadSettings(JsonSerializer.SerializeToElement(new { delay_ms = 500 }));
        ConfigurationWarningPresenter.Show([tool], _ => false);
        Require(tool.GetConfigurationWarnings().Count == 1, "未渲染的提示不能消费原值");
        ConfigurationWarningPresenter.Show([tool], _ => throw new InvalidOperationException("模拟窗口失败"));
        Require(tool.GetConfigurationWarnings().Count == 1, "提示失败必须保留下一次告知机会");
        var displayed = "";
        ConfigurationWarningPresenter.Show([tool], message => { displayed = message; return true; });
        Require(displayed.Contains("500", StringComparison.Ordinal) && tool.GetConfigurationWarnings().Count == 0,
            "实际提示后才能确认规范化值");
    }

    private class UndeclaredTool(string id) : ITool
    {
        public string Id => id;
        public string Name => id;
        public string IconKey => "";
        public void Initialize(ToolHost host) { }
        public FrameworkElement CreatePage() => throw new InvalidOperationException("本验证不创建页面");
        public void OnActivate() { }
        public void OnDeactivate() { }
        public void OnShutdown() { }
        public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() => [];
        public void LoadSettings(JsonElement section) { }
        public void SaveSettings(Utf8JsonWriter writer) { writer.WriteStartObject(); writer.WriteEndObject(); }
    }
    private sealed class PassiveTool(string id) : UndeclaredTool(id), IPassiveTool { }
    private class RuntimeTool(string id) : UndeclaredTool(id), IToolRuntime
    {
        public bool Completed { get; set; }
        public bool IsRunning => !Completed;
        public bool ShutdownCompleted => Completed;
        public void RequestStop() { }
    }
    private sealed class ConflictingTool(string id) : RuntimeTool(id), IPassiveTool { }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("应拒绝无效声明");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
