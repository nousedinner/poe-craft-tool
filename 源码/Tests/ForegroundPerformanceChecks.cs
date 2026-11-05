using System.Diagnostics;
using System.Text.Json;
using ShiKe.Services;

// 原生边界替身只模拟查询结果；所有用例均不注册热键、不发送输入、不操作用户进程。
internal static class ForegroundPerformanceChecks
{
    public static void CachedIdentityStillChecksEveryForegroundSnapshot()
    {
        var native = new FakeNative();
        using var detector = Detector(native);
        Require(detector.IsTargetForeground() && detector.IsTargetForeground(), "同一存活目标应可命中");
        Require(native.StableOpens == 1 && native.ImageReads == 1 && native.WindowReads == 4 && native.PidReads == 4,
            "缓存只能减少身份查询，不能省略本次与返回前的窗口/PID读取");
        native.Window = 2;
        Require(detector.IsTargetForeground() && native.StableOpens == 1,
            "同一存活进程的新窗口可以复用身份，不能把HWND当作进程身份");
        native.Processes[20] = new FakeProcess("other.exe");
        native.ProcessId = 20;
        Require(!detector.IsTargetForeground() && native.StableOpens == 2 && native.Disposals == 1,
            "相同窗口句柄转属不同PID时必须重新查身份并释放旧句柄");
    }

    public static void ReusedPidAndFailedWaitNeverReuseOldPermission()
    {
        foreach (var failedWait in new[] { false, true })
        {
            var native = new FakeNative();
            using var detector = Detector(native);
            Require(detector.IsTargetForeground(), "先建立目标身份");
            var original = native.Processes[10];
            if (failedWait) original.WaitFails = true;
            else original.Alive = false;
            native.Processes[10] = new FakeProcess("other.exe"); // 同PID甚至同HWND已被新身份复用。
            Require(!detector.IsTargetForeground() && native.Disposals == 1 && native.StableOpens == 1 && native.LimitedOpens == 0,
                "退出/等待失败本次必须拒绝并释放，不能降级查询后继续放行");
            Require(!detector.IsTargetForeground() && native.StableOpens == 2 && native.ImageReads == 2,
                "下一次检查必须查询复用PID的新身份");
        }

        var exited = new FakeNative();
        exited.Processes[10].Alive = false;
        using var cold = Detector(exited);
        Require(!cold.IsTargetForeground() && exited.Disposals == 1 && exited.LimitedOpens == 0,
            "首次打开时已经退出的进程也必须本次拒绝，不转入无身份保护的兜底");
    }

    public static void QueryAndFinalSnapshotRacesAreRejected()
    {
        var native = new FakeNative();
        using var detector = Detector(native);
        native.Processes[20] = new FakeProcess("other.exe");
        native.OnImageRead = () => { native.Window = 2; native.ProcessId = 20; };
        Require(!detector.IsTargetForeground() && native.Disposals == 1,
            "查询名称期间切换窗口/PID，不能返回旧身份或留下候选缓存");
        native.OnImageRead = null;
        native.Window = 1;
        native.ProcessId = 10;
        Require(detector.IsTargetForeground() && native.StableOpens == 2, "被拒候选必须在下次重新查询");
        var nextFinalPidRead = native.PidReads + 2;
        native.OnPidRead = () =>
        {
            if (native.PidReads == nextFinalPidRead) native.Processes[10].Alive = false;
        };
        Require(!detector.IsTargetForeground() && native.Disposals == 2,
            "最终窗口快照期间进程退出，最后一次句柄存活验证必须拒绝");
    }

    public static void PermissionFallbackNeverCreatesAnIdentityCache()
    {
        var native = new FakeNative { DenyStableOpen = true };
        using var detector = Detector(native);
        Require(detector.IsTargetForeground() && detector.IsTargetForeground(), "同步权限不足时保留旧查询权限路径");
        Require(native.StableOpens == 2 && native.LimitedOpens == 2 && native.ImageReads == 2 && native.Disposals == 2,
            "无同步权限的名称只能本次使用，每次重新打开且立即释放");
        native.DenyLimitedOpen = true;
        native.ManagedName = "target.exe";
        Require(detector.IsTargetForeground() && native.ManagedReads == 1, "保留托管名称兜底");
        native.ManagedName = null;
        Require(!detector.IsTargetForeground() && native.ManagedReads == 2,
            "两种查询失败不能复用上次托管成功结果");
    }

