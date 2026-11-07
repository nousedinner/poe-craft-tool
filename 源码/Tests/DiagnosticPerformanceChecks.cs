using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ShiKe.Services;

internal static class DiagnosticPerformanceChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    public static void ConcurrentBatchesPreserveOrderUtf8AndReleaseFile()
        => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "debug.log");
            var log = new BufferedDiagnosticLog(path, new(64, 32, 2));
            var expected = new ConcurrentDictionary<long, string>();
            try
            {
                Parallel.For(0, 4, producer =>
                {
                    for (var index = 0; index < 250; index++)
                    {
                        var line = $"[12:34:56.789] 线程{producer:D2} 行{index:D4} 中文🙂";
                        var sequence = log.WriteLine(line);
                        Require(sequence != 0 && expected.TryAdd(sequence, line), "正常容量压力不能丢行");
                    }
                });
                Require(log.Flush(Limit), "并发日志必须排空");
                var actual = File.ReadAllLines(path, new UTF8Encoding(false, true));
                Require(actual.SequenceEqual(expected.OrderBy(pair => pair.Key).Select(pair => pair.Value)),
                    "磁盘行顺序必须等于接收顺序且 UTF-8 完整");
                var state = log.Snapshot();
                Require(state.AcceptedLines == 1000 && state.WrittenLines == 1000 && state.FailedLines == 0 &&
                        state.LateLines == 0 && state.PendingLines == 0 && state.WriteOperations < 1000,
                    "批量写入必须减少文件追加次数且不丢正常日志");
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                var moved = path + ".moved";
                File.Move(path, moved);
                File.Move(moved, path);
                log.WriteLine("文件移动后继续");
                Require(log.Flush(Limit) && File.ReadAllLines(path).Last() == "文件移动后继续",
                    "空闲时不持文件锁，后续批次必须重新打开实际路径");
            }
            finally { Require(log.Shutdown(Limit), "并发日志线程必须正常关闭"); }
        });

    public static void CapacityWaitsWithoutLossAndShutdownWakesProducer()
        => InDirectory(directory =>
        {
            var files = new BlockingFiles();
            var log = new BufferedDiagnosticLog(Path.Combine(directory, "debug.log"), new(1, 1, 0), files, _ => { });
            try
            {
                log.WriteLine("first");
                Require(files.Entered.Wait(Limit), "首批必须进入实际写入");
                log.WriteLine("queued");
                var blocked = Task.Run(() => log.WriteLine("backpressure"));
                Require(!blocked.Wait(50), "容量满时必须背压，不能静默丢行或无界增长");
                files.Release.Set();
                Require(blocked.Wait(Limit) && blocked.Result != 0 && log.Flush(Limit), "磁盘恢复后背压生产者必须继续");
                Require(files.Text.ToString() == "first" + Environment.NewLine + "queued" + Environment.NewLine +
                        "backpressure" + Environment.NewLine, "压力下所有行仍按原序保留");
            }
            finally { files.Release.Set(); log.Shutdown(Limit); }

            var blockedFiles = new BlockingFiles();
            var stopping = new BufferedDiagnosticLog(Path.Combine(directory, "blocked.log"), new(1, 1, 0), blockedFiles, _ => { });
            stopping.WriteLine("in-flight");
            Require(blockedFiles.Entered.Wait(Limit), "停机测试必须有正在执行的写入");
            stopping.WriteLine("queued");
            var waiting = Task.Run(() => stopping.WriteLine("late"));
            Require(!waiting.Wait(50), "生产者应在停机前受容量限制");
            var timer = Stopwatch.StartNew();
            Require(!stopping.Shutdown(TimeSpan.FromMilliseconds(50)), "阻塞系统写入时必须如实报告超时");
            Require(timer.Elapsed < TimeSpan.FromSeconds(1), "日志不能拖住进程退出");
            Require(waiting.Wait(Limit) && waiting.Result == 0, "停机必须唤醒受背压生产者并拒绝晚到消息");
            blockedFiles.Release.Set();
            stopping.Shutdown(Limit);
            var stopped = stopping.Snapshot();
            Require(stopped.Stopped && stopped.AbandonedLines == 1 && stopped.LateLines == 1,
                "超时遗留及晚到消息必须可观察，不能复活写入者");
        });

    public static void StalledWriterBoundsProducerWaitAndCountsDrops()
        => InDirectory(directory =>
        {
            var files = new BlockingFiles();
            var reports = new ConcurrentQueue<string>();
            var log = new BufferedDiagnosticLog(Path.Combine(directory, "debug.log"),
                new(1, 1, 0, 80), files, reports.Enqueue);
            try
            {
                log.WriteLine("in-flight");
                Require(files.Entered.Wait(Limit), "写入线程必须已阻塞在文件追加");
                log.WriteLine("queued");
                var timer = Stopwatch.StartNew();
                Require(log.WriteLine("dropped") == 0, "持续满队列应限时拒收诊断行");
                log.ClearOldLog();
                timer.Stop();
                var state = log.Snapshot();
                Require(timer.Elapsed < TimeSpan.FromSeconds(1) && state.DroppedLines == 1 &&
                        state.DroppedCommands == 1 && state.LateLines == 0 && state.FailedLines == 0 &&
                        state.AcceptedLines == 2 && state.PendingLines == 2 && reports.Count == 1,
                    "慢盘不应无限卡住调用方；超时丢弃与写盘失败、停机晚到须分开计数并限频上报");
                files.Release.Set();
                Require(log.Flush(Limit), "写盘恢复后已接收队列应完成");
                log.WriteLine("after-recovery");
                Require(log.Flush(Limit) && files.Text.ToString().Contains("1 条消息、1 次清理请求未接收", StringComparison.Ordinal) &&
                        !files.Text.ToString().Contains("dropped" + Environment.NewLine, StringComparison.Ordinal) &&
                        log.Snapshot().WrittenLines == 3,
                    "恢复后应记录丢弃摘要，不能把已拒收消息伪记为成功写入");
            }
            finally { files.Release.Set(); log.Shutdown(Limit); }
        });

    public static void ClearCommandUsesOriginalThresholdAndOrderedBarrier()
        => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "debug.log");
            File.WriteAllText(path, new string('x', 512 * 1024), new UTF8Encoding(false));
            var log = new BufferedDiagnosticLog(path, new(16, 8, 0));
            try
            {
                log.ClearOldLog();
                Require(log.Flush(Limit) && new FileInfo(path).Length == 512 * 1024,
                    "恰好 512KiB 不能提前截断");
                log.WriteLine("先写入，随后应被截断");
                log.ClearOldLog();
                log.WriteLine("截断后的中文🙂");
                Require(log.Flush(Limit), "清空屏障及后续日志必须完成");
                var lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
                Require(lines.Length == 2 && lines[0].Contains("=== 日志已截断 ===", StringComparison.Ordinal) &&
                        lines[1] == "截断后的中文🙂" && log.Snapshot().Truncations == 1,
                    "截断必须位于正确的日志顺序位置");
            }
            finally { log.Shutdown(Limit); }
        });

    public static void FileFailureIsCountedAndLaterWritesRecover()
        => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "debug.log");
            File.WriteAllText(path, "existing" + Environment.NewLine);
            var reports = new ConcurrentQueue<string>();
            var log = new BufferedDiagnosticLog(path, new(8, 8, 0), reportFailure: reports.Enqueue);
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    log.WriteLine("locked failure");
                    Require(!log.Flush(Limit), "实际文件占用失败不能被报告为成功");
                    Require(log.Snapshot().FailedLines == 1, "写入失败必须计数");
                }
                log.WriteLine("recovered 中文");
                Require(log.Flush(Limit), "文件解锁后后续批次必须可恢复");
                var text = File.ReadAllText(path);
                Require(text.StartsWith("existing", StringComparison.Ordinal) &&
                        text.Contains("此前因磁盘写入失败有 1 条日志未保存", StringComparison.Ordinal) &&
                        text.EndsWith("recovered 中文" + Environment.NewLine, StringComparison.Ordinal),
                    "恢复必须保留已有日志并明确说明失败条数");
                Require(log.Snapshot().WrittenLines == 1 && log.Snapshot().FailedLines == 1,
                    "成功计数不能掩盖已失败日志");
            }
            finally { log.Shutdown(Limit); }
        });

    public static void ClearFailureFromLockOrReadOnlyCanRecover()
        => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "debug.log");
            File.WriteAllText(path, new string('x', 512 * 1024 + 1));
            var log = new BufferedDiagnosticLog(path, new(8, 8, 0), reportFailure: _ => { });
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    log.ClearOldLog();
                    Require(!log.Flush(Limit), "占用中的日志无法截断时必须报告失败");
                }
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                log.ClearOldLog();
                Require(!log.Flush(Limit) && log.Snapshot().FailedOperations == 2,
                    "只读/占用截断失败均须计数且不终止写入线程");
                File.SetAttributes(path, FileAttributes.Normal);
                log.ClearOldLog();
                log.WriteLine("正常恢复");
                Require(log.Flush(Limit) && log.Snapshot().Truncations == 1 &&
                        File.ReadAllText(path).Contains("正常恢复", StringComparison.Ordinal),
                    "解除只读后清空及后续日志必须恢复");
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
                log.Shutdown(Limit);
            }
        });

    public static void ShutdownDrainsAndNeverRestarts()
        => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "debug.log");
            var log = new BufferedDiagnosticLog(path, new(64, 32, 25));
            for (var index = 0; index < 50; index++) log.WriteLine($"line-{index}");
            Require(log.Shutdown(Limit), "正常退出应排空所有日志");
            Require(File.ReadAllLines(path).Length == 50, "退出不能丢最后一个批次");
            Require(log.WriteLine("late") == 0 && log.Snapshot().LateLines == 1 && log.Snapshot().Stopped,
                "关闭后的日志应拒收且不重新启动线程");
            Require(log.Shutdown(Limit), "重复关闭应立即完成");
        });

    public static void FlushTimeoutRetainsNormalQueuedMessages()
        => InDirectory(directory =>
        {
            var files = new BlockingFiles();
            var log = new BufferedDiagnosticLog(Path.Combine(directory, "debug.log"), new(8, 8, 0), files);
            try
            {
                log.WriteLine("first");
                Require(files.Entered.Wait(Limit), "必须在写入过程中测试 Flush 超时");
                log.WriteLine("second");
                Require(!log.Flush(TimeSpan.FromMilliseconds(30)), "慢存储应使短预算 Flush 返回 false");
                log.WriteLine("third");
                files.Release.Set();
                Require(log.Flush(Limit), "Flush 超时后日志系统仍应正常完成后续工作");
                Require(files.Text.ToString() == string.Join(Environment.NewLine, "first", "second", "third", "") &&
                        log.Snapshot().WrittenLines == 3 && log.Snapshot().AbandonedLines == 0,
                    "普通 Flush 超时不能像最终关闭那样放弃消息");
            }
            finally { files.Release.Set(); log.Shutdown(Limit); }
        });

    public static void UnusedOwnerDoesNotCreateWorkerOrFiles()
        => InDirectory(directory =>
        {
            var creations = 0;
            var data = Path.Combine(directory, "data");
            var owner = new DiagnosticLogOwner(() =>
            {
                creations++;
                return new BufferedDiagnosticLog(Path.Combine(data, "debug.log"));
            });
            Require(owner.Flush(Limit) && owner.Shutdown(Limit) && owner.Shutdown(Limit),
                "未使用日志时刷新/关闭应立即完成");
            Require(owner.WriteLine("late") == 0, "最终关闭后不能再创建写入器");
            owner.ClearOldLog();
            Require(creations == 0 && owner.Snapshot() is null && !Directory.Exists(data),
                "早退/助手的最终关闭不得创建日志线程、目录或文件");
        });

    public static void SingleProducerBurstPreservesAllMessages()
        => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "debug.log");
            var log = new BufferedDiagnosticLog(path, new(8, 4, 0));
            for (var index = 0; index < 1000; index++)
                Require(log.WriteLine($"burst-{index}") > 0, "单线程突发不得因队列满拒收");
            Require(log.Shutdown(Limit), "突发日志应全部完成关闭");
            Require(File.ReadAllLines(path).SequenceEqual(Enumerable.Range(0, 1000).Select(index => $"burst-{index}")),
                "单线程背压后不得缺行或乱序");
        });

    // 单独的本地基线对比；只度量日志机制，不外推成游戏循环提速。
    public static string CompareBaseline()
    {
        string? result = null;
        InDirectory(directory =>
        {
            const int workers = 4, perWorker = 1000;
            var oldPath = Path.Combine(directory, "baseline.log");
            var newPath = Path.Combine(directory, "buffered.log");
            var gate = new object();
            var baseline = Measure((worker, index) =>
            {
                lock (gate) File.AppendAllText(oldPath, $"[12:34:56.789] 线程{worker} 日志{index} 中文" + Environment.NewLine);
            }, workers, perWorker, () => { });
            var buffered = new BufferedDiagnosticLog(newPath);
            var changed = Measure((worker, index) => buffered.WriteLine($"[12:34:56.789] 线程{worker} 日志{index} 中文"),
                workers, perWorker, () => Require(buffered.Shutdown(Limit), "性能样本必须完整落盘"));
            Require(File.ReadAllLines(oldPath).Length == workers * perWorker &&
                    File.ReadAllLines(newPath).Length == workers * perWorker && buffered.Snapshot().FailedLines == 0,
                "性能结果必须以完整无丢失写入为前提");
            result = JsonSerializer.Serialize(new { lines = workers * perWorker, baseline, buffered = changed,
                actualBatchWrites = buffered.Snapshot().WriteOperations,
                scope = "isolated_local_logging_only_not_gameplay_speed" });
        });
        return result!;
    }

    private static object Measure(Action<int, int> write, int workers, int perWorker, Action finish)
    {
        var calls = new ConcurrentBag<double>();
        var total = Stopwatch.StartNew();
        Parallel.For(0, workers, worker =>
        {
            for (var index = 0; index < perWorker; index++)
            {
                var start = Stopwatch.GetTimestamp();
                write(worker, index);
                calls.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        });
        finish();
        total.Stop();
        var ordered = calls.Order().ToArray();
        return new { elapsedMs = total.Elapsed.TotalMilliseconds, linesPerSecond = calls.Count / total.Elapsed.TotalSeconds,
            callMedianMs = ordered[ordered.Length / 2], callP95Ms = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1] };
    }

    private sealed class BlockingFiles : IDiagnosticLogFiles
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        internal readonly StringBuilder Text = new();
        public void Append(string path, string text)
        {
            Entered.Set();
            if (!Release.Wait(Limit)) throw new IOException("fixture write timeout");
            Text.Append(text);
        }
        public bool TrimIfOversized(string path, long maximumBytes, string replacement) => false;
    }

    private static void InDirectory(Action<string> check)
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = Path.Combine(root, "diagnostic-performance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { check(directory); }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("日志验证目录越界");
            Directory.Delete(full, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
