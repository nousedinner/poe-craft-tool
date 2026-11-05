using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Services.Updates;
using ShiKe.Tools.Clicker;
using ShiKe.Tools.Craft;
using ShiKe.Tools.Hideout;
using ShiKe.Tools.KeyLoop;
using ShiKe.Tools.Settings;

// 第五个工具只声明能力并注册；不初始化真实引擎、不注册热键、不发送输入或请求网络。
internal static class ToolCapabilityChecks
{
    public static void RegisteredToolJoinsHotkeySettingsAndRollback() => WithFixture(f =>
    {
        var original = f.Settings.CaptureDraft();
        var entries = original.Hotkeys.GetEntries();
        Require(entries.Count == 8 && entries.Single(entry => entry.Id == "extra_run").Key == "F12",
            "新增工具的热键必须自动进入设置草稿");
        Require(f.Settings.DefaultHotkeys().GetEntries().Single(entry => entry.Id == "extra_run").Key == "F12",
            "恢复默认值必须包含新增工具的声明");

        var conflict = HotkeyConflictResolver.Resolve(original.Hotkeys.WithKey("extra_run", "F8"), "extra_run");
        Require(conflict.Hotkeys.ClickerToggle == string.Empty && conflict.ClearedNames.Contains("连点器切换"),
            "新增工具与既有工具必须使用同一冲突解决规则");

        var registrations = 0;
        var changed = original with { Hotkeys = original.Hotkeys.WithKey("extra_run", "Ctrl+F10") };
        var applied = f.Settings.ApplyDraft(changed, () =>
        {
            registrations++;
            Require(f.Host.BuildHotkeyRequests().Any(request => request.DisplayName == "附加工具" && request.Key == "Ctrl+F10"),
                "新工具的编辑字段必须进入统一运行热键清单");
            return [];
        }, f.Settings.SaveAllSections);
        Require(applied.Success && registrations == 1 && f.Extra.Key == "Ctrl+F10" && f.Extra.RefreshCount > 0,
            "新增工具的修改应经过一次注册并刷新展示");
        var stored = f.Host.Storage.LoadSettings();
        Require(stored["extra"]?["hotkey"]?.GetValue<string>() == "Ctrl+F10" && stored.ContainsKey("craft") && stored.ContainsKey("host"),
            "新增工具分节必须与既有分节一起持久化");
        var before = File.ReadAllBytes(Path.Combine(f.Directory, "settings.json"));

        var rejected = f.Settings.CaptureDraft() with
        {
            Hotkeys = f.Settings.CaptureDraft().Hotkeys.WithKey("extra_run", "F10"),
        };
        var rejectedRegistrations = 0;
        var result = f.Settings.ApplyDraft(rejected, () =>
        {
            rejectedRegistrations++;
            return f.Extra.Key == "F10" ? ["模拟注册失败"] : [];
        }, () => throw new InvalidOperationException("注册失败不得保存"));
        Require(!result.Success && rejectedRegistrations == 2 && f.Extra.Key == "Ctrl+F10",
            "新增工具注册失败必须恢复字段并重新注册原值");
        Require(File.ReadAllBytes(Path.Combine(f.Directory, "settings.json")).SequenceEqual(before),
            "注册失败不得触碰已保存设置");

        var saves = 0;
        result = f.Settings.ApplyDraft(rejected, () => [], () =>
        {
            if (++saves == 1) throw new IOException("模拟保存失败");
            f.Settings.SaveAllSections();
        });
        Require(!result.Success && saves == 2 && f.Extra.Key == "Ctrl+F10" &&
            f.Host.Storage.LoadSettings()["extra"]?["hotkey"]?.GetValue<string>() == "Ctrl+F10",
            "新增工具保存失败也必须随整组设置恢复");
    });

