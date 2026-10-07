using System.Text.Json.Nodes;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Themes;
using ShiKe.Tools.Clicker;
using ShiKe.Tools.Craft;
using ShiKe.Tools.Hideout;
using ShiKe.Tools.KeyLoop;
using ShiKe.Tools.Settings;

// 实际创建 WPF 页面并走原选择/编辑/切页事件；不注册热键、不联网、不发送输入。
internal static class AppearanceChecks
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckWindow(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(20)), "WPF 外观检查超时");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private static void CheckWindow()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/拾刻;component/Themes/DefaultTheme.xaml", UriKind.Relative),
        });
        // ToolHost 默认目录属于本次测试程序集；断言隔离，再暂存三个文件的原字节。
        var registry = new ToolRegistry();
        var host = new ToolHost(registry);
        Require(host.Storage.DataDir.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) &&
                AppContext.BaseDirectory.Contains("内部用例", StringComparison.Ordinal), "界面验证必须使用内部用例目录");
        var originals = new[] { "settings.json", "rules.json", "coordinates.json",
            Path.Combine("presets", "appearance-check-baseline.json"), Path.Combine("presets", "appearance-check-limit.json") }.ToDictionary(
            name => Path.Combine(host.Storage.DataDir, name),
            name => File.Exists(Path.Combine(host.Storage.DataDir, name)) ? File.ReadAllBytes(Path.Combine(host.Storage.DataDir, name)) : null);
        MainWindow? window = null;
        var craft = new CraftTool();
        var clicker = new ClickerTool();
        var keyLoop = new KeyLoopTool();
        var hideout = new HideoutTool();
        var settings = new SettingsTool(craft, clicker, keyLoop, hideout);
        ITool[] tools = [craft, clicker, keyLoop, hideout, settings];
        try
        {
            host.Storage.SaveSettings(new JsonObject { ["host"] = new JsonObject { ["gradient_speed"] = 3 } });
            host.Storage.SaveRules(JsonNode.Parse("""
                {"mode":3,"single_currency":"chaos","primary_hit_count":2,"secondary_hit_count":1,
                 "primary_affixes":["最大生命","移动速度"],"secondary_affixes":["火焰抗性"],"exclude_affixes":["反射"]}
                """)!.AsObject());
            host.Storage.SavePreset("appearance-check-baseline", JsonNode.Parse("""
                {"primary_hit_count":2,"secondary_hit_count":1,"primary_affixes":["最大生命","移动速度"],
                 "secondary_affixes":["火焰抗性"],"exclude_affixes":["反射"]}
                """)!.AsObject());
            host.Storage.SavePreset("appearance-check-limit", JsonNode.Parse("""
                {"primary_hit_count":3,"secondary_hit_count":3,"primary_affixes":["生命","护甲","闪避"],
                 "secondary_affixes":["火抗","冰抗","电抗"],"exclude_affixes":[]}
                """)!.AsObject());
            host.Storage.SaveCoordinates(new Dictionary<string, Point>
            {
                ["item"] = new(840, 360), [Currency.Alteration] = new(420, 330),
                [Currency.Augmentation] = new(470, 330), [Currency.Regal] = new(520, 330),
            });
            craft.DelayMs = 55;
            craft.Mode2ScourAlch = true;
            craft.UseExalt = true;
            foreach (var tool in tools) { registry.Register(tool); tool.Initialize(host); }
            settings.LoadSettings(JsonSerializer.SerializeToElement(host.Storage.LoadSettings()["host"]));
            hideout.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = true }));
            keyLoop.Slots[0].Enabled = true;
            window = new MainWindow(registry, host) { ShowActivated = false, ShowInTaskbar = false };
            var settingsBeforeShow = File.ReadAllBytes(Path.Combine(host.Storage.DataDir, "settings.json"));
            CheckFirstFrameSwitches(window, window.Show);
            Require(Find<Slider>(window, "GradientSpeed").Value == 3, "窗口首次显示必须恢复保存的速度");
            Require(File.ReadAllBytes(Path.Combine(host.Storage.DataDir, "settings.json")).SequenceEqual(settingsBeforeShow), "初始化外观不应额外写盘");
            var nav = Find<ListBox>(window, "ToolList");
            var content = Find<ContentPresenter>(window, "ToolContent");
            var page = (CraftPage)content.Content;
            var expectedBody = Bounds(Find<FrameworkElement>(page, "PageBody"), window);
            var expectedHeader = Bounds(Find<Grid>(page, "PageHeader"), window);
            var firstSwitch = Find<ToggleButton>(page, "EnableToolSwitch");
            var firstTrack = (Border)firstSwitch.Template.FindName("Track", firstSwitch);
            var expectedTrack = Bounds(firstTrack, window);
            CheckPageFrame(page, window, expectedBody, expectedHeader, expectedTrack);
            Require(nav.Items.Count == tools.Length, "导航应包含全部注册工具");
            Require(Find<RadioButton>(page, "Mode3Radio").IsChecked == true, "原规则模式应恢复");
            Require((string)Find<ComboBox>(page, "SingleCurrencyCombo").SelectedValue == Currency.Chaos,
                    "持久保存的单通货必须恢复到新的下拉选项，不能重置成改造石");
            Require(Find<ToggleButton>(page, "ExaltCheck").IsChecked == true, "原崇高选项应恢复");
            Require(Find<Slider>(page, "DelaySlider").Value == 55, "原延迟应恢复");
            CheckColumns(page);
            CheckSwitchHitArea(Find<ToggleButton>(page, "EnableToolSwitch"));
            var delay = Find<Slider>(page, "DelaySlider");
            var previousDelay = delay.Value;
            Slider.IncreaseLarge.Execute(null, delay);
            Require(delay.Value > previousDelay, "新滑块模板必须保留轨道步进命令");
            delay.Value = previousDelay;
            var presets = Find<ComboBox>(page, "PresetCombo");
            presets.IsDropDownOpen = true;
            Pump();
            var popup = (Popup)presets.Template.FindName("PART_Popup", presets);
            Require(popup.IsOpen && Descendants(popup.Child).OfType<ComboBoxItem>().Count() == presets.Items.Count,
                    "预设下拉框必须能真实展开并呈现选项");
            presets.IsDropDownOpen = false;
            Snapshot(window, "T11-WPF-Mode3.png");
            CheckInteractiveSwitchAnimation();
            presets.SelectedItem = "appearance-check-limit";
            Pump();
            Require(Find<Border>(page, "HitCountWarning").IsVisible &&
                    Find<RadioButton>(page, "PrimaryHit3").IsChecked == true &&
                    Find<RadioButton>(page, "SecondaryHit3").IsChecked == true,
                    "加载 3+3 预设必须原样恢复数量并立即显示当前模式上限");
            presets.SelectedItem = "appearance-check-baseline";
            Pump();
            Require(Find<Border>(page, "HitCountWarning").Visibility == Visibility.Collapsed,
                    "切回有效预设必须清除超限提示");
            CheckCraftSelections(page, craft, window);

            var panels = new[] { "ModePanel", "CurrencyPanel", "RulesPanel" }.Select(name => Find<Border>(page, name)).ToArray();
            Require(panels.All(panel => panel.Background is LinearGradientBrush { IsFrozen: false }), "面板应拥有独立可动画的画刷");
            Require(!ReferenceEquals(panels[0].Background, app.FindResource("ModePanelBackground")), "动画不能改变共享主题资源");
            window.Activate();
            Pump();
            Require(window.IsActive, "隔离窗口未激活，流动检查未完成");
            if (SystemParameters.ClientAreaAnimation)
            {
                Require(panels.All(FlowingGradient.IsAnimating), "窗口激活后渐变应流动");
                var brush = (LinearGradientBrush)panels[0].Background;
                var before = brush.StartPoint;
                Pump(180);
                Require(brush.StartPoint != before, "真实背景画刷应随时间缓慢移动");
                var speedSlider = Find<Slider>(window, "GradientSpeed");
                speedSlider.Value = 0;
                Pump(100);
                CheckRestoredSpeed(host, 0);
                Require(panels.All(panel => !FlowingGradient.IsAnimating(panel)), "速度 0 必须停止全部面板");
                var paused = brush.StartPoint;
                Pump(180);
                Require(brush.StartPoint == paused, "暂停后画刷不能继续移动");
                speedSlider.Value = 1;
                Pump(100);
                var slowBefore = brush.StartPoint;
                Pump(180);
                var slowTravel = (brush.StartPoint - slowBefore).Length;
                speedSlider.Value = 5;
                Pump(100);
                CheckRestoredSpeed(host, 5);
                var fastBefore = brush.StartPoint;
                Pump(180);
                Require((brush.StartPoint - fastBefore).Length > slowTravel * 1.2, "速度 5 应比速度 1 移动更快");
                var visibleBefore = BackgroundPixels(panels[0]);
                Snapshot(window, "T11-WPF-Flow-before.png");
                Pump(1600);
                var visibleAfter = BackgroundPixels(panels[0]);
                Snapshot(window, "T11-WPF-Flow-after.png");
                var pixelChange = visibleBefore.Zip(visibleAfter, (left, right) => Math.Abs(left - right)).Average();
                Console.WriteLine($"INFO T11 实际背景采样平均色阶变化={pixelChange:0.00}");
                Require(pixelChange > 1, "流动必须改变实际渲染背景颜色，不能只有内部坐标变化");
                speedSlider.Value = 2;
                Pump();
                Require(panels.All(FlowingGradient.IsAnimating), "速度恢复后应恢复全部面板");
                var other = new Window { Width = 140, Height = 80, ShowInTaskbar = false, Title = "隔离焦点检查" };
                try
                {
                    other.Show(); other.Activate(); Pump();
                    Require(!window.IsActive && panels.All(FlowingGradient.IsAnimating), "可见但失焦的窗口仍应流动，便于并排观察");
                }
                finally { other.Close(); }
                window.Activate(); Pump();
                Require(panels.All(FlowingGradient.IsAnimating), "恢复窗口焦点不能中断动画");
            }
            else Require(panels.All(panel => !FlowingGradient.IsAnimating(panel)), "必须尊重系统关闭动画的偏好");

            Find<RadioButton>(page, "Mode2Radio").IsChecked = true;
            Pump();
            Require(Find<StackPanel>(page, "Mode2Frame").IsVisible && !Find<StackPanel>(page, "Mode3Frame").IsVisible, "Mode 2 子模式显示错误");
            Require(Find<RadioButton>(page, "Mode2ScourAlch").IsChecked == true, "切模式不能重置子模式");
            CheckCurrencies(page, CraftMode.AltAug);
            Snapshot(window, "T11-WPF-Mode2.png");
            Find<RadioButton>(page, "Mode1Radio").IsChecked = true;
            Pump();
            CheckCurrencies(page, CraftMode.Single);
            Require(!Find<StackPanel>(page, "Mode2Frame").IsVisible && !Find<StackPanel>(page, "Mode3Frame").IsVisible, "Mode 1 不能残留子模式");
            Snapshot(window, "T11-WPF-Mode1.png");
            Find<RadioButton>(page, "Mode3Radio").IsChecked = true;
            Pump();
            CheckCurrencies(page, CraftMode.AltAugRegal);

            Find<TextBox>(page, "PrimaryInput").Text = "冰霜抗性";
            Descendants(page).OfType<Button>().Single(button => (string?)button.Tag == "primary")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Find<Slider>(page, "DelaySlider").Value = 77;
            CheckFirstFrameSwitches(window, () => nav.SelectedIndex = 1);
            CheckPageFrame((FrameworkElement)content.Content, window, expectedBody, expectedHeader, expectedTrack);
            var clickerPage = (ClickerPage)content.Content;
            clicker.Hotkey = "Ctrl+F8";
            clicker.HoldHotkey = "Alt+F11";
            clickerPage.RefreshSettingsPresentation();
            Require(Find<TextBlock>(clickerPage, "HotkeyHint").Text.Contains("Ctrl+F8", StringComparison.Ordinal) &&
                    Find<TextBlock>(clickerPage, "HotkeyHint").Text.Contains("Alt+F11", StringComparison.Ordinal) &&
                    Find<TextBlock>(clickerPage, "HotkeyHint").Text.Contains("松开停止", StringComparison.Ordinal),
                    "页头操作提示必须解释按住行为并刷新实际热键");
            Require(panels.All(panel => !FlowingGradient.IsAnimating(panel)), "切走后缓存页面不能继续动画");
            Require(craft.Rules.PrimaryAffixes.Any(rule => rule.Text == "冰霜抗性") && craft.DelayMs == 77, "切页必须收集并保存编辑值");
            var saved = host.Storage.LoadRules()!;
            Require(saved["primary_affixes"]!.AsArray().Any(value =>
                value is JsonValue text && text.GetValue<string>() == "冰霜抗性" ||
                value is JsonObject item && item["text"]?.GetValue<string>() == "冰霜抗性"), "规则文件必须保存新增词缀");
            Snapshot(window, "T11-WPF-Clicker.png");
            for (var index = 2; index < tools.Length; index++)
            {
                CheckFirstFrameSwitches(window, () => nav.SelectedIndex = index);
                Require(content.Content == tools[index].CreatePage(), "工具页面应复用原缓存");
                CheckPageFrame((FrameworkElement)content.Content, window, expectedBody, expectedHeader, expectedTrack);
                if (index == 4)
                {
                    var settingsPage = (FrameworkElement)content.Content;
                    CheckSettingsSwitches(settingsPage);
                    CheckSettingsPanels(settingsPage, window);
                }
                if (index == 2)
                {
                    var loopPage = (FrameworkElement)content.Content;
                    Find<Button>(loopPage, "MoreButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Pump();
                    Require(Find<StackPanel>(loopPage, "SlotPanel").Children.Cast<Border>().All(row => row.IsVisible), "加载更多应显示十个槽位");
                    var scroll = Descendants(loopPage).OfType<ScrollViewer>().First();
                    var more = Find<Button>(loopPage, "MoreButton");
                    var loopViewport = Descendants(scroll).OfType<ScrollContentPresenter>().First();
                    Require(more.TranslatePoint(new Point(0, more.ActualHeight), loopViewport).Y <= loopViewport.ActualHeight + 1,
                            "默认尺寸展开十槽后，收起按钮仍须完整可见");
                }
                Snapshot(window, $"T11-WPF-{tools[index].Id}.png");
            }
            CheckFirstFrameSwitches(window, () => nav.SelectedIndex = 0);
            Require(ReferenceEquals(content.Content, page), "切回洗装页应复用同一实例");
            Require(Find<WrapPanel>(page, "PrimaryTags").Children.Count == 3, "切回不能重建或重复添加词缀");
            Require(Find<ItemsControl>(page, "CurrencyGrid").Items.Count == Currency.All.Length, "切回不能重复添加通货");
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Pump();
            CheckColumns(page);
            foreach (var input in new[] { "PrimaryInput", "SecondaryInput", "ExcludeInput" })
                Require(Find<TextBox>(page, input).ActualWidth >= 100, "最小尺寸下词缀输入框过窄");
            var rulesScroll = Descendants(panels[2]).OfType<ScrollViewer>().First();
            // 增加示例标签确保小窗口溢出；通过实际路由的滚轮事件访问隐藏内容。
            for (var index = 0; index < 8; index++)
            {
                Find<TextBox>(page, "PrimaryInput").Text = $"额外词缀{index}";
                Descendants(page).OfType<Button>().Single(button => (string?)button.Tag == "primary")
                    .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            Pump();
            rulesScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
            });
            Pump();
            Require(rulesScroll.VerticalOffset > 0, "隐藏滚动条后鼠标滚轮仍须可用");
            rulesScroll.ScrollToBottom(); Pump();
            Require(rulesScroll.VerticalOffset > 0, "最小窗口下规则滚动条必须可用");
            var exclude = Find<TextBox>(page, "ExcludeInput");
            var viewport = Descendants(rulesScroll).OfType<ScrollContentPresenter>().First();
            Require(exclude.TranslatePoint(new Point(0, exclude.ActualHeight), viewport).Y <= viewport.ActualHeight + 1,
                    "滚动到底部后排除词缀输入必须完整可见");
            rulesScroll.ScrollToTop(); Pump();
            Snapshot(window, "T11-WPF-Minimum.png");
            for (var index = 1; index < tools.Length; index++)
            {
                nav.SelectedIndex = index;
                Pump();
                var smallPage = (FrameworkElement)content.Content;
                if (index == 4)
                {
                    CheckSettingsPanels(smallPage, window);
                    CheckSettingsSwitches(smallPage);
                    var general = Find<Border>(smallPage, "GeneralSettingsPanel");
                    var scroll = Descendants(general).OfType<ScrollViewer>().First();
                    scroll.ScrollToBottom(); Pump();
                    Require(Find<ToggleButton>(smallPage, "KeyLoopNotificationCheck").IsVisible,
                            "最小窗口仍须能滚动访问右栏最后一个开关");
                    Snapshot(window, "T11-WPF-Settings-minimum.png");
                }
                else CheckHeaderHint(smallPage, window);
            }
            nav.SelectedIndex = 0; Pump();
            window.ApplyAds([new AdItem { Location = "top", Type = "text", Text = "测试推广一" },
                             new AdItem { Location = "top", Type = "text", Text = "测试推广二" },
                             new AdItem { Location = "bottom", Type = "text", Text = "测试推广三" }]);
            Pump();
            Require(Find<StackPanel>(window, "TopAdPanel").Children.Count == 2 && Find<Button>(window, "BottomAdButton").IsVisible,
                    "广告区仍应承载多条 top 与一条 bottom 数据");
            window.WindowState = WindowState.Minimized;
            Pump();
            Require(panels.All(panel => !FlowingGradient.IsAnimating(panel)), "最小化后应暂停动画");
            window.WindowState = WindowState.Normal;
            window.Hide();
            Pump();
            Require(panels.All(panel => !FlowingGradient.IsAnimating(panel)), "隐藏后应暂停动画");
        }
        finally
        {
            window?.Close();
            foreach (var tool in tools) tool.OnShutdown();
            host.Hotkeys.UnregisterAll(); host.Sound.Dispose(); host.Notification.Dispose(); host.Network.Dispose();
            foreach (var (path, bytes) in originals)
            {
                if (bytes is null) File.Delete(path);
                else File.WriteAllBytes(path, bytes);
            }
            app.Shutdown();
        }
    }

    private static void CheckFirstFrameSwitches(Window window, Action display)
    {
        var observed = false;
        Exception? failure = null;
        EventHandler? inspect = null;
        inspect = (_, _) =>
        {
            // Rendering 为应用全局事件；忽略其他测试窗口和本窗口尚未显示控件的帧。
            if (!window.IsVisible || !window.IsLoaded) return;
            try
            {
                var switches = Descendants(window).OfType<ToggleButton>()
                    .Where(toggle => toggle.IsVisible && toggle.Template?.FindName("Track", toggle) is Border).ToArray();
                if (switches.Length == 0) return;
                CompositionTarget.Rendering -= inspect;
                observed = true;
                foreach (var toggle in switches) CheckSwitchPose(toggle);
            }
            catch (Exception error) { failure = error; }
        };
        CompositionTarget.Rendering += inspect;
        try { display(); Pump(80); }
        finally { CompositionTarget.Rendering -= inspect; }
        Require(observed, "未观察到切页后的真实渲染帧");
        if (failure is not null) throw new InvalidOperationException("开关首次显示不应补播状态动画", failure);
    }

    private static void CheckSwitchPose(ToggleButton toggle)
    {
        var transform = (TranslateTransform)((System.Windows.Shapes.Ellipse)toggle.Template.FindName("Thumb", toggle)).RenderTransform;
        var track = (Border)toggle.Template.FindName("Track", toggle);
        var expected = toggle.IsChecked == true ? 19 : 0;
        var color = toggle.IsChecked == true ? Color.FromRgb(0x49, 0x90, 0xB1) : Color.FromRgb(0xB7, 0xC4, 0xCE);
        var actualColor = ((SolidColorBrush)track.Background).Color;
        // ColorAnimation 的 ScRGB 浮点值可能微有差异；比较实际显示的 ARGB 通道。
        Require(Math.Abs(transform.X - expected) < .01 && actualColor.A == color.A && actualColor.R == color.R &&
                actualColor.G == color.G && actualColor.B == color.B,
                $"首个可见帧必须呈现保存状态：{toggle.Name}/{toggle.Content} checked={toggle.IsChecked}, X={transform.X:0.000}/{expected}, color={((SolidColorBrush)track.Background).Color}/{color}");
    }

    private static void CheckInteractiveSwitchAnimation()
    {
        var toggle = new ToggleButton { Content = "测试", IsChecked = true, Style = (Style)Application.Current.FindResource("ModernSwitchStyle"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var probe = new Window { Width = 180, Height = 110, ShowActivated = false, ShowInTaskbar = false, Content = toggle };
        try
        {
            CheckFirstFrameSwitches(probe, probe.Show);
            double Offset() => ((TranslateTransform)((System.Windows.Shapes.Ellipse)toggle.Template.FindName("Thumb", toggle)).RenderTransform).X;
            toggle.IsChecked = false;
            Pump(60);
            Require(Offset() is > 0 and < 19, $"手动关闭开关仍须有平滑过渡，实际位置={Offset():0.000}");
            Pump(160); CheckSwitchPose(toggle);
            toggle.IsChecked = true;
            Pump(60);
            Require(Offset() is > 0 and < 19, $"手动启用开关仍须有平滑过渡，实际位置={Offset():0.000}");
            Pump(160); CheckSwitchPose(toggle);
        }
        finally { probe.Close(); }
    }

    private static void CheckRestoredSpeed(ToolHost current, int expected)
    {
        var saved = current.Storage.LoadSettings();
        Require(saved["host"]?["gradient_speed"]?.GetValue<int>() == expected, "窗口调整速度后应立即写入配置");
        var registry = new ToolRegistry();
        var reopenedHost = new ToolHost(registry);
        var settings = new SettingsTool(new CraftTool(), new ClickerTool(), new KeyLoopTool(), new HideoutTool());
        settings.Initialize(reopenedHost);
        settings.LoadSettings(JsonSerializer.SerializeToElement(saved["host"]));
        var reopened = new MainWindow(registry, reopenedHost) { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            reopened.Show(); Pump();
            Require(Find<Slider>(reopened, "GradientSpeed").Value == expected && FlowingGradient.GetSpeed(reopened) == expected,
                    "重新创建窗口必须恢复实际速度绑定，包括停止档");
        }
        finally
        {
            reopened.Close();
            reopenedHost.Hotkeys.UnregisterAll(); reopenedHost.Sound.Dispose(); reopenedHost.Notification.Dispose(); reopenedHost.Network.Dispose();
        }
    }

    private static void CheckCurrencies(CraftPage page, CraftMode mode)
    {
        var visible = Find<ItemsControl>(page, "CurrencyGrid").Items.Cast<Border>().Where(border => border.IsVisible).ToArray();
        Require(visible.Length == Currency.ModeCurrencies[mode].Length, "通货显示子集与当前模式不一致");
        foreach (var card in visible)
            foreach (var button in Descendants(card).OfType<Button>().Where(button => button.IsVisible))
                Require(button.ActualWidth > 18 && button.TranslatePoint(new Point(button.ActualWidth, 0), card).X <= card.ActualWidth + 1,
                        "坐标按钮不能溢出通货卡片");
    }

    private static void CheckCraftSelections(CraftPage page, CraftTool craft, Window window)
    {
        var editor = Find<Expander>(page, "HitCountEditor");
        var warning = Find<Border>(page, "HitCountWarning");
        Find<RadioButton>(page, "Mode2Radio").IsChecked = true;
        Pump();
        Require(warning.IsVisible && editor.IsExpanded, "切换 Mode2 必须立即提示继承的 2+1 超限");
        Require(Find<RadioButton>(page, "PrimaryHit2").IsChecked == true &&
                Find<RadioButton>(page, "SecondaryHit1").IsChecked == true, "切模式不能静默削减命中要求");
        Find<RadioButton>(page, "PrimaryHit1").IsChecked = true;
        Require(warning.Visibility == Visibility.Collapsed, "修正到 1+1 后应清除提示");
        Find<RadioButton>(page, "PrimaryHit3").IsChecked = true;
        Require(Find<RadioButton>(page, "PrimaryHit1").IsChecked == true && warning.Visibility == Visibility.Visible,
                "有效模式下增加到超限必须保留旧值并给出内联提示");
        Find<RadioButton>(page, "SecondaryHit0").IsChecked = true;
        Find<RadioButton>(page, "Mode1Radio").IsChecked = true;
        Pump();
        Require(!editor.IsExpanded && Find<RadioButton>(page, "PrimaryHit1").IsChecked == true &&
                Find<RadioButton>(page, "SecondaryHit0").IsChecked == true, "单通货常用 1+0 应以摘要呈现，按需展开");
        Snapshot(window, "T11-WPF-Single-default.png");
        editor.IsExpanded = true;
        Find<RadioButton>(page, "PrimaryHit3").IsChecked = true;
        Find<RadioButton>(page, "SecondaryHit3").IsChecked = true;
        var currency = Find<ComboBox>(page, "SingleCurrencyCombo");
        foreach (var key in Currency.ModeCurrencies[CraftMode.Single])
        {
            currency.SelectedValue = key;
            page.CollectRulesFromUi();
            Require(craft.Rules.SingleCurrency == key, "通货下拉框必须真正更新运行规则");
        }
        var cards = Find<ItemsControl>(page, "CurrencyGrid").Items.Cast<Border>().ToArray();
        foreach (var card in cards.Where(card => card.IsVisible))
        {
            var name = Descendants(card).OfType<TextBlock>().First();
            name.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Require(card.BorderThickness == new Thickness(1) && ReferenceEquals(card.BorderBrush, page.FindResource("CardBorder")),
                    "通货坐标卡片不应再有选中外观");
            Require((string)currency.SelectedValue == Currency.Custom, "点击坐标卡片标题不能改变使用通货");
        }
        Find<RadioButton>(page, "Mode2Radio").IsChecked = true;
        Pump();
        Require(warning.IsVisible && Find<TextBlock>(page, "HitCountWarningText").Text.Contains("合计 6", StringComparison.Ordinal),
                "3+3 切 Mode2 后必须立即给出当前数量与上限");
        Find<RadioButton>(page, "PrimaryHit1").IsChecked = true;
        Require(warning.Visibility == Visibility.Visible, "减少为 1+3 后超限提示仍须保留");
        Find<RadioButton>(page, "SecondaryHit1").IsChecked = true;
        Require(warning.Visibility == Visibility.Collapsed, "逐步修正为 1+1 后应清除提示");
        Find<RadioButton>(page, "Mode1Radio").IsChecked = true;
        editor.IsExpanded = true;
        Find<RadioButton>(page, "PrimaryHit3").IsChecked = true;
        Find<RadioButton>(page, "SecondaryHit3").IsChecked = true;
        Find<RadioButton>(page, "Mode3Radio").IsChecked = true;
        Pump();
        Require(warning.IsVisible && Find<TextBlock>(page, "HitCountWarningText").Text.Contains("最多 3", StringComparison.Ordinal),
                "3+3 切 Mode3 后必须立即提示，不能只在启动时发现");
        Snapshot(window, "T11-WPF-Mode-limit.png");
        Find<RadioButton>(page, "PrimaryHit2").IsChecked = true;
        Find<RadioButton>(page, "SecondaryHit1").IsChecked = true;
        currency.SelectedValue = Currency.Alteration;
        page.CollectRulesFromUi();
        Require(craft.Rules.Validate().Ok && warning.Visibility == Visibility.Collapsed,
                "Mode3 恢复 2+1 后仍须符合原运行规则");
    }

    private static void CheckColumns(CraftPage page)
    {
        var panels = new[] { "ModePanel", "CurrencyPanel", "RulesPanel" }.Select(name => Find<Border>(page, name)).ToArray();
        Require(panels.All(panel => panel.ActualWidth > 250), "三栏可用宽度不足");
        Require(panels.Max(panel => panel.ActualWidth) - panels.Min(panel => panel.ActualWidth) < 2, "三栏应等宽");
        Require(panels[0].TranslatePoint(new Point(), page).Y == panels[2].TranslatePoint(new Point(), page).Y, "三栏顶部应对齐");
    }

    private static Rect Bounds(FrameworkElement element, Window window)
        => new(element.TranslatePoint(new Point(), window), new Size(element.ActualWidth, element.ActualHeight));

    private static void CheckPageFrame(FrameworkElement page, Window window, Rect expectedBody, Rect expectedHeader, Rect expectedTrack)
    {
        var body = Bounds(Find<FrameworkElement>(page, "PageBody"), window);
        var header = Bounds(Find<Grid>(page, "PageHeader"), window);
        Require(Math.Abs(body.X - expectedBody.X) < 1 && Math.Abs(body.Y - expectedBody.Y) < 1 &&
                Math.Abs(body.Width - expectedBody.Width) < 1 && Math.Abs(body.Height - expectedBody.Height) < 1,
                "切换工具时内容区域的位置、宽高必须一致");
        Require(header == expectedHeader, "页面标题区域必须一致");
        Require(Descendants(page).OfType<ScrollBar>().All(bar => bar.Orientation != Orientation.Vertical || !bar.IsVisible),
                "页面不能显示右侧滚动条");
        var enable = page.FindName("EnableToolSwitch") as ToggleButton ?? page.FindName("EnableCheck") as ToggleButton;
        if (enable is null) return; // 设置页不提供工具启用开关。
        var track = Bounds((Border)enable.Template.FindName("Track", enable), window);
        Require(Math.Abs(track.Right - expectedTrack.Right) < 1 && Math.Abs(track.Y - expectedTrack.Y) < 1,
                "四个工具的启用开关必须对齐到同一位置");
        CheckHeaderHint(page, window);
    }

    private static void CheckHeaderHint(FrameworkElement page, Window window)
    {
        var header = Find<Grid>(page, "PageHeader");
        var hint = header.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 1);
        var title = header.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 0);
        var enable = header.Children.OfType<ToggleButton>().Single();
        var hintBounds = Bounds(hint, window);
        Require(hint.IsVisible && hint.FontSize >= 14 && hint.Opacity == 1 && hint.Text.Length > 0,
                "操作提示必须放大并保持可见");
        Require(hintBounds.Left > Bounds(title, window).Right && hintBounds.Right < Bounds(enable, window).Left &&
                hintBounds.Top >= Bounds(header, window).Top && hintBounds.Bottom <= Bounds(header, window).Bottom + 1,
                "操作提示须位于标题和开关中间，不能溢出页头");
    }

    private static void CheckSettingsPanels(FrameworkElement page, Window window)
    {
        var left = Bounds(Find<Border>(page, "HotkeyPanel"), window);
        var right = Bounds(Find<Border>(page, "GeneralSettingsPanel"), window);
        Require(Math.Abs(left.Width - right.Width) < 1 && Math.Abs(left.Height - right.Height) < 1 &&
                Math.Abs(left.Top - right.Top) < 1 && Math.Abs(left.Bottom - right.Bottom) < 1,
                "设置左右两个模块须等宽、等高且上下对齐");
        Require(Find<Grid>(page, "PageBody").Children.OfType<Border>().Count() == 2,
                "设置页面只应存在两块主要模块");
    }

    private static void CheckSettingsSwitches(FrameworkElement page)
    {
        var switches = new[] { "AutoDetectCheck", "CraftSoundCheck", "CraftPopupCheck", "ClickerNotificationCheck", "KeyLoopNotificationCheck" }
            .Select(name => Find<ToggleButton>(page, name)).ToArray();
        var rightEdges = switches.Select(toggle =>
        {
            var track = (Border)toggle.Template.FindName("Track", toggle);
            return track.TranslatePoint(new Point(track.ActualWidth, 0), page).X;
        }).ToArray();
        Require(rightEdges.Max() - rightEdges.Min() < 1, "设置中的全部开关必须在行尾对齐");
    }

    private static byte[] BackgroundPixels(Border panel)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        // 左侧空白带避开卡片和文字，采样当前渐变的有色区域。
        var pixels = new byte[8 * 20 * 4];
        bitmap.CopyPixels(new Int32Rect(5, (int)(panel.ActualHeight * .45), 8, 20), pixels, 8 * 4, 0);
        return pixels;
    }

    private static void CheckSwitchHitArea(ToggleButton toggle)
    {
        var track = (Border)toggle.Template.FindName("Track", toggle);
        var trackCenter = track.TranslatePoint(new Point(track.ActualWidth / 2, track.ActualHeight / 2), toggle);
        Require(toggle.InputHitTest(trackCenter) is not null, "开关轨道必须可点击");
        Require(toggle.InputHitTest(new Point(2, toggle.ActualHeight / 2)) is null, "标签不能扩大开关点击范围");
    }

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement
        => root.FindName(name) as T ?? throw new InvalidOperationException($"缺少控件 {name}");

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Pump(int milliseconds = 60)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Snapshot(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SHIKE_UI_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        Pump(160); // 导出时让开关的短动画结束，避免把过渡态当作选中状态。
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 1.5), (int)Math.Ceiling(root.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name.Replace("T11-WPF", "T11-v38", StringComparison.Ordinal)));
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
