using System.Reflection;
using System.Windows;
using ShiKe.Host;
using ShiKe.Tools.Craft;

internal static class CraftAuditFixChecks
{
    public static void StopAfterClaimDoesNotBeginInput() => OnSta(() =>
    {
        var claimed = Signal();
        var resume = Signal();
        var stopped = Signal();
        var workCalls = 0;
        var inputCalls = 0;
        var cleanupCalls = 0;
        var engine = new CraftEngine(new ToolHost(new ToolRegistry()),
            (_, _, _) => { workCalls++; return Task.CompletedTask; },
            token =>
            {
                if (token.CanBeCanceled) inputCalls++;
                else cleanupCalls++;
                return Task.CompletedTask;
            }, WaitForCancellation, () => { claimed.TrySetResult(); Await(resume.Task); });
        engine.Stopped += _ => stopped.TrySetResult();
        try
        {
            Start(engine, Rules("第一轮"), Coordinates(100));
            Await(claimed.Task);
            engine.Stop();
            Require(!engine.IsRunning, "停止必须立即改变请求状态");
            resume.TrySetResult();
            Await(stopped.Task);
            Equal(0, inputCalls, "已经认领但尚未执行的运行被停止后，不能发送准备输入");
            Equal(0, workCalls, "停止不能在创建工作令牌前丢失");
            Equal(1, cleanupCalls, "停止路径仍须执行一次兜底释放");
        }
        finally { resume.TrySetResult(); Shutdown(engine); }
    });

    public static void RapidRestartKeepsActiveAndPendingSnapshotsSeparate() => OnSta(() =>
    {
        var firstStarted = Signal();
        var finishFirst = Signal();
        var firstCleanup = Signal();
        var finishCleanup = Signal();
        var secondStarted = Signal();
        var secondStopped = Signal();
        var runCalls = 0;
        var cleanups = 0;
        CraftRules? secondRules = null;
        IReadOnlyDictionary<string, Point>? secondCoordinates = null;
        var engine = new CraftEngine(new ToolHost(new ToolRegistry()),
            async (rules, coordinates, token) =>
            {
                if (Interlocked.Increment(ref runCalls) == 1)
                {
                    firstStarted.TrySetResult();
                    await finishFirst.Task;
                    token.ThrowIfCancellationRequested();
                }
                else
                {
                    secondRules = rules;
                    secondCoordinates = coordinates;
                    secondStarted.TrySetResult();
                }
            }, async token =>
            {
                if (!token.CanBeCanceled && Interlocked.Increment(ref cleanups) == 1)
                {
                    firstCleanup.TrySetResult();
                    await finishCleanup.Task;
                }
            }, WaitForCancellation);
        engine.Stopped += _ => secondStopped.TrySetResult();
        try
        {
            Start(engine, Rules("第一轮"), Coordinates(100));
            Await(firstStarted.Task);
            engine.Stop();
            var nextRules = Rules("第二轮");
            var nextCoordinates = Coordinates(200);
            Start(engine, nextRules, nextCoordinates);
            nextRules.PrimaryAffixes.Clear();
            nextCoordinates["item"] = new Point(999, 999);
            Equal("第一轮", Field<CraftRules>(engine, "_rules").PrimaryAffixes.Single().Text,
                "待运行请求不能覆盖旧任务仍在访问的规则字段");
            Equal(new Point(100, 100), Field<Dictionary<string, Point>>(engine, "_coordinates")["item"],
                "待运行请求不能覆盖旧任务仍在访问的坐标字段");
            finishFirst.TrySetResult();
            Await(firstCleanup.Task);
            Require(engine.IsRunning, "旧任务清理不能取消新请求的运行状态");
            Require(!secondStarted.Task.IsCompleted, "旧任务释放按键完成前不能开始新任务");
            finishCleanup.TrySetResult();
            Await(secondStopped.Task);
            Equal(2, runCalls, "快速停止再启动应恰好执行两个独立任务");
            Equal("第二轮", secondRules!.PrimaryAffixes.Single().Text, "待运行请求必须持有规则深拷贝");
            Equal(new Point(200, 200), secondCoordinates!["item"], "待运行请求必须持有坐标拷贝");
        }
        finally
        {
            finishFirst.TrySetResult();
            finishCleanup.TrySetResult();
            Shutdown(engine);
        }
    });