    public static void RegisteredRuntimeProtectsSettingsAndUpdateExit() => WithFixture(f =>
    {
        f.Extra.Running = true;
        f.Extra.Completed = false;
        Require(ToolCapabilities.HasActiveAutomation(f.Host.RegisteredTools), "运行检查必须识别新增工具");
        Require(!f.Settings.BeginHotkeyCapture().Success, "新增工具运行时不能进入热键捕获");
        var changed = f.Settings.CaptureDraft() with { TargetProcess = "new-target.exe" };
        var result = f.Settings.ApplyDraft(changed,
            () => throw new InvalidOperationException("运行中不应重新注册"),
            () => throw new InvalidOperationException("运行中不应保存目标变更"));
        Require(!result.Success && string.IsNullOrEmpty(f.Host.Foreground.TargetProcess),
            "新增工具运行期间目标变更必须被拒绝");

        ((IToolRuntime)f.Extra).RequestStop();
        Require(!ToolCapabilities.HasActiveAutomation(f.Host.RegisteredTools), "停止请求应取消运行状态");
        Require(!ToolCapabilities.AllShutdownCompleted(f.Host.RegisteredTools),
            "停止状态不能冒充后台清理完成");
        Require(!UpdateReadiness.CanApproveExit(true, true,
            ToolCapabilities.AllShutdownCompleted(f.Host.RegisteredTools), []),
            "新增工具尚未完成清理时必须阻止更新退出许可");
        f.Extra.Completed = true;
        Require(UpdateReadiness.CanApproveExit(true, true,
            ToolCapabilities.AllShutdownCompleted(f.Host.RegisteredTools), []),
            "全部注册工具真实完成后才允许更新退出");
    });

    public static void AuxiliaryStateIsAnExplicitCapability() => WithFixture(f =>
    {
        f.Extra.OnShutdown();
        Require(f.Extra.AuxiliarySaves == 0, "停止不应隐式保存额外数据");
        // 这里测试能力分派；App 的已加载/可写门禁决定是否调用此入口。
        ToolCapabilities.SaveAuxiliaryStates([f.Extra]);
        Require(f.Extra.AuxiliarySaves == 1, "额外数据保存不应识别具体工具类型");
    });

    public static void RegisteredOnlySectionLoadsWithoutMigration() => WithFixture(f =>
    {
        var path = Path.Combine(f.Directory, "settings.json");
        File.WriteAllText(path, "{\n  \"extra\": {\"hotkey\": \"F10\", \"sentinel\": \"原始格式保留\"}\n}\n");
        var before = File.ReadAllBytes(path);
        WithNewExtraHost(f.Directory, host =>
        {
            var loaded = host.Storage.LoadSettings();
            Require(loaded.Count == 1 && loaded["extra"]?["hotkey"]?.GetValue<string>() == "F10",
                "仅含已注册新工具分节的既有文件必须直接加载");
        });
        Require(File.ReadAllBytes(path).SequenceEqual(before) && !File.Exists(path + ".bak"),
            "新工具分节不得被误迁移、重写或创建迁移备份");
    });

    public static void FirstNewToolSectionSurvivesHostReload() => WithFixture(f =>
    {
        var path = Path.Combine(f.Directory, "settings.json");
        Require(!File.Exists(path), "首次保存测试需要空存储");
        ToolSettingsPersistence.SaveSections(f.Host.Storage, [f.Extra]);
        var before = File.ReadAllBytes(path);
        WithNewExtraHost(f.Directory, host =>
        {
            var loaded = host.Storage.LoadSettings();
            Require(loaded.Count == 1 && loaded["extra"]?["hotkey"]?.GetValue<string>() == "F12",
                "只保存新工具后，新宿主必须恢复该分节，不能补写旧五节或schema字段");
        });
        Require(File.ReadAllBytes(path).SequenceEqual(before) && !File.Exists(path + ".bak"),
            "首次新工具保存后重启加载必须保持原字节，不走旧版迁移");
    });

