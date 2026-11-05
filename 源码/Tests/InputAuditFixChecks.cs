using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using ShiKe.Host;
using ShiKe.Services;

internal static class InputAuditFixChecks
{
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;

    public static void InjectedHoldEventsDoNotStartOrRelease()
    {
        foreach (var key in new[] { "Shift", "Alt", "C", "Ctrl" })
        {
            var starts = 0;
            var stops = 0;
            var manager = CreateHoldManager(key, () => true, () => starts++, () => stops++);
            var vk = HotkeyParser.ParseHold(key)!.Value.Vk;
            foreach (var flags in new uint[] { 0x10, 0x02, 0x12 })
            {
                manager.HandleKeyboardHookEvent(vk, KeyDown, flags);
                manager.HandleKeyboardHookEvent(vk, KeyUp, flags | 0x80);
            }
            Require(starts == 0 && stops == 0, $"合成 {key} 不得派发 Hold");

            manager.HandleKeyboardHookEvent(vk, KeyDown, 0);
            manager.HandleKeyboardHookEvent(vk, KeyUp, 0x90);
            Require(starts == 1 && stops == 0, $"合成释放不得终止物理 {key} Hold");
            manager.HandleKeyboardHookEvent(vk, KeyUp, 0x80);
            Require(starts == 1 && stops == 1, $"物理 {key} 松开仍须停止 Hold");
        }
    }

    public static void PhysicalHoldRetainsModifiersAndForegroundBehavior()
    {
        var foreground = true;
        var starts = 0;
        var stops = 0;
        var manager = CreateHoldManager("Ctrl+F11", () => foreground, () => starts++, () => stops++);
        const ushort leftControl = 0xA2;
        const ushort f11 = 0x7A;
        manager.HandleKeyboardHookEvent(leftControl, KeyDown, 0x10);
        manager.HandleKeyboardHookEvent(f11, KeyDown, 0);
        manager.HandleKeyboardHookEvent(f11, KeyUp, 0x80);
        Require(starts == 0, "合成修饰键不得满足物理组合键");

        manager.HandleKeyboardHookEvent(leftControl, KeyDown, 0);
        manager.HandleKeyboardHookEvent(f11, KeyDown, 0);
        manager.HandleKeyboardHookEvent(f11, KeyDown, 0);
        Require(starts == 1, "物理组合键开始一次，长按重复事件不重启");
        foreground = false;
        manager.HandleKeyboardHookEvent(leftControl, KeyUp, 0x80);
        Require(stops == 1, "失焦后松开修饰键也必须停止 Hold");
        manager.HandleKeyboardHookEvent(leftControl, KeyDown, 0);
        manager.HandleKeyboardHookEvent(f11, KeyDown, 0);
        Require(starts == 1, "非目标前台不得启动物理 Hold");
    }

    public static void HideoutStopsWhenForegroundLeavesDuringText()
    {
        var foreground = true;
        var events = new List<string>();
        var task = SendCommand("abc", () => foreground, events, (ms, _) =>
        {
            if (ms == 5) foreground = false;
            return Task.CompletedTask;
        });
        ExpectCancelled(task);
        Require(events.SequenceEqual(new[] { "EnterDown", "EnterUp", "aDown", "aUp" }),
            "失焦后不得继续字符或发送最终 Enter");
        foreground = true;
        Require(task.IsCanceled && events.Count == 4, "切回游戏不能恢复已经取消的半条命令");
    }

    public static void HideoutChecksForegroundBeforeBothEnters()
    {
        var events = new List<string>();
        ExpectCancelled(SendCommand("x", () => false, events, (_, _) => Task.CompletedTask));
        Require(events.Count == 0, "失焦时不得发送打开聊天框的 Enter");

        var foreground = true;
        ExpectCancelled(SendCommand("x", () => foreground, events, (ms, _) =>
        {
            if (ms == 5) foreground = false;
            return Task.CompletedTask;
        }));
        Require(events.SequenceEqual(new[] { "EnterDown", "EnterUp", "xDown", "xUp" }),
            "最后一个字符输入后失焦，也不得发送确认 Enter");

        events.Clear();
        foreground = true;
        ExpectCancelled(SendCommand("x", () => foreground, events, (ms, _) =>
        {
            if (ms == 100) foreground = false;
            return Task.CompletedTask;
        }));
        Require(events.SequenceEqual(new[] { "EnterDown", "EnterUp" }),
            "打开聊天框后的等待期间失焦，应在第一个字符前结束");
    }

    public static void HideoutCancellationAndInputFailureReleaseKeys()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        var task = SendCommand("x", () => true, events, (ms, token) =>
        {
            if (ms == 10)
            {
                Require(!token.CanBeCanceled, "Enter 按下与释放之间不可被取消中断");
                cancellation.Cancel();
            }
            return Task.CompletedTask;
        }, cancellation.Token);
        ExpectCancelled(task);
        Require(events.SequenceEqual(new[] { "EnterDown", "EnterUp" }), "取消后仍释放已按下的 Enter");