    public static void StopCancelsRestartQueuedBehindCleanup() => OnSta(() =>
    {
        var host = new ToolHost(new ToolRegistry());
        var firstStarted = Signal();
        var firstCleanup = Signal();
        var finishCleanup = Signal();
        var runCalls = 0;
        var stoppedEvents = 0;
        CraftEngine? engine = null;
        engine = new CraftEngine(host,
            async (_, _, token) =>
            {
                Interlocked.Increment(ref runCalls);
                typeof(CraftEngine).GetMethod("ReportStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(engine, new object[] { "第一轮运行中", true });
                firstStarted.TrySetResult();
                await WaitForCancellation(token);
            }, async token =>
            {
                if (!token.CanBeCanceled)
                {
                    firstCleanup.TrySetResult();
                    await finishCleanup.Task;
                }
            }, WaitForCancellation);
        engine.StatusUpdated += status => host.Statuses.Report("craft", status.Text, status.Running, status.UseCount);
        engine.Stopped += reason =>
        {
            Interlocked.Increment(ref stoppedEvents);
            host.Statuses.Report("craft", reason, false, engine.UseCount);
        };
        try
        {
            Start(engine, Rules("第一轮"), Coordinates(100));
            Await(firstStarted.Task);
            engine.Stop();
            Await(firstCleanup.Task);
            Start(engine, Rules("第二轮"), Coordinates(200));
            engine.Stop();
            Require(!engine.IsRunning, "再次停止必须同时撤销待运行请求");
            Equal(1, stoppedEvents, "待运行请求没有finally，停止方必须发布且只发布一次停止事件");
            Require(host.Statuses.Read("craft").Item1 is { Running: false }, "宿主可见状态必须同步停止");
            finishCleanup.TrySetResult();
            Shutdown(engine);
            Equal(1, runCalls, "被撤销的待运行请求不得在旧任务清理后执行");
            Equal(1, stoppedEvents, "旧任务收尾不能再次发布停止或覆盖新状态");
        }
        finally { finishCleanup.TrySetResult(); Shutdown(engine); }
    });

    public static void PollingEndsOnSuccessExceptionAndCancellation() => OnSta(() =>
    {
        foreach (var ending in new[] { "success", "exception", "cancel" })
        {
            var pollStarted = Signal();
            var workStarted = Signal();
            var stopped = Signal();
            var pollExited = 0;
            var errors = new List<string>();
            var engine = new CraftEngine(new ToolHost(new ToolRegistry()),
                async (_, _, token) =>
                {
                    await pollStarted.Task;
                    workStarted.TrySetResult();
                    if (ending == "exception") throw new IOException("受控洗装失败");
                    if (ending == "cancel") await WaitForCancellation(token);
                }, _ => Task.CompletedTask,
                async token =>
                {
                    pollStarted.TrySetResult();
                    try { await WaitForCancellation(token); }
                    finally { Interlocked.Increment(ref pollExited); }
                });
            engine.ErrorOccurred += errors.Add;
            engine.Stopped += _ => stopped.TrySetResult();
            try
            {
                Start(engine, Rules("轮询验证"), Coordinates(100));
                Await(workStarted.Task);
                if (ending == "cancel") engine.Stop();
                Await(stopped.Task);
                Equal(1, pollExited, $"{ending}路径必须在停止事件前结束停止键轮询");
                Equal(ending == "exception" ? 1 : 0, errors.Count, "原运行错误应保留，取消不应伪报错误");
                if (ending == "exception") Require(errors[0].Contains("受控洗装失败"), "原异常不能被清理覆盖");
            }
            finally { Shutdown(engine); }
        }
    });

    public static void RestartFromSuccessStatusSuppressesOldSuccessEvent() => OnSta(() =>
    {
        var stopped = Signal();
        var runCalls = 0;
        var successEvents = 0;
        CraftEngine? engine = null;
        engine = new CraftEngine(new ToolHost(new ToolRegistry()),
            (_, _, token) =>
            {
                if (Interlocked.Increment(ref runCalls) == 1)
                {
                    try
                    {
                        typeof(CraftEngine).GetMethod("OnSuccess", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(engine, new object[] { token });
                    }
                    catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException cancelled)
                    {
                        throw cancelled;
                    }
                }
                return Task.CompletedTask;
            }, _ => Task.CompletedTask, WaitForCancellation);
        engine.StatusUpdated += status =>
        {
            if (!status.Text.StartsWith("匹配成功!", StringComparison.Ordinal)) return;
            engine.Stop();
            Start(engine, Rules("新请求"), Coordinates(200));
        };
        engine.MatchFound += () => successEvents++;
        engine.Stopped += _ => stopped.TrySetResult();
        try
        {
            Start(engine, Rules("旧请求"), Coordinates(100));
            Await(stopped.Task);
            Equal(2, runCalls, "成功状态回调里的新请求必须独立执行");
            Equal(0, successEvents, "停止/重启后旧任务不得再发成功事件或成功副作用");
        }
        finally { Shutdown(engine); }
    });

    public static void Mode3ExcludedAugmentationNeverAdvancesToRegal()
    {
        foreach (var total in new[] { 0, 1, 2, 3 })
        {
            var rules = Rules("目标");
            rules.Mode = CraftMode.AltAugRegal;
            rules.PrimaryHitCount = total > 0 ? 1 : 0;
            rules.SecondaryHitCount = Math.Max(0, total - 1);
            rules.SecondaryAffixes.Add(new AffixRule { Text = "次甲" });
            rules.SecondaryAffixes.Add(new AffixRule { Text = "次乙" });
            rules.ExcludeAffixes.Add(new AffixRule { Text = "禁用" });
            Require(rules.Validate().Ok, "测试必须使用合法命中需求");
            var item = AffixEngine.ParseItem("物品类别: 头盔\n稀有度: 魔法\n铁帽\n--------\n" +
                "{ 前缀属性 \"测试前缀\" (等阶: 1) }\n目标\n" +
                "{ 后缀属性 \"测试后缀\" (等阶: 1) }\n禁用");
            var result = AffixEngine.CheckAffixes(item, rules);
            Require(result.HasExclude, "样本必须触发实际解析后的排除匹配");
            Equal(Mode3MagicDecision.ContinueAlteration,
                CraftDecisions.AfterAugmentation(result.TotalHits, Math.Max(0, total - 1), result.HasExclude),
                "增幅排除命中不能因阈值为零而进入富豪");
            Equal(Mode3MagicDecision.ProceedToRegal,
                CraftDecisions.AfterAugmentation(Math.Max(0, total - 1), Math.Max(0, total - 1), false),
                "无排除且达阈值仍必须进入富豪，保留原边界");
        }
    }

    private static CraftRules Rules(string target)
    {
        var rules = new CraftRules { Mode = CraftMode.Single };
        rules.PrimaryAffixes.Add(new AffixRule { Text = target });
        return rules;
    }

    private static Dictionary<string, Point> Coordinates(int position) => new()
    {
        [Currency.Alteration] = new Point(position + 20, position + 20),
        ["item"] = new Point(position, position),
    };

    private static void Start(CraftEngine engine, CraftRules rules, Dictionary<string, Point> coordinates)
        => engine.Start(rules, coordinates, 33, false, false, "", 10, false, false);

    private static Task WaitForCancellation(CancellationToken token) => Task.Delay(Timeout.Infinite, token);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    private static T Field<T>(CraftEngine engine, string name)
        => (T)typeof(CraftEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
    private static void Shutdown(CraftEngine engine)
        => Require(engine.Shutdown(TimeSpan.FromSeconds(2)), "无输入洗装验证必须结束常驻任务");

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(20)), "无输入洗装验证线程未按时结束");
        if (failure is not null) throw failure;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}；预期{expected}，实际{actual}");
    }
}
