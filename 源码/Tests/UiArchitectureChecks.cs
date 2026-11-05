using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Tools.Clicker;
using ShiKe.Tools.Craft;
using ShiKe.Tools.KeyLoop;

internal static class UiArchitectureChecks
{
    // 无页面的配置/生命周期验证；不启动任何输入任务、热键或网络请求。
    public static void Run()
    {
        var legacy = CraftPreset.FromJson(JsonNode.Parse("""
            {"primary_affixes":["生命",{"text":"护甲"}],"primary_hit_count":2,
             "secondary_affixes":[{"text":"抗性"}],"secondary_hit_count":1,
             "exclude_affixes":["反射"]}
            """)!.AsObject());
        Require(legacy.PrimaryAffixes.SequenceEqual(["生命", "护甲"]) &&
                legacy.SecondaryAffixes.SequenceEqual(["抗性"]) && legacy.ExcludeAffixes.Single() == "反射",
            "预设移出页面后仍须兼容字符串和字典词缀");
        Require(JsonNode.DeepEquals(legacy.ToJson(), CraftPreset.FromJson(legacy.ToJson()).ToJson()),
            "预设字段必须稳定往返");
        var rules = new CraftRules { Mode = CraftMode.Single, PrimaryHitCount = 3, SecondaryHitCount = 2 };
        rules.PrimaryAffixes.Add(new AffixRule { Text = "生命" });
        var snapshot = CraftPreset.FromRules(rules);
        rules.PrimaryAffixes.Clear();
        Require(snapshot.PrimaryHitCount == 1 && snapshot.SecondaryHitCount == 0 && snapshot.PrimaryAffixes.Single() == "生命",
            "单通货预设保持主1次0，并且快照不随后续编辑改变");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { WithIsolatedHost(CheckWithoutPage); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(20)), "无页面配置检查超时");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private static void CheckWithoutPage(ToolHost host)
    {
        var craft = new CraftTool();
        var clicker = new ClickerTool();
        var keyLoop = new KeyLoopTool();
        ITool[] tools = [craft, clicker, keyLoop];
        try
        {
            foreach (var tool in tools) tool.Initialize(host);
            clicker.IntervalMs = 81;
            clicker.MouseButton = ClickerMouseButton.Right;
            clicker.OnDeactivate();
            keyLoop.Slots[0].DelaySeconds = 2.7;
            keyLoop.Slots[0].Key = "NumPad2";
            keyLoop.OnDeactivate();
            var saved = host.Storage.LoadSettings();
            Require(saved["clicker"]!["interval_ms"]!.GetValue<int>() == 81 &&
                    saved["clicker"]!["button"]!.GetValue<string>() == "right",
                "未创建页面时连点配置也必须独立保存");
            Require(saved["keyloop"]!["slots"]![0]!["delay_s"]!.GetValue<double>() == 2.7 &&
                    saved["keyloop"]!["slots"]![0]!["key"]!.GetValue<string>() == "NumPad2",
                "未创建页面时循环配置也必须独立保存");

            craft.Rules.Mode = CraftMode.AltAug;
            craft.Rules.PrimaryHitCount = 1;
            craft.Rules.PrimaryAffixes.Add(new AffixRule { Text = "无页面预设" });
            craft.SavePreset("无页面预设");
            Require(craft.LoadPreset("无页面预设")!.PrimaryAffixes.Single() == "无页面预设",
                "预设数据保存和加载不应要求创建WPF页面");

            var before = ReadFiles(host.Storage.DataDir);
            craft.SaveEditedState();
            craft.SaveAuxiliaryState();
            Require(SameFiles(before, ReadFiles(host.Storage.DataDir)),
                "未创建编辑页不能因附属状态保存新增rules文件");
            host.Storage.ReadOnlyForUpdateValidation = true;
            foreach (var tool in tools) tool.OnShutdown();
            Require(SameFiles(before, ReadFiles(host.Storage.DataDir)),
                "关闭生命周期只停止任务，不能读页面或写只读配置");
        }
        finally { foreach (var tool in tools) tool.OnShutdown(); }
    }

