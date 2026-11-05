using System.Reflection;
using ShiKe.Host;
using ShiKe.Tools.KeyLoop;

// 只注入受控异步动作；不注册热键、不发送按键、不访问网络。
internal static class KeyLoopAuditFixChecks
{
    public static void SameRunLateProgressDoesNotReplaceStop() => WithHost(host =>
    {
        var statuses = new List<KeyLoopStatus>();
        var calls = 0;
        var engine = new KeyLoopEngine(host, () => true, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        engine.StatusUpdated += statuses.Add;
        try
        {
            lock (ReadField(engine, "_gate"))
            {
                Require(engine.Start(Slots("q")), "待运行按键循环启动失败");
                var runId = (int)ReadField(engine, "_runId");
                Require(engine.Stop(), "待运行按键循环停止失败");
                // 让节流允许本次报告，确定性模拟停止消息发布已超过 250ms。
                typeof(KeyLoopEngine).GetField("_lastStatusTimestamp", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(engine, 0L);
                ReportLateRunningStatus(engine, runId, force: false);
                Require(statuses.Count == 2 && !statuses[^1].Running && statuses[^1].Text == "按键循环已停止",
                    "同轮迟到进度不能覆盖停止文案，即使节流已允许发布");
            }
        }
        finally { Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "迟到进度检查未完整关闭 worker"); }
        Require(calls == 0, "受控待运行检查不能执行按键动作");
    });

    public static void ForcedLateStartDoesNotReplaceWorkerFailure() => WithHost(host =>
    {
        using var entered = new ManualResetEventSlim();
        using var failed = new ManualResetEventSlim();
        var failAction = NewSignal();
        var statuses = new List<KeyLoopStatus>();
        var failureMessage = string.Empty;
        var engine = new KeyLoopEngine(host, () => true, async (_, _) =>
        {
            entered.Set();
            await failAction.Task;
            throw new InvalidOperationException("受控的启动后按键失败");
        });
        engine.StatusUpdated += statuses.Add;
        engine.ErrorOccurred += error =>
        {
            failureMessage = error;
            failed.Set();
        };
        try
        {
            Require(engine.Start(Slots("q")), "受控失败轮次启动失败");
            Require(entered.Wait(TimeSpan.FromSeconds(2)), "受控动作未开始");
            var runId = (int)ReadField(engine, "_runId");
            failAction.TrySetResult();
            Require(failed.Wait(TimeSpan.FromSeconds(2)), "worker 未完成真实异常收尾并发布错误");
            lock (ReadField(engine, "_gate"))
            {
                Require(!engine.IsRunning && statuses.Count == 2 && statuses[^1].Text == failureMessage,
                    "worker 异常必须先发布停止状态与具体原因");
                // 模拟 Start 唤醒 worker 后，强制运行报告晚于失败收尾才抵达。
                ReportLateRunningStatus(engine, runId, force: true);
                Require(statuses.Count == 2 && !statuses[^1].Running && statuses[^1].Text == failureMessage,
                    "同轮迟到的强制启动报告不能覆盖 worker 失败文案");
            }
        }
        finally
        {
            failAction.TrySetResult();
            Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "迟到启动报告检查未完整关闭 worker");
        }
    });

