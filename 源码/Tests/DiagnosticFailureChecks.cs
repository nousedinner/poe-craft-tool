using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Text;
using ShiKe.Services;

internal static class DiagnosticFailureChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    public static void DefaultFailureReporterIsObservableInReleaseAndRecoveryRemainsIntact()
    {
        using var listener = new FailureListener();
        var files = new RecoverableFiles();
        var log = new BufferedDiagnosticLog("unused-test-path", new(8, 8, 0), files);
        try
        {
            log.WriteLine("first line cannot be saved");
            Require(!log.Flush(Limit), "文件失败必须仍被Flush报告");
            Require(SpinWait.SpinUntil(() => listener.Events.Any(item => item.Kind == (int)DiagnosticFailureKind.StorageWrite), Limit),
                "默认报告器必须在Release中向EventListener发出实际错误事件");
            Require(DiagnosticFailureReporter.Latest is { Kind: DiagnosticFailureKind.StorageWrite },
                "生产可读取的最后错误状态必须更新");
            Require(log.Snapshot().AcceptedLines == 1, "故障报告不能递归写回同一日志队列");

            files.Fail = false;
            log.WriteLine("recovered line");
            Require(log.Flush(Limit), "恢复后应继续正常追加");
            Require(files.Text.ToString().Contains("此前因磁盘写入失败有 1 条日志未保存", StringComparison.Ordinal) &&
                    files.Text.ToString().EndsWith("recovered line" + Environment.NewLine, StringComparison.Ordinal),
                "独立错误通道不能删除原有文件恢复摘要");
        }
        finally { log.Shutdown(Limit); }

        var notifications = 0;
        void Notify(DiagnosticFailure _) => Interlocked.Increment(ref notifications);
        DiagnosticFailureReporter.NotificationRequested += Notify;
        try
        {
            DiagnosticFailureReporter.Report(DiagnosticFailureKind.Shutdown, "test shutdown failure");
            DiagnosticFailureReporter.Report(DiagnosticFailureKind.LateMessage, "test late message");
            Require(notifications == 0 && listener.Events.Any(item => item.Kind == (int)DiagnosticFailureKind.Shutdown) &&
                    listener.Events.Any(item => item.Kind == (int)DiagnosticFailureKind.LateMessage),
                "退出和晚到消息仍应有独立诊断事件，但不能请求弹出提醒");
        }
        finally { DiagnosticFailureReporter.NotificationRequested -= Notify; }
    }

    public static void FailureNotificationsAreBoundedThrottledAndObserverSafe()
    {
        var clock = new ManualClock();
        var emitted = new List<DiagnosticFailure>();
        var observed = new List<DiagnosticFailure>();
        var hub = new DiagnosticFailureHub(clock, failure =>
        {
            emitted.Add(failure);
            throw new IOException("模拟独立通道监听器失败");
        });
        hub.NotificationRequested += _ => throw new InvalidOperationException("模拟提醒观察者失败");
        hub.NotificationRequested += observed.Add;

        hub.Report(DiagnosticFailureKind.StorageWrite, "first");
        hub.Report(DiagnosticFailureKind.Receive, "second");
        clock.Advance(TimeSpan.FromMilliseconds(29999));
        hub.Report(DiagnosticFailureKind.StorageWrite, "third");
        Require(observed.Count == 1 && emitted.Count == 3 && hub.Latest?.Sequence == 3,
            "30秒内只请求一次提醒，但每次都更新有界状态并发出诊断事件");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        hub.Report(DiagnosticFailureKind.StorageWrite, new string('x', 2000));
        Require(observed.Count == 2 && hub.Latest?.Message.Length <= DiagnosticFailureHub.MaximumMessageCharacters,
            "冷却后应再次允许提醒，最后错误文本必须有长度上限");
        hub.Report(DiagnosticFailureKind.Shutdown, "shutdown");
        hub.Report(DiagnosticFailureKind.LateMessage, "late");
        Require(observed.Count == 2 && emitted.Count == 6 && hub.Latest is { Sequence: 6, CanNotify: false },
            "终止类错误只能报告，不得触发UI通知");
    }

    public static void FailureCallbacksRunOutsideTheStateLock()
    {
        foreach (var blockEmission in new[] { true, false })
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            void Block(DiagnosticFailure _)
            {
                entered.Set();
                Require(release.Wait(Limit), "独立报告回调释放超时");
            }
            var hub = new DiagnosticFailureHub(new ManualClock(), blockEmission ? Block : _ => { });
            if (!blockEmission) hub.NotificationRequested += Block;
            var report = Task.Run(() => hub.Report(DiagnosticFailureKind.StorageWrite, "blocked observer"));
            Require(entered.Wait(Limit), "未进入受控回调");
            var read = Task.Run(() => hub.Latest);
            try
            {
                Require(read.Wait(TimeSpan.FromSeconds(2)) && read.Result?.Sequence == 1,
                    "独立通道和UI观察者运行期间不得持有错误状态锁");
            }
            finally { release.Set(); }
            Require(report.Wait(Limit), "报告回调未正常结束");
        }
    }

    private sealed class FailureListener : EventListener
    {
        internal ConcurrentQueue<(int Kind, string Message)> Events { get; } = new();
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "ShiKe-DiagnosticFailures") EnableEvents(eventSource, EventLevel.LogAlways);
        }
        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (data.EventSource.Name != "ShiKe-DiagnosticFailures" || data.EventId != 1 || data.Payload is null) return;
            Events.Enqueue((Convert.ToInt32(data.Payload[1]), data.Payload[2]?.ToString() ?? string.Empty));
        }
    }

    private sealed class RecoverableFiles : IDiagnosticLogFiles
    {
        internal volatile bool Fail = true;
        internal StringBuilder Text { get; } = new();
        public void Append(string path, string text)
        {
            if (Fail) throw new IOException("模拟磁盘拒绝写入");
            Text.Append(text);
        }
        public bool TrimIfOversized(string path, long maximumBytes, string replacement) => false;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(_timestamp);
        internal void Advance(TimeSpan amount) => _timestamp += amount.Ticks;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