    // 放在既有真实WPF外观验证末尾，复用同一个Application。Craft页此时保持挂载。
    public static void CheckPages(CraftPage craftPage, CraftTool craft, ClickerPage clickerPage,
        ClickerTool clicker, KeyLoopPage keyLoopPage, KeyLoopTool keyLoop, StorageService storage)
    {
        Require(craftPage.IsLoaded, "即时编辑验证需要已加载的洗装页");
        var rulesPath = Path.Combine(storage.DataDir, "rules.json");
        var beforeRules = File.ReadAllBytes(rulesPath);
        var beforeSettings = File.ReadAllBytes(Path.Combine(storage.DataDir, "settings.json"));
        Find<RadioButton>(craftPage, "Mode2Radio").IsChecked = true;
        Find<RadioButton>(craftPage, "PrimaryHit3").IsChecked = true;
        Find<RadioButton>(craftPage, "SecondaryHit3").IsChecked = true;
        Find<Slider>(craftPage, "DelaySlider").Value = 79;
        Find<RadioButton>(craftPage, "Mode2ScourAlch").IsChecked = true;
        Find<ToggleButton>(craftPage, "ExaltCheck").IsChecked = true;
        Require(craft.Rules.Mode == CraftMode.AltAug && craft.Rules.PrimaryHitCount == 3 &&
                craft.Rules.SecondaryHitCount == 3 && craft.DelayMs == 79 && craft.Mode2ScourAlch && craft.UseExalt,
            "编辑立即进入工具配置，不需要Collect或热键回读；超限数量仍可编辑");
        var input = Find<TextBox>(craftPage, "PrimaryInput");
        input.Text = "即时编辑验证";
        var addButton = ((Grid)input.Parent).Children.OfType<Button>().Single();
        addButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(craft.Rules.PrimaryAffixes.Any(rule => rule.Text == "即时编辑验证"),
            "新词缀必须立即进入无控件规则状态");
        var tags = Find<WrapPanel>(craftPage, "PrimaryTags");
        var added = (StackPanel)((Border)tags.Children[^1]).Child;
        var remove = added.Children.OfType<TextBlock>().Last();
        remove.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Require(craft.Rules.PrimaryAffixes.All(rule => rule.Text != "即时编辑验证"),
            "移除词缀必须立即更新规则");
        Find<RadioButton>(craftPage, "Mode1Radio").IsChecked = true;
        Find<ComboBox>(craftPage, "SingleCurrencyCombo").SelectedValue = Currency.Custom;
        Require(craft.Rules.RequiredPrimaryHits == 1 && craft.Rules.RequiredSecondaryHits == 0 &&
                craft.Rules.SingleCurrency == Currency.Custom,
            "单通货切换和币种选择必须即时同步并保留主1次0");
        Find<RadioButton>(craftPage, "Mode2Radio").IsChecked = true;
        Require(craft.Rules.PrimaryHitCount == 3 && craft.Rules.SecondaryHitCount == 3,
            "切回多通货仍保留本次页面隐藏的命中数量");
        Require(File.ReadAllBytes(rulesPath).SequenceEqual(beforeRules) &&
                File.ReadAllBytes(Path.Combine(storage.DataDir, "settings.json")).SequenceEqual(beforeSettings),
            "即时编辑不能擅自提前原规则/设置落盘时机");
        craft.OnDeactivate();
        var savedRules = storage.LoadRules()!;
        Require(savedRules["primary_hit_count"]!.GetValue<int>() == 3 &&
                storage.LoadSettings()["craft"]!["delay_ms"]!.GetValue<int>() == 79,
            "切页使用当前配置保存，无需回读Page");

        Find<Slider>(clickerPage, "IntervalSlider").Value = 73;
        Require(clicker.IntervalMs == 73, "连点滑条值必须即时进入配置");
        var row = (Grid)((Border)Find<StackPanel>(keyLoopPage, "SlotPanel").Children[0]).Child;
        var delay = row.Children.OfType<TextBox>().Single();
        delay.Text = "2.7";
        Require(keyLoop.Slots[0].DelaySeconds == 2.7, "未失焦的有效延时必须可供运行读取");
        delay.Text = "NaN";
        Require(keyLoop.Slots[0].DelaySeconds == 2.7, "非法编辑不应污染最近有效延时");
        delay.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Require(KeyLoopPage.TryParseDelay(delay.Text, out var restored) && restored == 2.7,
            "非法编辑提交后仍恢复有效值");

        WithIsolatedHost(host =>
        {
            host.Storage.SaveRules(JsonNode.Parse("""
                {"mode":2,"single_currency":"chaos","primary_hit_count":1,"secondary_hit_count":0,
                 "primary_affixes":["首帧原规则"],"secondary_affixes":[],"exclude_affixes":[]}
                """)!.AsObject());
            var tool = new CraftTool();
            tool.Initialize(host);
            try
            {
                var before = ReadFiles(host.Storage.DataDir);
                var page = (CraftPage)tool.CreatePage();
                Require(!page.IsLoaded, "首帧检查不能显示页面");
                Find<Slider>(page, "DelaySlider").Value = 99;
                tool.SaveAuxiliaryState();
                tool.OnShutdown();
                Require(tool.Rules.PrimaryAffixes.Single().Text == "首帧原规则" &&
                        SameFiles(before, ReadFiles(host.Storage.DataDir)),
                    "创建但未加载的页面不能覆盖原规则或触发关闭写入");
            }
            finally { tool.OnShutdown(); }
        });
    }

    private static T Find<T>(FrameworkElement root, string name) where T : class
        => root.FindName(name) as T ?? throw new InvalidOperationException("找不到控件: " + name);

    private static Dictionary<string, byte[]> ReadFiles(string path)
        => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static bool SameFiles(Dictionary<string, byte[]> left, Dictionary<string, byte[]> right)
        => left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var bytes) && item.Value.SequenceEqual(bytes));

    private static void WithIsolatedHost(Action<ToolHost> check)
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = Path.Combine(root, "ui-architecture-" + Guid.NewGuid().ToString("N"));
        ToolHost? host = null;
        try
        {
            host = new ToolHost(new ToolRegistry(), new StorageService(directory));
            check(host);
        }
        finally
        {
            if (host is not null)
            {
                host.Hotkeys.UnregisterAll();
                host.Sound.Dispose();
                host.Notification.Dispose();
                host.Network.Dispose();
            }
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("验证目录越界");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