    public static void MissingWindowQueryFailureAndTargetChangesClearIdentity()
    {
        var native = new FakeNative();
        using var detector = Detector(native);
        Require(detector.IsTargetForeground(), "先建立缓存");
        native.Window = 0;
        Require(!detector.IsTargetForeground() && native.Disposals == 1, "无窗口必须拒绝并清缓存");
        native.Window = 1;
        Require(detector.IsTargetForeground(), "返回目标后重新查询");
        native.ProcessId = 0;
        Require(!detector.IsTargetForeground() && native.Disposals == 2, "PID查询失败不能借旧身份放行");
        native.ProcessId = 10;
        Require(detector.IsTargetForeground(), "建立新缓存供目标变更检查");
        detector.TargetProcess = "other.exe";
        Require(native.Disposals == 3 && !detector.IsTargetForeground(), "目标变更必须清缓存并立即改变比较对象");
        detector.TargetProcess = string.Empty;
        var reads = native.WindowReads;
        Require(!detector.IsTargetForeground() && native.WindowReads == reads, "空目标直接拒绝，不查询或缓存");
        detector.TargetProcess = "target.exe";
        native.Processes[10].QueryFails = true;
        Require(!detector.IsTargetForeground() && native.ManagedReads == 1,
            "新身份查询失败时不能使用以前相同PID的名称");
    }

    public static void ConcurrentDisposeCannotCloseAHandleDuringItsQuery()
    {
        var native = new FakeNative();
        var detector = Detector(native);
        using var queryEntered = new ManualResetEventSlim();
        using var releaseQuery = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        native.OnImageRead = () =>
        {
            queryEntered.Set();
            Require(releaseQuery.Wait(TimeSpan.FromSeconds(5)), "受控查询释放超时");
        };
        var check = Task.Run(detector.IsTargetForeground);
        Require(queryEntered.Wait(TimeSpan.FromSeconds(5)), "查询未进入受控边界");
        var dispose = Task.Run(() => { disposeStarted.Set(); detector.Dispose(); });
        try
        {
            Require(disposeStarted.Wait(TimeSpan.FromSeconds(5)), "释放请求未开始");
            Require(native.Disposals == 0, "并发Dispose不能提前关闭正在查询的句柄");
        }
        finally { releaseQuery.Set(); }
        Require(Task.WaitAll([check, dispose], TimeSpan.FromSeconds(5)), "并发查询/释放未结束");
        Require(check.Result && native.Disposals == 1 && !detector.IsTargetForeground(),
            "查询完成后释放一次，已释放实例必须永久拒绝检查");
        detector.Dispose();
        Require(native.Disposals == 1, "重复Dispose不能再次关闭句柄");
    }

    public static void ConcurrentTargetChangeInvalidatesTheCompletedIdentity()
    {
        var native = new FakeNative();
        using var detector = Detector(native);
        using var queryEntered = new ManualResetEventSlim();
        using var releaseQuery = new ManualResetEventSlim();
        using var setterStarted = new ManualResetEventSlim();
        native.OnImageRead = () =>
        {
            queryEntered.Set();
            Require(releaseQuery.Wait(TimeSpan.FromSeconds(5)), "目标变更检查的查询释放超时");
        };
        var check = Task.Run(detector.IsTargetForeground);
        Require(queryEntered.Wait(TimeSpan.FromSeconds(5)), "目标变更检查未进入查询");
        var change = Task.Run(() => { setterStarted.Set(); detector.TargetProcess = "other.exe"; });
        try { Require(setterStarted.Wait(TimeSpan.FromSeconds(5)), "目标设置请求未开始"); }
        finally { releaseQuery.Set(); }
        Require(Task.WaitAll([check, change], TimeSpan.FromSeconds(5)), "查询和目标变更未结束");
        Require(check.Result && native.Disposals == 1, "已开始的检查用原目标完成，目标设置随后清除该身份");
        native.OnImageRead = null;
        Require(!detector.IsTargetForeground() && native.StableOpens == 2,
            "目标设置返回后，后续检查必须重新查询且不得沿用旧允许结果");
    }

