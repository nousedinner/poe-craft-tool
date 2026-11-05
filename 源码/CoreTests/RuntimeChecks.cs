using System.Collections.Concurrent;
using ShiKe.Runtime;
using ShiKe.Tools.Clicker;
using ShiKe.Tools.Craft;
using ShiKe.Tools.KeyLoop;

internal static class RuntimeChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static void CoreHasNoDesktopAssemblyDependency()
    {
        var forbidden = new[] { "拾刻", "WindowsBase", "PresentationCore", "PresentationFramework", "System.Windows.Forms" };
        Require(!typeof(CraftRules).Assembly.GetReferencedAssemblies().Any(reference => forbidden.Contains(reference.Name)),
            "Core 不得引用桌面宿主或 UI 程序集");
    }

    public static void ClickerWaitsForForegroundAndReleasesOnShutdown()
    {
        using var environment = new FakeEnvironment();
        var foregroundObserved = Signal();
        var clickEntered = Signal();
        var allowForeground = false;
        environment.Foreground = () =>
        {
            foregroundObserved.TrySetResult();
            return Volatile.Read(ref allowForeground);
        };
        environment.Click = async (_, token) =>
        {
            clickEntered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
        };
        var engine = new ClickerEngine(environment);
        try
        {
            Require(engine.Start(ClickerMouseButton.Left, 33), "应能启动");
            Wait(foregroundObserved.Task);
            Require(!clickEntered.Task.IsCompleted, "非前台不能执行输入");
            Volatile.Write(ref allowForeground, true);
            Wait(clickEntered.Task);
            Require(engine.Shutdown(Timeout), "关闭必须等待被取消的实际输入返回");
            Require(engine.ShutdownCompleted && !engine.IsRunning, "关闭后工作任务应实际结束");
            Require(environment.MouseReleases == 1, "当前运行应释放一次鼠标");
        }
        finally { engine.Shutdown(Timeout); }
    }

    public static void ClickerOldFailureCannotStopRestartedRun()
    {
        using var environment = new FakeEnvironment();
        var firstEntered = Signal();
        var releaseFirst = Signal();
        var secondEntered = Signal();
        var calls = 0;
        environment.Click = async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
                throw new IOException("旧运行故障");
            }
            secondEntered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
        };
        var errors = new ConcurrentQueue<string>();
        var engine = new ClickerEngine(environment);
        engine.ErrorOccurred += errors.Enqueue;
        try
        {
            Require(engine.Start(ClickerMouseButton.Left, 33), "第一轮应启动");
            Wait(firstEntered.Task);
            engine.Stop();
            Require(engine.Start(ClickerMouseButton.Right, 33), "旧动作收尾期间允许排队新运行");
            releaseFirst.TrySetResult();
            Wait(secondEntered.Task);
            Require(engine.IsRunning && errors.IsEmpty, "旧运行故障不能停止或报错到新运行");
        }
        finally
        {
            releaseFirst.TrySetResult();
            Require(engine.Shutdown(Timeout), "重启后的工作任务必须退出");
        }
    }

    public static void KeyLoopSnapshotsBothSlotsAndCancelsActualWork()
    {
        using var environment = new FakeEnvironment();
        using var bothEntered = new CountdownEvent(2);
        var calls = new ConcurrentQueue<string>();
        var released = 0;
        environment.Press = async (key, token) =>
        {
            calls.Enqueue(key);
            bothEntered.Signal();
            try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
            finally { Interlocked.Increment(ref released); }
        };
        var configurations = new[]
        {
            new KeyLoopSlot { Enabled = true, Key = "A", DelaySeconds = 0.1 },
            new KeyLoopSlot { Enabled = true, Key = "B", DelaySeconds = 0.1 },
        };
        var engine = new KeyLoopEngine(environment);
        try
        {
            Require(engine.Start(configurations), "两个槽位应启动");
            configurations[0].Key = "changed";
            configurations[1].Enabled = false;
            Require(bothEntered.Wait(Timeout), "两个槽位都应实际进入动作");
            Require(calls.Order(StringComparer.Ordinal).SequenceEqual(new[] { "A", "B" }), "运行必须使用启动快照");
            Require(engine.Shutdown(Timeout), "关闭必须等待全部槽位清理");
            Require(engine.ShutdownCompleted && released == 2, "所有实际输入动作必须完成取消收尾");
        }
        finally { engine.Shutdown(Timeout); }
    }

    public static void KeyLoopKeepsEmergencyReasonAfterEnvironmentReset()
    {
        using var environment = new FakeEnvironment();
        var entered = Signal();
        var complete = Signal();
        var stopped = new TaskCompletionSource<KeyLoopStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Press = async (_, token) =>
        {
            entered.TrySetResult();
            await complete.Task;
            token.ThrowIfCancellationRequested();
        };
        var engine = new KeyLoopEngine(environment);
        engine.StatusUpdated += status => { if (!status.Running) stopped.TrySetResult(status); };
        try
        {
            engine.Start([new() { Enabled = true, Key = "A", DelaySeconds = 0.1 }]);
            Wait(entered.Task);
            environment.CancelEmergency();
            environment.ResetEmergencyStop();
            complete.TrySetResult();
            var status = stopped.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
            Require(status.Text == "按键循环已紧急停止" && !engine.IsRunning,
                "宿主换新令牌后，旧运行仍应根据自己捕获的令牌结束");
        }
        finally
        {
            complete.TrySetResult();
            Require(engine.Shutdown(Timeout), "紧急停止后的工作任务必须退出");
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Wait(Task task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeEnvironment : IClickerEnvironment, IKeyLoopEnvironment, IDisposable
    {
        private CancellationTokenSource _emergency = new();
        private readonly List<CancellationTokenSource> _retired = [];
        private int _mouseReleases;
        public int MouseReleases => Volatile.Read(ref _mouseReleases);
        public Func<bool> Foreground { get; set; } = () => true;
        public Func<ClickerMouseButton, CancellationToken, Task> Click { get; set; } = (_, _) => Task.CompletedTask;
        public Func<string, CancellationToken, Task> Press { get; set; } = (_, _) => Task.CompletedTask;
        public CancellationToken EmergencyToken => _emergency.Token;
        public void CancelEmergency() => _emergency.Cancel();
        public void ResetEmergencyStop()
        {
            _retired.Add(_emergency);
            _emergency = new();
        }
        public bool IsTargetForeground() => Foreground();
        public bool IsSupportedKey(string key) => key is "A" or "B";
        public Task ClickAsync(ClickerMouseButton button, CancellationToken token) => Click(button, token);
        public Task PressAndReleaseAsync(string key, CancellationToken token) => Press(key, token);
        public void ReleaseMouseButtons() => Interlocked.Increment(ref _mouseReleases);
        public void Log(string message) { }
        public void Dispose()
        {
            _emergency.Dispose();
            foreach (var source in _retired) source.Dispose();
        }
    }
}
