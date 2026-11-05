using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Tools.Clicker;
using ShiKe.Tools.KeyLoop;

// 不注册热键或发送输入；只调用拒绝回调并检查独立通知窗口。
internal static class MissingTargetReminderChecks
{
    public static void ReminderOnlyShowsForMissingTargetAndRespectsCooldown()
    {
        var clock = new ManualClock();
        var reminder = new MissingTargetReminder(clock);
        var shown = 0;
        void Show() => shown++;

        reminder.ShowIfNeeded(false, null, Show);
        reminder.ShowIfNeeded(true, "PathOfExile_x64.exe", Show);
        Require(shown == 0, "停用工具或已配置目标时应保持静默");
        reminder.ShowIfNeeded(true, null, Show);
        Require(shown == 1, "静默拒绝不能消耗未锁定目标的首次提醒机会");

        for (var repeat = 0; repeat < 50; repeat++)
            reminder.ShowIfNeeded(true, string.Empty, Show);
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        reminder.ShowIfNeeded(true, " ", Show);
        Require(shown == 1, "连续触发和五秒以内的重试不能刷屏");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        reminder.ShowIfNeeded(false, null, Show);
        reminder.ShowIfNeeded(true, "configured-target.exe", Show);
        reminder.ShowIfNeeded(true, "\t", Show);
        Require(shown == 2, "五秒后再次未锁定应提醒，其他拒绝不能延长冷却");
        reminder.ShowIfNeeded(true, null, Show);
        Require(shown == 2, "再次显示后应重新开始冷却");
    }

    public static void ClickerHotkeysShareLightReminderWithoutStarting()
        => WithHost(host =>
        {
            var tool = new ClickerTool();
            tool.Initialize(host);
            try
            {
                var requests = tool.GetHotkeyRequests();
                Require(requests.Count == 2 && requests[0].Mode == HotkeyMode.Toggle &&
                    requests[1].Mode == HotkeyMode.Hold, "连点应保留切换及按住两个入口");
                CheckToolReminder(host, tool, requests, () => tool.IsRunning);
                Require(tool.ClickCount == 0, "拒绝提醒不能产生点击次数");
            }
            finally
            {
                tool.OnShutdown();
                Require(tool.ShutdownCompleted, "连点提醒测试必须结束空闲 worker");
            }
        });

    public static void KeyLoopHotkeyUsesLightReminderWithoutStarting()
        => WithHost(host =>
        {
            var tool = new KeyLoopTool();
            tool.Initialize(host);
            try
            {
                var requests = tool.GetHotkeyRequests();
                Require(requests.Count == 1 && requests[0].Mode == HotkeyMode.Toggle,
                    "按键循环应保留切换入口");
                CheckToolReminder(host, tool, requests, () => tool.IsRunning);
            }
            finally
            {
                tool.OnShutdown();
                Require(tool.ShutdownCompleted, "循环提醒测试必须结束空闲 workers");
            }
        });

    private static void CheckToolReminder(ToolHost host, ITool tool,
        IReadOnlyList<HotkeyRequest> requests, Func<bool> isRunning)
    {
        var before = CurrentWindows().ToHashSet();
        foreach (var request in requests)
            Require(request.CheckForeground && request.ForegroundRejectedHandler is not null,
                "提醒必须通过前台拒绝回调接入，不能移除输入门禁");

        tool.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = false }));
        foreach (var request in requests) request.ForegroundRejectedHandler!();
        Require(!CurrentWindows().Any(window => !before.Contains(window)),
            "停用工具不应创建提醒窗口");

        tool.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = true, notifications_enabled = false }));
        host.Foreground.TargetProcess = "configured-unfocused-target.exe";
        foreach (var request in requests) request.ForegroundRejectedHandler!();
        Require(!CurrentWindows().Any(window => !before.Contains(window)),
            "已有目标时前台拒绝必须继续静默");

        host.Foreground.TargetProcess = null;
        requests[0].ForegroundRejectedHandler!();
        var overlay = CurrentWindows().Where(window => !before.Contains(window)).Single();
        Require(overlay.IsVisible && !overlay.ShowActivated && !overlay.ShowInTaskbar && overlay.Topmost,
            "未锁定提醒应使用可见但不激活、不进任务栏的浮层");
        const int noActivate = 0x08000000;
        const int transparent = 0x00000020;
        var style = GetWindowLong(new WindowInteropHelper(overlay).Handle, -20);
        Require((style & (noActivate | transparent)) == (noActivate | transparent),
            "提醒窗口必须实际保留不激活与鼠标穿透样式");
        Require(!isRunning(), "拒绝回调只能提醒，不能启动工具");

        host.Notification.Hide();
        // 先尝试另一个入口，确认 Toggle/Hold 没有各自绕过冷却。
        foreach (var request in requests.Reverse()) request.ForegroundRejectedHandler!();
        Require(!overlay.IsVisible, "同一工具各热键应共享冷却，不能再次弹出提醒");
        Require(CurrentWindows().Count(window => !before.Contains(window)) == 1 && !isRunning(),
            "重复拒绝不能增加窗口或启动工具");
    }

    private static IEnumerable<Window> CurrentWindows()
        => PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Select(source => source.RootVisual).OfType<Window>();

    private static void WithHost(Action<ToolHost> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ToolHost? host = null;
            var directory = Path.Combine(AppContext.BaseDirectory, "missing-target-" + Guid.NewGuid().ToString("N"));
            try
            {
                host = new ToolHost(new ToolRegistry(), new StorageService(directory));
                check(host);
                Require(!Directory.EnumerateFileSystemEntries(directory).Any(),
                    "热键拒绝提醒不应读写或生成用户配置");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (host is not null)
                {
                    host.Sound.Dispose();
                    host.Notification.Dispose();
                    host.Network.Dispose();
                }
                // 仅删除本次唯一命名的空目录，不递归清理测试或用户文件。
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(15)), "未锁定提醒检查超时");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
