using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ShiKe.Host;
using ShiKe.Presentation;
using ShiKe.Tools.Clicker;
using ShiKe.Tools.Craft;
using ShiKe.Tools.KeyLoop;
using ShiKe.Tools.Settings;

internal static class UiPerformanceChecks
{
    public static void DispatcherCoalescesLatestState()
    {
        var queued = new Queue<Action>();
        var rendered = new List<int>();
        var states = new LatestStateDispatcher<int>(action => { queued.Enqueue(action); return true; }, rendered.Add);
        for (var value = 0; value < 100; value++) states.Publish(value);
        Require(queued.Count == 0 && rendered.Count == 0, "隐藏时只保留最新状态，不排队也不绘制");
        states.SetActive(true);
        Require(rendered.SequenceEqual([99]), "重显必须立即绘制隐藏期间最后状态");
        for (var value = 100; value < 200; value++) states.Publish(value);
        Require(queued.Count == 1, "可见期间连续状态只应排队一次");
        queued.Dequeue()();
        Require(rendered.SequenceEqual([99, 199]), "排队回调必须读取最新值，而非最早入队时的旧值");

        states.Publish(200); // 留下上一轮可见期间的排队回调。
        states.SetActive(false);
        states.Publish(201);
        states.SetActive(true);
        states.Publish(202); // 新一轮回调不能被旧一轮回调解除排队状态。
        queued.Dequeue()();
        Require(rendered[^1] == 201, "旧可见轮次回调不能覆盖刚恢复的最新状态");
        states.Publish(203);
        Require(queued.Count == 1, "旧回调不能导致当前轮次重复排队");
        queued.Dequeue()();
        Require(rendered.SequenceEqual([99, 199, 201, 203]), "可见性切换后必须仍只绘制最新状态");

        states.Publish(204);
        states.Dispose();
        states.Publish(205);
        states.SetActive(true);
        queued.Dequeue()();
        Require(rendered[^1] == 203 && queued.Count == 0, "关闭后的状态与残留回调不能再绘制");

        // 可见性已启用、同步恢复尚未开始时，后台已经取得异步队列槽。
        // 同步恢复不能清除此槽，否则后续发布会在同一代次再排入一个回调。
        var interleavedQueue = new Queue<Action>();
        var interleavedRenders = new List<int>();
        var interleaved = new LatestStateDispatcher<int>(
            action => { interleavedQueue.Enqueue(action); return true; }, interleavedRenders.Add);
        interleaved.Publish(1);
        interleaved.SetActive(true, () => Task.Run(() => interleaved.Publish(2)).GetAwaiter().GetResult());
        Require(interleavedRenders.SequenceEqual([2]) && interleavedQueue.Count == 1,
            "同步恢复应显示并发发布的最新值，已排队回调仍拥有队列槽");
        Task.Run(() => interleaved.Publish(3)).GetAwaiter().GetResult();
        Require(interleavedQueue.Count == 1 && interleaved.PostedCount == 1,
            "同步恢复不能释放并发发布持有的队列槽，第二次发布必须合并");
        interleavedQueue.Dequeue()();
        Require(interleavedRenders.SequenceEqual([2, 3]) && interleaved.RenderedCount == 2,
            "唯一异步回调应继续显示合并后的最后状态");
        interleaved.Dispose();
    }

    // 复用外观检查的Application与窗口；只注入展示状态，不调用工具运行或真实输入。
    public static void CheckPages(MainWindow window, CraftPage craftPage, ClickerPage clickerPage,
        KeyLoopPage keyLoopPage, SettingsTool settings)
    {
        CheckSettingsScans(settings);
        var navigation = Find<ListBox>(window, "ToolList");
        var probes = new[]
        {
            new PageProbe("craft", craftPage, (text, value) => craftPage.PublishStatus(text),
                () => craftPage.StatusPostCount, () => craftPage.StatusRenderCount,
                () => Find<TextBlock>(craftPage, "CraftStatusText").Text),
            new PageProbe("clicker", clickerPage,
                (text, value) => clickerPage.PublishStatus(new ClickerStatus(false, value, text)),
                () => clickerPage.StatusPostCount, () => clickerPage.StatusRenderCount,
                () => Find<TextBlock>(clickerPage, "StatusText").Text),
            new PageProbe("keyloop", keyLoopPage,
                (text, value) => keyLoopPage.PublishStatus(new KeyLoopStatus(false, [value], text)),
                () => keyLoopPage.StatusPostCount, () => keyLoopPage.StatusRenderCount,
                () => Find<TextBlock>(keyLoopPage, "StatusText").Text),
        };
        foreach (var probe in probes)
        {
            Select(probe.Id);
            var beforePosts = probe.Posts();
            var beforeRenders = probe.Renders();
            PublishBurst(probe, "可见最后状态", 64);
            Require(probe.Posts() == beforePosts + 1 && probe.Renders() == beforeRenders,
                probe.Id + "后台突发状态必须只排队一次且不跨线程绘制");
            Pump();
            Require(probe.Renders() == beforeRenders + 1 && probe.Text() == Expected(probe, "可见最后状态"),
                probe.Id + "可见时必须绘制最新状态");

            Select("host");
            CheckHidden(probe, () => Select(probe.Id), "切页后已停止");

            window.WindowState = WindowState.Minimized;
            Pump();
            CheckHidden(probe, () => { window.WindowState = WindowState.Normal; Pump(); }, "最小化后已停止");

            window.Hide();
            Pump();
            CheckHidden(probe, () => { window.Show(); Pump(); }, "隐藏后已停止");
        }
        Select("craft");
        CheckClosedWindow();
        Console.WriteLine("INFO UI状态合并：三页切走/最小化/隐藏期间新增投递0、绘制0，重显恢复最新状态；首次列表扫描1/1。");

        void Select(string id)
        {
            navigation.SelectedItem = navigation.Items.Cast<ITool>().Single(tool => tool.Id == id);
            Pump();
        }
    }