    // 只查询当前测试进程。前台来源固定为自身，因此只比较身份解析成本，不代表真实游戏或完整输入循环提速。
    public static void BenchmarkOwnProcessIdentityResolution()
    {
        var native = new OwnProcessNative();
        using var initial = native.OpenProcess((uint)Environment.ProcessId, verifyLifetime: false)
            ?? throw new InvalidOperationException("无法查询当前测试进程");
        var name = initial.ReadImageName() ?? throw new InvalidOperationException("无法读取当前测试进程名");
        using var detector = new ForegroundDetector(native) { TargetProcess = name };
        for (var i = 0; i < 100; i++) Require(detector.IsTargetForeground(), "自身身份预热失败");
        const int iterations = 10000;
        var results = new List<object>();
        for (var round = 0; round < 3; round++)
        {
            var uncached = Measure(() =>
            {
                using var handle = native.OpenProcess((uint)Environment.ProcessId, verifyLifetime: false);
                Require(string.Equals(handle?.ReadImageName(), name, StringComparison.OrdinalIgnoreCase), "自身未缓存查询失败");
            }, iterations);
            var cached = Measure(() => Require(detector.IsTargetForeground(), "自身缓存查询失败"), iterations);
            results.Add(new { round, uncached, cached });
        }
        Console.WriteLine("FOREGROUND_IDENTITY_BENCHMARK " + JsonSerializer.Serialize(new
        {
            iterations, results,
            scope = "current test process only; fixed foreground source; identity resolution, not game/input throughput",
        }));
    }

    private sealed record Measurement(double Milliseconds, long AllocatedBytes);

    private static Measurement Measure(Action action, int iterations)
    {
        var allocation = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++) action();
        return new(Stopwatch.GetElapsedTime(started).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocation);
    }

    private static ForegroundDetector Detector(FakeNative native) => new(native) { TargetProcess = "target.exe" };

    private sealed class FakeProcess(string name)
    {
        public string Name { get; } = name;
        public bool Alive { get; set; } = true;
        public bool WaitFails { get; set; }
        public bool QueryFails { get; set; }
    }

    private sealed class FakeNative : IForegroundNativeApi
    {
        public nint Window { get; set; } = 1;
        public uint ProcessId { get; set; } = 10;
        public Dictionary<uint, FakeProcess> Processes { get; } = new() { [10] = new("target.exe") };
        public bool DenyStableOpen { get; set; }
        public bool DenyLimitedOpen { get; set; }
        public string? ManagedName { get; set; }
        public int StableOpens { get; private set; }
        public int LimitedOpens { get; private set; }
        public int ImageReads { get; private set; }
        public int WindowReads { get; private set; }
        public int PidReads { get; private set; }
        public int ManagedReads { get; private set; }
        public int Disposals { get; private set; }
        public Action? OnImageRead { get; set; }
        public Action? OnPidRead { get; set; }
        public nint GetForegroundWindow() { WindowReads++; return Window; }
        public uint GetWindowProcessId(nint window) { PidReads++; OnPidRead?.Invoke(); return ProcessId; }
        public IForegroundProcessHandle? OpenProcess(uint processId, bool verifyLifetime)
        {
            if (verifyLifetime) StableOpens++; else LimitedOpens++;
            if (verifyLifetime ? DenyStableOpen : DenyLimitedOpen) return null;
            return Processes.TryGetValue(processId, out var process) ? new Handle(this, process) : null;
        }
        public string? GetManagedProcessName(uint processId) { ManagedReads++; return ManagedName; }

        private sealed class Handle(FakeNative owner, FakeProcess process) : IForegroundProcessHandle
        {
            private bool _disposed;
            public string? ReadImageName()
            {
                Require(!_disposed, "不能查询已释放句柄");
                owner.ImageReads++;
                owner.OnImageRead?.Invoke();
                Require(!_disposed, "查询进行中句柄被并发关闭");
                return process.QueryFails ? null : process.Name;
            }
            public bool IsAlive()
            {
                Require(!_disposed, "不能等待已释放句柄");
                return process.Alive && !process.WaitFails;
            }
            public void Dispose()
            {
                Require(!_disposed, "句柄重复释放");
                _disposed = true;
                owner.Disposals++;
            }
        }
    }

    private sealed class OwnProcessNative : IForegroundNativeApi
    {
        public nint GetForegroundWindow() => 1;
        public uint GetWindowProcessId(nint window) => (uint)Environment.ProcessId;
        public IForegroundProcessHandle? OpenProcess(uint processId, bool verifyLifetime)
        {
            Require(processId == Environment.ProcessId, "benchmark只能查询自身");
            return ForegroundNativeApi.Instance.OpenProcess(processId, verifyLifetime);
        }
        public string? GetManagedProcessName(uint processId)
        {
            Require(processId == Environment.ProcessId, "benchmark托管兜底只能查询自身");
            return ForegroundNativeApi.Instance.GetManagedProcessName(processId);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