        events.Clear();
        var failed = ForegroundCommandInput.SendAsync("x", () => true, () => { },
            () => events.Add("EnterDown"), () => events.Add("EnterUp"),
            (_, down) =>
            {
                events.Add(down ? "xDown" : "xUp");
                if (down) throw new InvalidOperationException("受控字符发送失败");
            }, (_, _) => Task.CompletedTask, CancellationToken.None);
        try { failed.GetAwaiter().GetResult(); throw new Exception("字符失败必须结束命令"); }
        catch (InvalidOperationException error) when (error.Message == "受控字符发送失败") { }
        Require(events.SequenceEqual(new[] { "EnterDown", "EnterUp", "xDown", "xUp" }),
            "字符输入失败仍须释放，且不能继续确认命令");
    }

    public static void HideoutPreservesSequenceAndTiming()
    {
        var events = new List<string>();
        var delays = new List<int>();
        SendCommand("ab", () => true, events, (ms, _) =>
        {
            delays.Add(ms);
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        Require(events.SequenceEqual(new[] { "EnterDown", "EnterUp", "aDown", "aUp", "bDown", "bUp", "EnterDown", "EnterUp" }),
            "正常回城仍应完整输入并确认一次");
        Require(delays.SequenceEqual(new[] { 10, 100, 5, 5, 10 }), "回城原输入等待保持");
    }

    public static void HideoutReleasesEnterWithoutUiContinuation()
    {
        var previous = SynchronizationContext.Current;
        var context = new NonPumpingContext();
        var resumeEnter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var task = SendCommand("x", () => true, events,
                (ms, _) => ms == 10 ? resumeEnter.Task : Task.CompletedTask, cancellation.Token);
            Require(events.SequenceEqual(new[] { "EnterDown" }), "应在 Enter 按下后的等待期间取消");
            cancellation.Cancel();
            resumeEnter.TrySetResult();
            // 模拟关闭期间 UI 线程同步等待；不能依赖它泵消息才能释放按键。
            ExpectCancelled(task.WaitAsync(TimeSpan.FromSeconds(2)));
            Require(events.SequenceEqual(new[] { "EnterDown", "EnterUp" }),
                "UI 不处理续体时也必须释放 Enter，不能再继续命令");
            Require(context.PostCount == 0, "回城清理不得依赖 UI 续体");
        }
        finally
        {
            resumeEnter.TrySetResult();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    public static void CraftSendBoundaryRejectsForegroundLossAfterWaiting()
    {
        foreach (var operation in new[] { "选择通货", "点击装备", "复制物品" })
        {
            var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var foreground = true;
            var sent = new List<string>();
            var task = InputSendBoundary.WaitThenSendAsync(preparation.Task, () => foreground, () => { },
                () => RecordSend(operation, sent), CancellationToken.None);
            Require(!task.IsCompleted && sent.Count == 0, "准备等待结束前不能发送任何按下");
            foreground = false;
            preparation.TrySetResult();
            try { task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
            catch (InputSimulationException)
            {
                Require(sent.Count == 0, $"{operation}等待中失焦，不得发送按下");
                foreground = true;
                Require(task.IsFaulted && sent.Count == 0, "恢复前台不得补发已经拒绝的输入");
                continue;
            }
            throw new InvalidOperationException($"{operation}等待后必须重新检查前台");
        }
    }

    public static void CraftSendBoundaryRejectsCancellationAfterWaiting()
    {
        foreach (var operation in new[] { "选择通货", "点击装备", "复制物品" })
        {
            using var cancellation = new CancellationTokenSource();
            var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = new List<string>();
            var task = InputSendBoundary.WaitThenSendAsync(preparation.Task, () => true, () => { },
                () => RecordSend(operation, sent), cancellation.Token);
            // 准备任务故意不响应取消，覆盖最后一个等待完成与实际发送之间的边界。
            cancellation.Cancel();
            preparation.TrySetResult();
            ExpectCancelled(task.WaitAsync(TimeSpan.FromSeconds(2)));
            Require(sent.Count == 0, $"{operation}等待中取消后不得发送");
        }

        using var emergency = new CancellationTokenSource();
        var emergencySent = false;
        ExpectCancelled(InputSendBoundary.WaitThenSendAsync(Task.CompletedTask, () => true,
            emergency.Cancel, () => emergencySent = true, emergency.Token));
        Require(!emergencySent, "紧急检查触发取消后必须在实际发送前再次检查令牌");
    }

    public static void CraftSendBoundaryPreservesPreparationAndSendOrder()
    {
        foreach (var (operation, waits) in new[]
        {
            ("选择通货", new[] { 30 }),
            ("点击装备", new[] { 10, 23 }),
            ("复制物品", new[] { 20 }),
        })
        {
            var events = new List<string>();
            var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Prepare()
            {
                foreach (var wait in waits) events.Add($"等待{wait}");
                await prepared.Task;
                events.Add("准备完成");
            }
            var task = InputSendBoundary.WaitThenSendAsync(Prepare(), () => true,
                () => events.Add("紧急检查"), () => RecordSend(operation, events), CancellationToken.None);
            Require(events.SequenceEqual(waits.Select(wait => $"等待{wait}")), "共享发送步骤不得跳过准备等待");
            prepared.TrySetResult();
            task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var expected = waits.Select(wait => $"等待{wait}").Concat(new[] { "准备完成", "紧急检查" }).ToList();
            RecordSend(operation, expected);
            Require(events.SequenceEqual(expected), "正常路径应在全部原准备步骤完成后发送一次，复制按放顺序保持");
        }
    }

    public static void KeyboardScanDescriptionsPreserveOrdinaryAndNumpadKeys()
    {
        foreach (var vk in new ushort[] { 0x10, 0x11, 0x12, 0x0D, 0x1B, 0x20, 0x31, 0x41, 0x43,
                     0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69 })
        {
            var scan = (ushort)MapVirtualKeyW(vk, 0); // 仅查询本机映射，不发送输入。
            Require(scan != 0, $"按键0x{vk:X}应有实际扫描码");
            var down = InputSimulator.DescribeKeyboardInput(vk, false, scan);
            var up = InputSimulator.DescribeKeyboardInput(vk, true, scan);
            Require(down.ScanCode == (scan & 0xFF) && up.ScanCode == down.ScanCode,
                "普通键的按下/释放必须保持原扫描码");
            Require(down.Flags == 0x08 && up.Flags == 0x0A, "普通键和小键盘数字不得误加扩展标志");
        }
    }

    public static void KeyboardScanDescriptionsDistinguishExtendedKeys()
    {
        foreach (var vk in new ushort[] { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
                     0x2D, 0x2E, 0x5B, 0x5C, 0x90 })
        {
            var scan = (ushort)MapVirtualKeyW(vk, 0); // 模式0缺失的E0身份由明确VK规则补足。
            Require(scan != 0, $"扩展按键0x{vk:X}应有实际扫描码");
            var down = InputSimulator.DescribeKeyboardInput(vk, false, scan);
            var up = InputSimulator.DescribeKeyboardInput(vk, true, scan);
            Require(down.ScanCode == (scan & 0xFF) && up.ScanCode == down.ScanCode,
                "扩展键按下/释放必须使用相同基础扫描码");
            Require(down.Flags == 0x09 && up.Flags == 0x0B, "导航/Windows键的按下与释放都必须包含扩展标志");
        }
        var arrowUp = InputSimulator.DescribeKeyboardInput(0x26, false, 0x48);
        var numpad8 = InputSimulator.DescribeKeyboardInput(0x68, false, 0x48);
        Require(arrowUp.ScanCode == numpad8.ScanCode && arrowUp.Flags != numpad8.Flags,
            "Up与NumPad8扫描码相同，也必须保留不同按键身份");
    }

    private static void RecordSend(string operation, List<string> events)
    {
        if (operation == "复制物品")
            CraftCopyShortcut.Send(key => events.Add($"按下{key}"), key => events.Add($"释放{key}"),
                key => events.Add($"清理{key}"));
        else events.Add(operation);
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    private sealed class NonPumpingContext : SynchronizationContext
    {
        private int _posts;
        internal int PostCount => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback callback, object? state)
            => Interlocked.Increment(ref _posts);
    }

    private static Task SendCommand(string command, Func<bool> isTargetForeground, List<string> events,
        Func<int, CancellationToken, Task> delay, CancellationToken token = default)
        => ForegroundCommandInput.SendAsync(command, isTargetForeground, () => { },
            () => events.Add("EnterDown"), () => events.Add("EnterUp"),
            (ch, down) => events.Add($"{ch}{(down ? "Down" : "Up")}"), delay, token);

    private static HotkeyManager CreateHoldManager(string key, Func<bool> foreground, Action start, Action stop)
    {
        var manager = new HotkeyManager(foreground, callback => callback());
        var request = new HotkeyRequest
        {
            Key = key, DisplayName = "受控 Hold", CheckForeground = true, Mode = HotkeyMode.Hold,
            Handler = start, ReleaseHandler = stop,
        };
        // 仅建立托管绑定；不注册原生热键、不安装键盘钩子，也不发送输入。
        var bindingType = typeof(HotkeyManager).GetNestedType("HoldBinding", BindingFlags.NonPublic)!;
        var binding = Activator.CreateInstance(bindingType)!;
        var parsed = HotkeyParser.ParseHold(key)!.Value;
        bindingType.GetProperty("Request")!.SetValue(binding, request);
        bindingType.GetProperty("Hotkey")!.SetValue(binding, parsed);
        var bindings = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(bindingType))!;
        bindings.Add(binding);
        var map = (IDictionary)typeof(HotkeyManager).GetField("_holdByVk", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        map.Add(parsed.Vk, bindings);
        return manager;
    }

    private static void ExpectCancelled(Task task)
    {
        try { task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("失焦或停止后必须取消本次命令");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
