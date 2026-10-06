using System.Text.Json.Nodes;
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
        var originals = new[] { "settings.json", "rules.json", "coordinates.json" }.ToDictionary(
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
            host.Storage.SaveSettings(new JsonObject());
            host.Storage.SaveRules(JsonNode.Parse("""
                {"mode":3,"single_currency":"alteration","primary_hit_count":2,"secondary_hit_count":1,
                 "primary_affixes":["最大生命","移动速度"],"secondary_affixes":["火焰抗性"],"exclude_affixes":["反射"]}
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
            window = new MainWindow(registry, host) { ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            Pump();
            var nav = Find<ListBox>(window, "ToolList");
            var content = Find<ContentPresenter>(window, "ToolContent");
            var page = (CraftPage)content.Content;
            Require(nav.Items.Count == tools.Length, "导航应包含全部注册工具");
            Require(Find<RadioButton>(page, "Mode3Radio").IsChecked == true, "原规则模式应恢复");
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
                var motionSwitch = Find<ToggleButton>(window, "GradientSwitch");
                motionSwitch.IsChecked = false;
                Pump();
                Require(panels.All(panel => !FlowingGradient.IsAnimating(panel)), "暂停按钮必须暂停全部面板");
                var paused = brush.StartPoint;
                Pump(180);
                Require(brush.StartPoint == paused, "暂停后画刷不能继续移动");
                motionSwitch.IsChecked = true;
                Pump();
                Require(panels.All(FlowingGradient.IsAnimating), "恢复按钮必须恢复全部面板");
                var other = new Window { Width = 140, Height = 80, ShowInTaskbar = false, Title = "隔离焦点检查" };
                try
                {
                    other.Show(); other.Activate(); Pump();
                    Require(!window.IsActive && panels.All(panel => !FlowingGradient.IsAnimating(panel)), "失焦后应暂停动画");
                }
                finally { other.Close(); }
                window.Activate(); Pump();
                Require(panels.All(FlowingGradient.IsAnimating), "恢复窗口焦点后应恢复动画");
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
            nav.SelectedIndex = 1;
            Pump();
            Require(panels.All(panel => !FlowingGradient.IsAnimating(panel)), "切走后缓存页面不能继续动画");
            Require(craft.Rules.PrimaryAffixes.Any(rule => rule.Text == "冰霜抗性") && craft.DelayMs == 77, "切页必须收集并保存编辑值");
            var saved = host.Storage.LoadRules()!;
            Require(saved["primary_affixes"]!.AsArray().Any(value =>
                value is JsonValue text && text.GetValue<string>() == "冰霜抗性" ||
                value is JsonObject item && item["text"]?.GetValue<string>() == "冰霜抗性"), "规则文件必须保存新增词缀");
            Snapshot(window, "T11-WPF-Clicker.png");
            for (var index = 2; index < tools.Length; index++)
            {
                nav.SelectedIndex = index;
                Pump();
                Require(content.Content == tools[index].CreatePage(), "工具页面应复用原缓存");
                if (index == 2)
                {
                    var loopPage = (FrameworkElement)content.Content;
                    Find<Button>(loopPage, "MoreButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Pump();
                    Require(Find<StackPanel>(loopPage, "SlotPanel").Children.Cast<Border>().All(row => row.IsVisible), "加载更多应显示十个槽位");
                }
                Snapshot(window, $"T11-WPF-{tools[index].Id}.png");
            }
            nav.SelectedIndex = 0;
            Pump();
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
            rulesScroll.ScrollToBottom(); Pump();
            Require(rulesScroll.VerticalOffset > 0, "最小窗口下规则滚动条必须可用");
            var exclude = Find<TextBox>(page, "ExcludeInput");
            var viewport = Descendants(rulesScroll).OfType<ScrollContentPresenter>().First();
            Require(exclude.TranslatePoint(new Point(0, exclude.ActualHeight), viewport).Y <= viewport.ActualHeight + 1,
                    "滚动到底部后排除词缀输入必须完整可见");
            rulesScroll.ScrollToTop(); Pump();
            Snapshot(window, "T11-WPF-Minimum.png");
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

    private static void CheckCurrencies(CraftPage page, CraftMode mode)
    {
        var visible = Find<ItemsControl>(page, "CurrencyGrid").Items.Cast<Border>().Where(border => border.IsVisible).ToArray();
        Require(visible.Length == Currency.ModeCurrencies[mode].Length, "通货显示子集与当前模式不一致");
        foreach (var card in visible)
            foreach (var button in Descendants(card).OfType<Button>().Where(button => button.IsVisible))
                Require(button.ActualWidth > 18 && button.TranslatePoint(new Point(button.ActualWidth, 0), card).X <= card.ActualWidth + 1,
                        "坐标按钮不能溢出通货卡片");
    }

    private static void CheckColumns(CraftPage page)
    {
        var panels = new[] { "ModePanel", "CurrencyPanel", "RulesPanel" }.Select(name => Find<Border>(page, name)).ToArray();
        Require(panels.All(panel => panel.ActualWidth > 250), "三栏可用宽度不足");
        Require(panels.Max(panel => panel.ActualWidth) - panels.Min(panel => panel.ActualWidth) < 2, "三栏应等宽");
        Require(panels[0].TranslatePoint(new Point(), page).Y == panels[2].TranslatePoint(new Point(), page).Y, "三栏顶部应对齐");
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
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 1.5), (int)Math.Ceiling(root.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