    public static void EmergencyStopSurvivesHostTokenReset() => WithHost(host =>
    {
        using var entered = new ManualResetEventSlim();
        using var stopped = new ManualResetEventSlim();
        var release = NewSignal();
        var reason = string.Empty;
        var engine = new KeyLoopEngine(host, () => true, async (_, token) =>
        {
            entered.Set();
            await release.Task;
            token.ThrowIfCancellationRequested();
        });
        engine.StatusUpdated += status =>
        {
            if (status.Running) return;
            reason = status.Text;
            stopped.Set();
        };
        try
        {
            Require(engine.Start(Slots("q")), "首轮启动失败");
            Require(entered.Wait(TimeSpan.FromSeconds(2)), "受控动作未开始");
            host.TriggerEmergencyStop();
            host.ResetEmergencyStop(); // 模拟另一个工具在旧 worker 收尾前启动。
            Require(!host.EmergencyCts.IsCancellationRequested, "宿主令牌应已换成新一轮");
            release.TrySetResult();
            Require(stopped.Wait(TimeSpan.FromSeconds(2)), "重置宿主令牌后旧运行仍必须发布停止状态");
            Require(!engine.IsRunning && reason == "按键循环已紧急停止", "应按本轮取消原因收尾，不能留下空运行");
        }
        finally
        {
            release.TrySetResult();
            Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "紧急停止检查未完整关闭 worker");
        }
    });

    public static void OldWorkerCannotFailRestartedRun() => WithHost(host =>
    {
        using var firstEntered = new ManualResetEventSlim();
        using var nextEntered = new ManualResetEventSlim();
        var releaseOld = NewSignal();
        var errors = 0;
        var engine = new KeyLoopEngine(host, () => true, async (key, token) =>
        {
            if (key == "q")
            {
                firstEntered.Set();
                await releaseOld.Task;
                throw new InvalidOperationException("受控的旧轮失败");
            }
            nextEntered.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        engine.ErrorOccurred += _ => Interlocked.Increment(ref errors);
        try
        {
            Require(engine.Start(Slots("q")), "旧轮启动失败");
            Require(firstEntered.Wait(TimeSpan.FromSeconds(2)), "旧轮动作未开始");
            Require(engine.Stop(), "旧轮停止失败");
            Require(engine.Start(Slots("w")), "立即重启失败");
            releaseOld.TrySetResult();
            Require(nextEntered.Wait(TimeSpan.FromSeconds(2)), "旧 worker 结束后未接手新轮");
            Require(engine.IsRunning && errors == 0, "旧轮异常不得停止新轮或上报成新轮错误");
            Require(engine.PressCounts.All(count => count == 0), "旧轮完成不得污染新轮计数");
        }
        finally
        {
            releaseOld.TrySetResult();
            Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "重启检查未完整关闭 worker");
        }
    });

    public static void StopToleratesWorkerDisposingCollectedSource() => WithHost(host =>
    {
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var cancelling = new ManualResetEventSlim();
        using var releaseCancellation = new ManualResetEventSlim();
        var releaseSecond = NewSignal();
        var firstToken = CancellationToken.None;
        Task<bool>? stopping = null;
        var engine = new KeyLoopEngine(host, () => true, async (key, token) =>
        {
            if (key == "q")
            {
                firstToken = token;
                firstEntered.Set();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            else
            {
                secondEntered.Set();
                await releaseSecond.Task;
                throw new InvalidOperationException("让第二槽位在停止快照之后结束");
            }
        });
        try
        {
            Require(engine.Start(Slots("q", "w")), "双槽启动失败");
            Require(firstEntered.Wait(TimeSpan.FromSeconds(2)) && secondEntered.Wait(TimeSpan.FromSeconds(2)),
                "两个受控动作未开始");
            var secondSource = ReadRunSource(engine, 1);
            // Stop 已收集两槽位后，阻塞第一项 Cancel；允许第二 worker 先释放其源。
            using var registration = firstToken.Register(() =>
            {
                cancelling.Set();
                releaseCancellation.Wait(TimeSpan.FromSeconds(4));
            });
            stopping = Task.Run(() => engine.Stop());
            Require(cancelling.Wait(TimeSpan.FromSeconds(2)), "停止未进入受控取消回调");
            releaseSecond.TrySetResult();
            Require(SpinWait.SpinUntil(() => IsDisposed(secondSource), TimeSpan.FromSeconds(2)),
                "第二 worker 未在取消快照之后释放源");
            releaseCancellation.Set();
            Require(stopping.Wait(TimeSpan.FromSeconds(2)) && stopping.Result, "并发释放取消源不应令 Stop 抛异常");
            Require(!engine.IsRunning, "停止后不能保留运行标记");
        }
        finally
        {
            releaseSecond.TrySetResult();
            releaseCancellation.Set();
            try { if (stopping is not null) stopping.GetAwaiter().GetResult(); }
            finally { Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "并发取消检查未完整关闭 worker"); }
        }
    });

    public static void StopBeforeWorkerStartsReleasesSources() => WithHost(host =>
    {
        var calls = 0;
        var engine = new KeyLoopEngine(host, () => true, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        try
        {
            var gate = typeof(KeyLoopEngine).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            lock (gate)
            {
                // 在 worker 取得运行快照前完成启停，确定性覆盖尚无人负责释放的源。
                for (var run = 0; run < 2; run++)
                {
                    Require(engine.Start(Slots("q", "w")), "待接手轮次启动失败");
                    var sources = new[] { ReadRunSource(engine, 0), ReadRunSource(engine, 1) };
                    Require(engine.Stop(), "待接手轮次停止失败");
                    Require(sources.All(IsDisposed), "worker 尚未接手时应由停止方释放取消源");
                }
            }
            Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "待接手检查未完整关闭 worker");
            Require(calls == 0, "已停止的待接手运行不能执行按键动作");
        }
        finally { engine.Shutdown(TimeSpan.FromSeconds(2)); }
    });

    public static void RestartDuringOtherSlotCancellationSkipsOldError() => WithHost(host =>
    {
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var cancelling = new ManualResetEventSlim();
        using var releaseCancellation = new ManualResetEventSlim();
        using var restarted = new ManualResetEventSlim();
        using var oldWorkerResumed = new ManualResetEventSlim();
        var failOld = NewSignal();
        var secondToken = CancellationToken.None;
        var errors = 0;
        var staleStatuses = 0;
        var newRunStarted = 0;
        var engine = new KeyLoopEngine(host, () => true, async (key, token) =>
        {
            if (key == "q")
            {
                firstEntered.Set();
                await failOld.Task;
                throw new InvalidOperationException("受控的旧轮故障");
            }
            if (key == "w")
            {
                secondToken = token;
                secondEntered.Set();
            }
            else if (key == "e") oldWorkerResumed.Set();
            else restarted.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        engine.ErrorOccurred += _ => Interlocked.Increment(ref errors);
        engine.StatusUpdated += status =>
        {
            if (Volatile.Read(ref newRunStarted) == 1 && (!status.Running || status.Text.Contains("失败", StringComparison.Ordinal)))
                Interlocked.Increment(ref staleStatuses);
        };
        try
        {
            Require(engine.Start(Slots("q", "w")), "旧双槽运行启动失败");
            Require(firstEntered.Wait(TimeSpan.FromSeconds(2)) && secondEntered.Wait(TimeSpan.FromSeconds(2)),
                "旧双槽动作未开始");
            using var registration = secondToken.Register(() =>
            {
                cancelling.Set();
                releaseCancellation.Wait(TimeSpan.FromSeconds(4));
            });
            failOld.TrySetResult();
            Require(cancelling.Wait(TimeSpan.FromSeconds(2)), "故障收尾未进入其他槽位的取消回调");
            Volatile.Write(ref newRunStarted, 1);
            // 第三 worker 可立即接手新轮；第一 worker 仍停留在旧轮的取消收尾中。
            var next = Slots("e", "w", "r");
            next[1].Enabled = false;
            Require(engine.Start(next), "旧轮收尾期间应可启动新轮");
            Require(restarted.Wait(TimeSpan.FromSeconds(2)), "新轮独立槽位未运行");
            releaseCancellation.Set();
            Require(oldWorkerResumed.Wait(TimeSpan.FromSeconds(2)), "旧 worker 未完成收尾并接手新轮");
            Require(engine.IsRunning && errors == 0 && staleStatuses == 0,
                "旧轮取消完成后不得向新轮发布错误或停止状态");
        }
        finally
        {
            Volatile.Write(ref newRunStarted, 0);
            failOld.TrySetResult();
            releaseCancellation.Set();
            Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "跨轮发布检查未完整关闭 worker");
        }
    });

    private static object ReadField(KeyLoopEngine engine, string name)
        => typeof(KeyLoopEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;

    private static void ReportLateRunningStatus(KeyLoopEngine engine, int runId, bool force)
        => typeof(KeyLoopEngine).GetMethod("ReportStatus", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [typeof(string), typeof(bool), typeof(bool), typeof(int?)], modifiers: null)!
            .Invoke(engine, ["按键循环运行中...", true, force, runId]);

    private static CancellationTokenSource ReadRunSource(KeyLoopEngine engine, int index)
    {
        var slots = (Array)typeof(KeyLoopEngine).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
        var runtime = slots.GetValue(index)!;
        return (CancellationTokenSource)runtime.GetType().GetProperty("RunCts")!.GetValue(runtime)!;
    }

    private static bool IsDisposed(CancellationTokenSource source)
    {
        try { _ = source.Token; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static KeyLoopSlot[] Slots(params string[] keys) => keys
        .Select(key => new KeyLoopSlot { Enabled = true, Key = key, DelaySeconds = 0.1 }).ToArray();

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void WithHost(Action<ToolHost> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ToolHost? host = null;
            try
            {
                host = new ToolHost(new ToolRegistry());
                check(host);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (host is not null)
                {
                    host.Hotkeys.UnregisterAll();
                    host.Sound.Dispose();
                    host.Notification.Dispose();
                    host.Network.Dispose();
                }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(15)), "KeyLoop 审计修复检查超时");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
