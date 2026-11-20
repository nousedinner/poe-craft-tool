using System.Diagnostics;
using ShiKe.Services;

internal static class ClipboardPerformanceChecks
{
    public static void CancelledRequestsDoNotOpenClipboard()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        ExpectCancellation(() => ClipboardHelper.GetText(stop.Token));
        ExpectCancellation(() => ClipboardHelper.TryClear(stop.Token));
        ExpectCancellation(() => ClipboardHelper.GetTextWrittenAfter(10, stop.Token));
        var opens = 0;
        ExpectCancellation(() => ClipboardHelper.WithOpenClipboard(() => { opens++; return true; },
            () => "value", () => { }, "", stop.Token));
        Require(opens == 0, "预先取消不得访问剪贴板");
    }

    public static void RetryBudgetAndResultsStayUnchanged()
    {
        var opens = 0;
        var waits = 0;
        var closes = 0;
        var result = ClipboardHelper.WithOpenClipboard(() => ++opens == 3, () => "物品文本",
            () => closes++, "", CancellationToken.None, (ms, _) =>
            {
                Require(ms == 20, "原重试间隔不得缩短"); waits++;
            });
        Require(result == "物品文本" && opens == 3 && waits == 2 && closes == 1,
            "成功后必须及时返回并关闭，不增加操作");

        opens = waits = closes = 0;
        result = ClipboardHelper.WithOpenClipboard(() => { opens++; return false; }, () => "错误",
            () => closes++, "", CancellationToken.None, (ms, _) => { Require(ms == 20, "保持20ms"); waits++; });
        Require(result == "" && opens == 5 && waits == 5 && closes == 0,
            "持续占用必须保持五次尝试及失败结果");

        opens = closes = 0;
        var cleared = ClipboardHelper.WithOpenClipboard(() => { opens++; return true; }, () => false,
            () => closes++, false, CancellationToken.None);
        Require(!cleared && opens == 1 && closes == 1, "已打开但清空失败不应重新清空");
    }

    public static void CancellationAndFailureAlwaysCloseOpenedClipboard()
    {
        using var stop = new CancellationTokenSource();
        var actions = 0;
        var closes = 0;
        ExpectCancellation(() => ClipboardHelper.WithOpenClipboard(() => { stop.Cancel(); return true; },
            () => { actions++; return ""; }, () => closes++, "", stop.Token));
        Require(actions == 0 && closes == 1, "取得剪贴板后取消也必须关闭且不继续读取");

        using var during = new CancellationTokenSource();
        ExpectCancellation(() => ClipboardHelper.WithOpenClipboard(() => true,
            () => { during.Cancel(); return "old"; }, () => closes++, "", during.Token));
        Require(closes == 2, "读取期间取消不能返回旧结果或遗漏关闭");

        try
        {
            ClipboardHelper.WithOpenClipboard<string>(() => true,
                () => throw new IOException("模拟读取失败"), () => closes++, "", CancellationToken.None);
            throw new InvalidOperationException("读取错误不得被吞掉");
        }
        catch (IOException) { }
        Require(closes == 3, "读取异常仍需关闭");
    }

    public static void CancellationInterruptsWaitWithoutAnotherAttempt()
    {
        using var stop = new CancellationTokenSource();
        var opens = 0;
        var waits = 0;
        ExpectCancellation(() => ClipboardHelper.WithOpenClipboard(() => { opens++; return false; },
            () => "", () => { }, "", stop.Token, (ms, token) =>
            {
                waits++;
                stop.Cancel();
                ClipboardHelper.WaitForRetry(ms, token);
            }));
        Require(opens == 1 && waits == 1, "取消后不得进入下一次打开尝试");

        using var waiting = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        var work = Task.Run(() =>
        {
            entered.Set();
            ExpectCancellation(() => ClipboardHelper.WaitForRetry(5000, waiting.Token));
        });
        Require(entered.Wait(TimeSpan.FromSeconds(2)), "等待检查未启动");
        var clock = Stopwatch.StartNew();
        waiting.Cancel();
        Require(work.Wait(TimeSpan.FromSeconds(1)), "可取消等待不应继续睡满固定时长");
        Console.WriteLine($"INFO clipboard cancellable-wait completion={clock.Elapsed.TotalMilliseconds:F2}ms (synthetic wait)");
    }

    public static void ClipboardRequiresANewWrite()
    {
        const string item = "物品类别: 节杖\n稀有度: 稀有";
        const uint before = 20;
        Require(Read(20) == "", "发键后仍是原序号，有效格式的旧物品文本也必须拒绝");
        Require(Read(21) == item, "有新写入即读取内容，游戏没有登记剪贴板所有者也可通过");
        Require(Read(0) == "", "无法读取当前序号时不能沿用物品文本");

        var sequence = before;
        var rendered = ClipboardHelper.ReadOpenedTextWrittenAfter(before, () =>
        {
            sequence++;
            return item;
        }, () => sequence);
        Require(rendered == item, "延迟渲染的复制在取得文本后才比较序号");

        string Read(uint currentSequence)
            => ClipboardHelper.ReadOpenedTextWrittenAfter(before, () => item, () => currentSequence);
    }

    public static void CopyFallbackRequiresShiftAndFreshSuccess()
    {
        const string item = "物品类别: 节杖\n稀有度: 稀有";
        var copy = new CraftCopyPreference();
        var stale = ClipboardHelper.ReadOpenedTextWrittenAfter(20, () => item, () => 20);
        Require(!copy.UseAlt(shiftHeld: true) && !copy.IsTryingAlternate(shiftHeld: true), "新任务优先尝试 Ctrl+C");
        Require(!copy.RecordAttempt(receivedFreshItem: false, shiftHeld: false) && !copy.IsTryingAlternate(shiftHeld: true),
            "未保持 Shift 的启动复制失败不能预定备用按法");
        Require(!copy.RecordAttempt(ClipboardHelper.IsItemText(stale), shiftHeld: true) &&
                copy.UseAlt(shiftHeld: true) && copy.IsTryingAlternate(shiftHeld: true),
            "旧的有效物品文本不能阻止备用复制");
        Require(!copy.UseAlt(shiftHeld: false) && !copy.IsTryingAlternate(shiftHeld: false),
            "即使备用按法已待用，未保持 Shift 时也只能使用 Ctrl+C");
        var fresh = ClipboardHelper.ReadOpenedTextWrittenAfter(21, () => item, () => 22);
        Require(copy.RecordAttempt(ClipboardHelper.IsItemText(fresh), shiftHeld: true), "备用按法取得新文本后应切换首选");
        copy.BeginItemRead();
        Require(copy.UseAlt(shiftHeld: true) && !copy.IsTryingAlternate(shiftHeld: true), "下一次读取沿用本轮验证过的 Ctrl+Alt+C");
        Require(!copy.UseAlt(shiftHeld: false) && !copy.RecordAttempt(receivedFreshItem: true, shiftHeld: false) &&
                copy.UseAlt(shiftHeld: true), "未保持 Shift 的普通复制成功不能覆盖已验证的组合键");
        Require(!copy.RecordAttempt(receivedFreshItem: true, shiftHeld: true) && !copy.IsTryingAlternate(shiftHeld: true),
            "成功读到同一文本仍是一次成功复制");

        copy.RecordAttempt(receivedFreshItem: false, shiftHeld: true);
        Require(!copy.UseAlt(shiftHeld: true) && copy.IsTryingAlternate(shiftHeld: true), "已选按法后来失败时也允许一次反向备用复制");
        Require(!copy.RecordAttempt(receivedFreshItem: false, shiftHeld: true) &&
                copy.UseAlt(shiftHeld: true) && !copy.IsTryingAlternate(shiftHeld: true),
            "备用失败时保持此前验证的首选");
        copy.RecordAttempt(receivedFreshItem: false, shiftHeld: true);
        Require(!copy.IsTryingAlternate(shiftHeld: true), "同一次物品读取最多试一次备用按法");

        copy.BeginItemRead();
        copy.RecordAttempt(receivedFreshItem: false, shiftHeld: true);
        Require(copy.IsTryingAlternate(shiftHeld: true) && !copy.UseAlt(shiftHeld: true), "下一次读取可重新进行一次备用尝试");
        Require(copy.RecordAttempt(receivedFreshItem: true, shiftHeld: true) && !copy.UseAlt(shiftHeld: true),
            "反向备用取得新文本后应恢复 Ctrl+C 首选");
    }

    private static void ExpectCancellation(Action action)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("应报告取消");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
