using System.Reflection;
using ShiKe.Host;
using ShiKe.Tools.Clicker;

// 注入受控点击与释放动作，整个检查不调用实际鼠标输入、热键或网络。
internal static class ClickerAuditFixChecks
{
    public static void SameRunLateRunningStatusDoesNotReplaceStop() => WithHost(host =>
    {
        var statuses = new List<ClickerStatus>();
        var engine = new ClickerEngine(host, () => true, (_, _) => Task.CompletedTask);
        engine.StatusUpdated += statuses.Add;
        try
        {
            var gate = typeof(ClickerEngine).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            lock (gate)
            {
                Require(engine.Start(ClickerMouseButton.Left, 33), "待运行连点启动失败");
                var runId = (int)typeof(ClickerEngine).GetField("_runId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                Require(engine.Stop(), "待运行连点停止失败");
                typeof(ClickerEngine).GetMethod("ReportStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(engine, new object?[] { "迟到的运行消息", true, true, runId });
                Require(statuses.Count == 2 && !statuses[^1].Running && statuses[^1].Text == "连点器已停止",
                    "同一轮已经停止后，迟到的运行状态不能覆盖停止文案");
            }
        }
        finally { Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "迟到状态验证必须完整关闭"); }
    });

    public static void OldWorkerFailureDoesNotAffectRestartedRun() => WithHost(host =>
    {
        using var firstEntered = new ManualResetEventSlim();
        using var nextEntered = new ManualResetEventSlim();
        var releaseOld = NewSignal();
        var errors = 0;
        var releases = 0;
        var engine = new ClickerEngine(host, () => true, async (button, token) =>
        {
            if (button == ClickerMouseButton.Left)
            {
                firstEntered.Set();
                await releaseOld.Task;
                throw new InvalidOperationException("受控的旧轮失败");
            }
            nextEntered.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, () => Interlocked.Increment(ref releases));
        engine.ErrorOccurred += _ => Interlocked.Increment(ref errors);
        try
        {
            Require(engine.Start(ClickerMouseButton.Left, 10), "旧轮启动失败");
            Require(firstEntered.Wait(TimeSpan.FromSeconds(2)), "旧轮受控点击未开始");
            Require(engine.Stop(), "旧轮停止失败");
            Require(engine.Start(ClickerMouseButton.Right, 15), "立即重启失败");
            releaseOld.TrySetResult();
            Require(nextEntered.Wait(TimeSpan.FromSeconds(2)), "旧 worker 未接手新轮");
            Require(engine.IsRunning && engine.ClickCount == 0 && errors == 0 && releases == 0,
                "旧轮不得改变新轮计数、错误或鼠标释放状态");
        }
        finally
        {
            releaseOld.TrySetResult();
            Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "重启检查未完整关闭 worker");
        }
    });

    public static void PendingRunSourcesAreReleased() => WithHost(host =>
    {
        var calls = 0;
        var engine = new ClickerEngine(host, () => true, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        try
        {
            var gate = ReadField(engine, "_gate");
            lock (gate)
            {
                for (var run = 0; run < 2; run++)
                {
                    Require(engine.Start(ClickerMouseButton.Left, 10), "待接手轮启动失败");
                    var source = (CancellationTokenSource)ReadField(engine, "_runCts");
                    Require(engine.Stop(), "待接手轮停止失败");
                    Require(IsDisposed(source), "未被 worker 接手的源必须由停止方释放");
                }
                Require(engine.Start(ClickerMouseButton.Right, 10), "待关闭轮启动失败");
                var pending = (CancellationTokenSource)ReadField(engine, "_runCts");
                engine.Shutdown(TimeSpan.Zero);
                Require(IsDisposed(pending), "关闭也必须释放未接手的源");
            }
            Require(SpinWait.SpinUntil(() => engine.Shutdown(TimeSpan.Zero), TimeSpan.FromSeconds(2)),
                "待接手检查未完整关闭 worker");
            Require(calls == 0, "已停止的待接手轮不能执行鼠标动作");
        }
        finally { engine.Shutdown(TimeSpan.FromSeconds(2)); }
    });

    public static void DisposedCancellationSnapshotIsHarmless()
    {
        // 固定 Stop 取到快照后、worker 已将它释放的边界，不依赖线程碰巧抢占。
        var retired = new CancellationTokenSource();
        retired.Dispose();
        ClickerEngine.CancelRun(retired, dispose: false);
        ClickerEngine.CancelRun(retired, dispose: true);
        ClickerEngine.CancelRun(null, dispose: true);

        var pending = new CancellationTokenSource();
        var token = pending.Token;
        ClickerEngine.CancelRun(pending, dispose: true);
        Require(token.IsCancellationRequested && IsDisposed(pending), "未接手源必须先取消再释放");
    }

    public static void EmergencyReasonSurvivesHostReset() => WithHost(host =>
    {
        using var entered = new ManualResetEventSlim();
        using var stopped = new ManualResetEventSlim();
        var release = NewSignal();
        var reason = string.Empty;
        var engine = new ClickerEngine(host, () => true, async (_, token) =>
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
            Require(engine.Start(ClickerMouseButton.Left, 10), "紧急停止检查启动失败");
            Require(entered.Wait(TimeSpan.FromSeconds(2)), "受控点击未开始");
            host.TriggerEmergencyStop();
            host.ResetEmergencyStop();
            release.TrySetResult();
            Require(stopped.Wait(TimeSpan.FromSeconds(2)), "紧急停止未发布结束状态");
            Require(!engine.IsRunning && reason == "连点器已紧急停止", "结束原因必须使用本轮令牌");
        }
        finally
        {
            release.TrySetResult();
            Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "紧急停止检查未完整关闭 worker");
        }
    });

    public static void RestartDuringCleanupSkipsStaleEvents() => WithHost(host =>
    {
        using var firstEntered = new ManualResetEventSlim();
        using var nextEntered = new ManualResetEventSlim();
        var failOld = NewSignal();
        var errors = 0;
        var failureStatuses = 0;
        var releases = 0;
        var restarted = false;
        ClickerEngine? engine = null;
        engine = new ClickerEngine(host, () => true, async (button, token) =>
        {
            if (button == ClickerMouseButton.Left)
            {
                firstEntered.Set();
                await failOld.Task;
                throw new InvalidOperationException("受控的旧轮故障");
            }
            nextEntered.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, () =>
        {
            // 确定性将新 Start 放在旧轮清理与发布之间，覆盖检查后再发布的窗口。
            if (Interlocked.Increment(ref releases) == 1)
                restarted = engine!.Start(ClickerMouseButton.Right, 15);
        });
        engine.ErrorOccurred += _ => Interlocked.Increment(ref errors);
        engine.StatusUpdated += status =>
        {
            if (status.Text.Contains("失败", StringComparison.Ordinal)) Interlocked.Increment(ref failureStatuses);
        };
        try
        {
            Require(engine.Start(ClickerMouseButton.Left, 10), "旧轮启动失败");
            Require(firstEntered.Wait(TimeSpan.FromSeconds(2)), "旧轮点击未开始");
            failOld.TrySetResult();
            Require(nextEntered.Wait(TimeSpan.FromSeconds(2)), "清理期间的新轮未启动");
            Require(restarted && engine.IsRunning && errors == 0 && failureStatuses == 0,
                "旧轮清理后不能向新轮发布停止状态或错误");
        }
        finally
        {
            failOld.TrySetResult();
            Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "清理期间重启检查未完整关闭 worker");
        }
    });

    private static object ReadField(ClickerEngine engine, string name)
        => typeof(ClickerEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;

    private static bool IsDisposed(CancellationTokenSource source)
    {
        try { _ = source.Token; return false; }
        catch (ObjectDisposedException) { return true; }
    }

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
        Require(thread.Join(TimeSpan.FromSeconds(15)), "Clicker 审计修复检查超时");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