    private static void CheckSettingsScans(SettingsTool tool)
    {
        var processScans = 0;
        var soundScans = 0;
        var draft = tool.CaptureDraft();
        var page = new SettingsPage(tool,
            () => { processScans++; return ["ui-scan-check.exe"]; },
            () => { soundScans++; return ["ui-scan-check.mp3"]; });
        Require(processScans == 1 && soundScans == 1, "设置页构造应各扫描一次");
        page.ActivatePage();
        Require(processScans == 1 && soundScans == 1, "首次激活不能重复构造时已完成的扫描");
        var process = Find<ComboBox>(page, "ProcessCombo");
        var sound = Find<ComboBox>(page, "SoundCombo");
        Require(string.IsNullOrEmpty(draft.TargetProcess) ? process.SelectedIndex == 0 :
                process.SelectedItem?.ToString() == draft.TargetProcess,
            "扫描结果没有当前进程时仍须保留当前选择");
        Require(sound.SelectedItem?.ToString() == draft.SelectedSound,
            "扫描结果没有当前音效时仍须保留当前选择");
        page.DeactivatePage();
        page.ActivatePage();
        Require(processScans == 2 && soundScans == 2, "再次进入设置页仍应按原语义重新扫描");

        ((Grid)process.Parent).Children.OfType<Button>().Single()
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(processScans == 3 && soundScans == 2, "手动刷新进程只扫描进程");
        ((Grid)sound.Parent).Children.OfType<Button>().Single(button => Equals(button.Content, "刷新"))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(processScans == 3 && soundScans == 3, "手动刷新声音只扫描声音");
        page.DeactivatePage();
    }

    private static void CheckHidden(PageProbe probe, Action reveal, string finalText)
    {
        var posts = probe.Posts();
        var renders = probe.Renders();
        var previousText = probe.Text();
        PublishBurst(probe, finalText, 64);
        Pump();
        Require(probe.Posts() == posts && probe.Renders() == renders && probe.Text() == previousText,
            probe.Id + "隐藏/卸载/最小化期间不应投递或修改展示");
        reveal();
        Require(probe.Renders() == renders + 1 && probe.Text() == Expected(probe, finalText),
            probe.Id + "重显应仅绘制一次最新停止状态");
    }

    private static string Expected(PageProbe probe, string text)
        => Find<ToggleButton>(probe.Page, "EnableToolSwitch").IsChecked == true ? text : "功能已停用";

    private static void PublishBurst(PageProbe probe, string finalText, int count)
        => Task.Run(() =>
        {
            for (var value = 0; value < count - 1; value++) probe.Publish("旧展示状态" + value, value);
            probe.Publish(finalText, count);
        }).GetAwaiter().GetResult();

    private static void CheckClosedWindow()
    {
        var child = new Border();
        var draws = new List<int>();
        var presenter = new LatestPageState<int>(child, draws.Add);
        var disposed = 0;
        presenter.Disposed += () => disposed++;
        var window = new Window { Content = child, Width = 160, Height = 80, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            Pump();
            presenter.Publish(1);
            window.Close();
            var posts = presenter.PostedCount;
            Task.Run(() => presenter.Publish(2)).GetAwaiter().GetResult();
            Pump();
            Require(disposed == 1 && presenter.PostedCount == posts && draws.Count == 0,
                "关闭必须解绑生命周期，且关闭前已排队状态和关闭后状态均不得绘制");
        }
        finally { window.Close(); presenter.Dispose(); }
    }

    private sealed record PageProbe(string Id, FrameworkElement Page, Action<string, int> Publish,
        Func<int> Posts, Func<int> Renders, Func<string> Text);

    private static T Find<T>(FrameworkElement root, string name) where T : class
        => root.FindName(name) as T ?? throw new InvalidOperationException("找不到控件: " + name);

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