    public static void MalformedRegisteredSectionCannotBeOverwritten() => WithFixture(f =>
    {
        var path = Path.Combine(f.Directory, "settings.json");
        foreach (var malformed in new[] { "null", "42", "[]", "\"invalid\"" })
        {
            File.WriteAllText(path, "{\"extra\":" + malformed + "}");
            var before = File.ReadAllBytes(path);
            WithNewExtraHost(f.Directory, host =>
            {
                RequireStorageFailure(() => host.Storage.LoadSettings(), "畸形新工具分节必须在读取时拒绝");
                RequireStorageFailure(() => host.Storage.SaveSettings(
                    new JsonObject { ["extra"] = new JsonObject { ["hotkey"] = "F12" } }),
                    "不能用有效候选覆盖原畸形配置");
                var updated = false;
                RequireStorageFailure(() => host.Storage.UpdateSettings(root => updated = true),
                    "畸形配置必须在调用修改函数前拒绝");
                Require(!updated, "不应向调用方提供畸形新工具分节");
            });
            Require(File.ReadAllBytes(path).SequenceEqual(before) && !File.Exists(path + ".bak"),
                "拒绝畸形分节时原字节和迁移备份状态必须保持");
        }

        File.WriteAllText(path, "{\"extra\":{\"hotkey\":\"F12\"}}");
        var validBefore = File.ReadAllBytes(path);
        WithNewExtraHost(f.Directory, host =>
            RequireStorageFailure(() => host.Storage.UpdateSettings(root => root["extra"] = 7),
                "候选新工具分节也必须验证为对象"));
        Require(File.ReadAllBytes(path).SequenceEqual(validBefore), "无效候选不得覆盖原有效分节");
    });

    private static void WithNewExtraHost(string directory, Action<ToolHost> check)
    {
        var registry = new ToolRegistry();
        registry.Register(new ExtraTool());
        var host = new ToolHost(registry, new StorageService(directory));
        try { check(host); }
        finally
        {
            host.Sound.Dispose();
            host.Notification.Dispose();
            host.Network.Dispose();
        }
    }

    private static void RequireStorageFailure(Action action, string message)
    {
        try { action(); }
        catch (StorageException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void WithFixture(Action<Fixture> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "tool-capabilities-" + Guid.NewGuid().ToString("N"));
            ToolHost? host = null;
            try
            {
                var craft = new CraftTool();
                var clicker = new ClickerTool();
                var keyLoop = new KeyLoopTool();
                var hideout = new HideoutTool();
                var extra = new ExtraTool();
                var settings = new SettingsTool(craft, clicker, keyLoop, hideout);
                var registry = new ToolRegistry();
                foreach (var tool in new ITool[] { craft, clicker, keyLoop, hideout, extra, settings }) registry.Register(tool);
                host = new ToolHost(registry, new StorageService(directory));
                settings.Initialize(host);
                check(new Fixture(host, settings, extra, directory));
            }
            catch (Exception error) { failure = error; }
            finally
            {
                host?.Sound.Dispose();
                host?.Notification.Dispose();
                host?.Network.Dispose();
                var settingsPath = Path.Combine(directory, "settings.json");
                if (File.Exists(settingsPath)) File.Delete(settingsPath);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(15)), "工具能力用例超时");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private sealed record Fixture(ToolHost Host, SettingsTool Settings, ExtraTool Extra, string Directory);

    private sealed class ExtraTool : ITool, IToolRuntime, IToolAuxiliaryState, IEditableHotkeyProvider
    {
        public string Id => "extra";
        public string Name => "附加工具";
        public string IconKey => "extra";
        public string Key { get; private set; } = "F12";
        public bool Running { get; set; }
        public bool Completed { get; set; } = true;
        public bool IsRunning => Running;
        public bool ShutdownCompleted => Completed;
        public int RefreshCount { get; private set; }
        public int AuxiliarySaves { get; private set; }
        public void RequestStop() => Running = false;
        public void SaveAuxiliaryState() => AuxiliarySaves++;
        public void RefreshSettingsPresentation() => RefreshCount++;
        public IReadOnlyList<EditableHotkey> GetEditableHotkeys() =>
            [new("extra_run", Name, HotkeyMode.Toggle, "F12", () => Key, key => Key = key)];
        public void Initialize(ToolHost host) { }
        public FrameworkElement CreatePage() => throw new InvalidOperationException("能力用例不创建页面");
        public void OnActivate() { }
        public void OnDeactivate() { }
        public void OnShutdown() => RequestStop();
        public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() =>
            [new() { Key = Key, DisplayName = Name, CheckForeground = true, Mode = HotkeyMode.Toggle, Handler = () => { } }];
        public void LoadSettings(JsonElement section) { }
        public void SaveSettings(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("hotkey", Key);
            writer.WriteEndObject();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
