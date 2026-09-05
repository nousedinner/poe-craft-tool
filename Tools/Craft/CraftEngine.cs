using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Craft;

/// <summary>洗装状态推送（引擎 250ms 节流后发出）。</summary>
public sealed record CraftStatus(string Text, int UseCount, int MatchCount, bool Running);

/// <summary>
/// 洗装引擎（对齐 Python auto_operator.py CraftOperator v8）：
/// - 单 Task 常驻（启动即建永不销毁，Python 坑 #3 最终方案），ManualResetEventSlim 唤醒 + CTS 取消（唤醒/取消分离，坑 #5）
/// - 三模式 + Mode 2 子模式（改造+增幅 / 重铸+点金）+ Mode 3 可选崇高
/// - 通货切换统一释放修饰键并等待至少 0.2s；delay 表示通货点击后的首次服务器同步等待
/// - 紧急停止：光标 ≤(1,1) → InputSimulator 触发 EmergencyCts.Cancel（对齐 Python FAILSAFE）
/// - 状态推送 250ms 节流；错误先停线程再上报（坑 #17）
/// </summary>
public sealed class CraftEngine
{
    private readonly ToolHost _host;
    private readonly ManualResetEventSlim _wakeSignal = new(false);
    private CancellationTokenSource? _workCts;
    private bool _running;
    private readonly Task _thread;
    private int _runId;
    private DateTime _lastStatusTime = DateTime.MinValue;

    // 运行时参数（Start 快照）
    private CraftRules _rules = new();
    private Dictionary<string, Point> _coordinates = [];
    private int _delayMs = SettingsDefaults.DelayMs;
    private bool _soundEnabled = true;
    private bool _popupEnabled = true;
    private string _selectedSound = SettingsDefaults.SelectedSound;
    private int _exhaustionThreshold = SettingsDefaults.ClipboardUnchangedThreshold;
    private bool _mode2ScourAlch;
    private bool _useExalt;
    private bool _shutdownRequested;
    private string? _completionReason;
    private ushort _stopKeyVk; // F6 停止键的虚拟键码（轮询兜底用）
    private uint _stopKeyModifiers;
    private string? _activeCurrency;
    private Point _activeCurrencyCoordinate;

    // GetAsyncKeyState：轮询物理按键状态（兜底 RegisterHotKey 可能被游戏吞掉）
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    public bool IsRunning => _running;
    public int UseCount { get; private set; }
    public int MatchCount { get; private set; }

    /// <summary>状态更新（已节流）。</summary>
    public event Action<CraftStatus>? StatusUpdated;

    /// <summary>匹配成功（引擎已播音效+通知，宿主可再更新 UI）。</summary>
    public event Action? MatchFound;

    /// <summary>已停止（正常/耗尽/错误后均触发，参数为停止原因）。</summary>
    public event Action<string>? Stopped;

    /// <summary>错误（线程已停止后触发，坑 #17）。</summary>
    public event Action<string>? ErrorOccurred;

    public CraftEngine(ToolHost host)
    {
        _host = host;
        Diag.Log("[引擎] CraftEngine 构造，常驻线程启动");
        _thread = Task.Run(Loop); // 单常驻线程，启动即建
    }

    // ── 启停 ──

    public void Start(CraftRules rules, Dictionary<string, Point> coordinates, int delayMs,
        bool soundEnabled, bool popupEnabled, string selectedSound, int exhaustionThreshold,
        bool mode2ScourAlch, bool useExalt)
    {
        lock (this)
        {
            if (_running) return;
            if (_shutdownRequested)
            {
                ErrorOccurred?.Invoke("洗装引擎正在关闭，无法启动");
                return;
            }

            // 上次紧急停止（光标到角落误触发）残留的取消状态会令本次启动立即失效，
            // CTS 不可重置（Python Event.clear() 差异），故每次启动前重置（修复：启动后只动鼠标就停）
            if (_host.EmergencyCts.IsCancellationRequested)
            {
                Diag.Log("[引擎] Start: EmergencyCts 已取消，重置");
                _host.ResetEmergencyStop();
            }

            var missing = CheckCoordinates(rules, coordinates, mode2ScourAlch, useExalt);
            if (missing is not null)
            {
                ErrorOccurred?.Invoke($"缺少坐标: {missing}");
                return;
            }

            _rules = rules.CreateSnapshot();
            _coordinates = coordinates.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            _delayMs = delayMs;
            _soundEnabled = soundEnabled;
            _popupEnabled = popupEnabled;
            _selectedSound = selectedSound;
            _exhaustionThreshold = exhaustionThreshold;
            _mode2ScourAlch = mode2ScourAlch;
            _useExalt = useExalt;
            _completionReason = null;
            _activeCurrency = null;
            _activeCurrencyCoordinate = default;
            _host.Input.ResetCraftClickInterval();
            UseCount = 0;
            MatchCount = 0;
            _runId++;
            _running = true;
            _wakeSignal.Set();
            Diag.Log($"[引擎] Start: runId={_runId}, mode={rules.Mode}, delay={delayMs}ms, 坐标数={coordinates.Count}");
        }
    }

    public void Stop()
    {
        lock (this)
        {
            Diag.Log($"[引擎] Stop: runId={_runId}, _workCts={(_workCts != null ? "存在" : "null")}, " +
                     $"EmergencyCts.IsCancellationRequested={_host.EmergencyCts.IsCancellationRequested}");
            _running = false;
            _workCts?.Cancel();
            _wakeSignal.Set();
        }
    }

    /// <summary>永久关闭常驻循环，并在限定时间内等待 finally 完成按键释放。</summary>
    public bool Shutdown(TimeSpan timeout)
    {
        lock (this)
        {
            _shutdownRequested = true;
            _running = false;
            _workCts?.Cancel();
            _wakeSignal.Set();
        }
        try { return _thread.Wait(timeout); }
        catch (AggregateException) { return _thread.IsCompleted; }
    }

    /// <summary>设置停止键 VK 码（CraftTool 启动前调用，用于 GetAsyncKeyState 轮询兜底）。</summary>
    public void SetStopKey(string key)
    {
        var parsed = HotkeyParser.Parse(key);
        _stopKeyVk = parsed?.Vk ?? 0;
        _stopKeyModifiers = parsed?.Modifiers ?? 0;
    }

    /// <summary>
    /// 轮询停止键物理状态（对齐 Python _check_stop_key）。
    /// RegisterHotKey 在游戏前台可能被吞掉，此方法作为兜底。
    /// </summary>
    private bool IsStopKeyPhysicallyPressed()
    {
        if (_stopKeyVk == 0) return false;
        if ((GetAsyncKeyState(_stopKeyVk) & 0x8000) == 0) return false;
        return HotkeyParser.AreModifiersPressed(_stopKeyModifiers,
            vk => (GetAsyncKeyState(vk) & 0x8000) != 0);
    }

    /// <summary>坐标检查（对齐 Python _check_coordinates；Mode2 按子模式正确检查——修复 Python 错位 bug，方案 §6）。</summary>
    private static string? CheckCoordinates(CraftRules rules, Dictionary<string, Point> coords,
        bool mode2ScourAlch, bool useExalt)
    {
        static bool IsValid(Point c) => c.X != 0 || c.Y != 0;

        if (rules.Mode == CraftMode.Single)
        {
            if (!coords.TryGetValue(rules.SingleCurrency, out var c1) || !IsValid(c1))
                return $"{Currency.Label(rules.SingleCurrency)}坐标";
        }
        else if (rules.Mode == CraftMode.AltAug)
        {
            // 按子模式检查（C# 修复：Python 固定查改造/增幅，选重铸+点金时检查错位）
            if (mode2ScourAlch)
            {
                if (!coords.TryGetValue(Currency.Alchemy, out var alch) || !IsValid(alch))
                    return "点金石坐标";
                if (!coords.TryGetValue(Currency.Scouring, out var scour) || !IsValid(scour))
                    return "重铸石坐标";
            }
            else
            {
                if (!coords.TryGetValue(Currency.Alteration, out var alt) || !IsValid(alt))
                    return "改造石坐标";
                if (!coords.TryGetValue(Currency.Augmentation, out var aug) || !IsValid(aug))
                    return "增幅石坐标";
            }
        }
        else if (rules.Mode == CraftMode.AltAugRegal)
        {
            foreach (var key in new[] { Currency.Alteration, Currency.Augmentation, Currency.Regal, Currency.Scouring, Currency.Transmutation })
            {
                if (!coords.TryGetValue(key, out var c) || !IsValid(c))
                    return $"{Currency.Label(key)}坐标";
            }
            if (useExalt && (!coords.TryGetValue(Currency.Exalted, out var ex) || !IsValid(ex)))
                return "崇高石坐标";
        }

        if (!coords.TryGetValue("item", out var item) || !IsValid(item))
            return "装备坐标";
        return null;
    }

    // ── 常驻循环（Python _run）──

    private async Task Loop()
    {
        while (true)
        {
            _wakeSignal.Wait();
            _wakeSignal.Reset();
            if (_shutdownRequested) break;
            if (!_running) continue;

            var myRunId = _runId;
            Diag.Log($"[引擎] Loop: 开始运行, runId={myRunId}");
            try
            {
                _workCts = new CancellationTokenSource();

                // 诊断：检查 EmergencyCts 状态
                var emergCancelled = _host.EmergencyCts.IsCancellationRequested;
                if (emergCancelled)
                    Diag.Log("[引擎] Loop: ⚠️ EmergencyCts 已取消！linked CTS 将立即取消");

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _workCts.Token, _host.EmergencyCts.Token);

                Diag.Log($"[引擎] Loop: linked CTS 创建完成, linked.IsCancellationRequested={linked.IsCancellationRequested}");

                // 启动停止键轮询任务（对齐 Python _check_stop_key 兜底机制）
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                var pollTask = Task.Run(() => PollStopKeyAsync(pollCts.Token));

                await _host.Input.ReleaseAllKeysAsync(linked.Token); // 释放键盘修饰键（对齐 Python _release_all）
                Diag.Log("[引擎] Loop: ReleaseAllKeys 完成，进入 DoCrafting");
                await DoCraftingAsync(linked.Token);
                Diag.Log("[引擎] Loop: DoCrafting 正常结束");

                pollCts.Cancel(); // 停止轮询
                try { await pollTask; } catch { }
            }
            catch (OperationCanceledException ex)
            {
                // 诊断：区分取消来源
                var workCancelled = _workCts?.IsCancellationRequested ?? false;
                var emergCancelled2 = _host.EmergencyCts.IsCancellationRequested;
                Diag.Log($"[引擎] Loop: OperationCanceledException, runId={myRunId}, " +
                         $"_workCts.Cancelled={workCancelled}, EmergencyCts.Cancelled={emergCancelled2}, " +
                         $"message={ex.Message}");
            }
            catch (Exception ex)
            {
                // 先停线程再发错误信号（坑 #17）
                Diag.Log($"[引擎] Loop: 异常 runId={myRunId}: {ex}");
                lock (this)
                {
                    if (_runId == myRunId) { _running = false; _wakeSignal.Set(); }
                }
                _completionReason = $"运行出错: {ex.Message}";
                ErrorOccurred?.Invoke($"异常: {ex.Message}");
            }
            finally
            {
                // 兜底释放：任何停止路径（含取消/异常）都必须释放键盘修饰键，
                // 用 CancellationToken.None 保证释放不被取消中断（否则 Shift 残留）。
                // 不释放鼠标按键（对齐 Python _release_all：只释放 shift/ctrl/alt）。
                try { await _host.Input.ReleaseAllKeysAsync(CancellationToken.None); } catch { }
                Diag.Log($"[引擎] Loop: finally 兜底释放完成, runId={myRunId}");

                lock (this)
                {
                    if (_runId == myRunId)
                    {
                        // 只清理本次运行（审查 E：旧运行 finally 不得 Dispose 新运行的 _workCts）
                        _running = false;
                        _wakeSignal.Set();
                        _workCts?.Dispose();
                        _workCts = null;
                    }
                }
                if (_runId == myRunId)
                {
                    var reason = _completionReason ?? "已停止";
                    Diag.Log($"[引擎] Loop: Stopped 事件触发, runId={myRunId}, reason={reason}");
                    Stopped?.Invoke(reason);
                }
            }
        }
    }

    /// <summary>
    /// 停止键轮询任务（对齐 Python _check_stop_key）。
    /// 每 50ms 检查一次 GetAsyncKeyState，如果停止键被物理按下则调用 Stop()。
    /// 解决：RegisterHotKey 在游戏前台被吞掉导致 F6 失效的问题。
    /// </summary>
    private async Task PollStopKeyAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(50, token);
                if (_running && IsStopKeyPhysicallyPressed())
                {
                    Diag.Log("[引擎] PollStopKey: 检测到停止键物理按下，触发 Stop");
                    Stop();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DoCraftingAsync(CancellationToken token)
    {
        switch (_rules.Mode)
        {
            case CraftMode.Single:
                await Mode1Async(token);
                break;
            case CraftMode.AltAug:
                if (_mode2ScourAlch) await Mode2ScourAlchAsync(token);
                else await Mode2Async(token);
                break;
            case CraftMode.AltAugRegal:
                await Mode3Async(token);
                break;
        }
    }

    // ── 低层操作（对齐 auto_operator.py）──

    private async Task AwaitForegroundAsync(CancellationToken token)
    {
        if (_host.Foreground.IsTargetForeground()) return;
        ReportStatus($"⏳ 等待切换至游戏窗口（{_host.Foreground.TargetProcess}）...");
        var wait = await _host.Input.WaitForTargetForegroundAsync(token);
        if (!wait.ShiftWasHeld) return;

        if (_activeCurrency is null)
            throw new InputSimulationException("返回游戏后无法确定当前通货，已停止以避免空点");

        ReportStatus($"已返回游戏，重新选择{Currency.Label(_activeCurrency)}...");
        Diag.Log($"[引擎] 前台恢复: 重新选择 {_activeCurrency} " +
                 $"({_activeCurrencyCoordinate.X},{_activeCurrencyCoordinate.Y}) 并恢复 Shift");
        await _host.Input.ReleaseAllKeysAsync(token);
        await _host.Input.RightClickAsync((int)_activeCurrencyCoordinate.X,
            (int)_activeCurrencyCoordinate.Y, 0, token);
        await Task.Delay(200, token);
        await _host.Input.HoldShiftAsync(token);
    }

    /// <summary>
    /// 选择一类通货。所有模式共用同一条安全路径，避免 Ctrl/Alt/Shift 残留令右键失效。
    /// 固定附加 0.2s 等待来自旧版实机经验，确保通货已附着到光标。
    /// </summary>
    private async Task SelectCurrencyAsync(string currency, Point coordinate, CancellationToken token)
    {
        await AwaitForegroundAsync(token);
        await _host.Input.ReleaseAllKeysAsync(token);
        Diag.Log($"[引擎] 选择通货开始: {currency} ({coordinate.X},{coordinate.Y})");
        await _host.Input.RightClickAsync((int)coordinate.X, (int)coordinate.Y, 0, token);
        await Task.Delay(200, token);
        _activeCurrency = currency;
        _activeCurrencyCoordinate = coordinate;
        Diag.Log($"[引擎] 选择通货完成: {currency}");
    }

    private async Task<string?> TryCopyClipboardOnceAsync(int timeoutMs, bool throwOnClearFailure,
        CancellationToken token)
    {
        await AwaitForegroundAsync(token);
        var item = _coordinates["item"];
        await _host.Input.MoveToAsync((int)item.X, (int)item.Y, token);
        if (!ClipboardHelper.TryClear())
        {
            if (throwOnClearFailure)
                throw new InvalidOperationException("无法清空剪贴板，已停止以避免使用旧物品信息");
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        await _host.Input.CtrlAltCAsync(0, token);
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            var text = ClipboardHelper.GetText();
            if (ClipboardHelper.IsItemText(text))
            {
                Diag.Log($"[剪贴板] 获取新物品文本成功: {stopwatch.ElapsedMilliseconds}ms, {text.Length} chars");
                return text;
            }
            await Task.Delay(20, token);
        }

        Diag.Log($"[剪贴板] 本次复制未收到物品文本: {stopwatch.ElapsedMilliseconds}ms");
        return null;
    }

    private async Task<string> CopyClipboardAsync(int _, CancellationToken token)
    {
        const int clipboardTimeoutMs = 800;
        var text = await TryCopyClipboardOnceAsync(clipboardTimeoutMs, throwOnClearFailure: true, token);
        if (text is not null) return text;

        Diag.Log($"[剪贴板] 获取新物品文本超时: {clipboardTimeoutMs}ms");
        throw new InvalidOperationException("复制物品信息超时；请确认鼠标位于装备上且游戏支持 Ctrl+Alt+C");
    }

    /// <summary>启动基线允许重试复制，避免一次丢失的 Ctrl+Alt+C 直接终止任务。</summary>
    private async Task<string> CopyBaselineWithRetryAsync(string context, CancellationToken token)
    {
        var timeoutMs = CraftStateSync.CalculateChangeTimeoutMs(_delayMs);
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            attempts++;
            var text = await TryCopyClipboardOnceAsync(
                CraftStateSync.CopyAttemptTimeoutMs, throwOnClearFailure: false, token);
            if (text is not null)
            {
                Diag.Log($"[同步] {context} 启动基线已建立: total={stopwatch.ElapsedMilliseconds}ms, " +
                         $"attempts={attempts}, {text.Length} chars");
                return text;
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs) break;
            await Task.Delay(CraftStateSync.RetryBackoffMs, token);
        }

        Diag.Log($"[同步] {context} 启动基线复制超时: total={stopwatch.ElapsedMilliseconds}ms, attempts={attempts}");
        throw new InvalidOperationException(
            $"启动时连续复制物品信息失败（{timeoutMs}ms）；已安全停止，请确认鼠标位于装备上");
    }

    /// <summary>
    /// Mode 1 点击后的状态门禁：只有复制到与点击前不同的完整物品文本才允许进入词缀判定。
    /// 剪贴板未写入或仍为旧状态时只重发 Ctrl+Alt+C，绝不再次点击通货。
    /// </summary>
    private async Task<string> WaitForChangedMode1ItemAsync(string baseline, CancellationToken token)
    {
        var timeoutMs = CraftStateSync.CalculateChangeTimeoutMs(_delayMs);
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        var unchangedCopies = 0;
        var emptyCopies = 0;

        // delay 表示通货点击后留给游戏/服务器刷新物品的首次同步窗口。
        if (_delayMs > 0)
            await Task.Delay(_delayMs, token);

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            attempts++;

            var candidate = await TryCopyClipboardOnceAsync(
                CraftStateSync.CopyAttemptTimeoutMs, throwOnClearFailure: false, token);
            if (candidate is not null)
            {
                if (CraftStateSync.HasItemStateChanged(baseline, candidate))
                {
                    Diag.Log($"[同步] Mode1 物品状态已变化: total={stopwatch.ElapsedMilliseconds}ms, " +
                             $"attempts={attempts}, unchanged={unchangedCopies}, empty={emptyCopies}");
                    return candidate;
                }

                unchangedCopies++;
                Diag.Log($"[同步] Mode1 仍为点击前状态: total={stopwatch.ElapsedMilliseconds}ms, attempt={attempts}");
                if (unchangedCopies >= Math.Max(1, _exhaustionThreshold))
                {
                    Diag.Log($"[同步] Mode1 达到未变确认阈值: {unchangedCopies}");
                    break;
                }
            }
            else
            {
                emptyCopies++;
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs) break;
            await Task.Delay(CraftStateSync.RetryBackoffMs, token);
        }

        Diag.Log($"[同步] Mode1 等待状态变化超时: total={stopwatch.ElapsedMilliseconds}ms, " +
                 $"attempts={attempts}, unchanged={unchangedCopies}, empty={emptyCopies}");
        if (unchangedCopies > 0)
            throw new InvalidOperationException(
                $"通货点击后物品信息在 {timeoutMs}ms 内未变化；可能通货已耗尽或游戏尚未响应，已安全停止");

        throw new InvalidOperationException(
            $"通货点击后连续复制物品信息失败（{timeoutMs}ms）；已安全停止，请确认鼠标位于装备上");
    }

    /// <summary>
    /// Mode 2/3 操作后门禁：重试复制直到观察到该通货应有的稀有度/词缀数转换。
    /// 未观察到预期转换时绝不发送下一次鼠标点击。
    /// </summary>
    private async Task<string> WaitForExpectedTransitionAsync(string baseline,
        CraftCurrencyOperation operation, string operationLabel, CancellationToken token)
    {
        var timeoutMs = CraftStateSync.CalculateChangeTimeoutMs(_delayMs);
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        var rejectedCopies = 0;
        var emptyCopies = 0;
        var beforeParsed = AffixEngine.ParseItem(baseline);
        var before = new CraftItemState(beforeParsed.Rarity, beforeParsed.ExplicitAffixCount);
        var lastReason = "未收到有效物品文本";

        if (_delayMs > 0)
            await Task.Delay(_delayMs, token);

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            token.ThrowIfCancellationRequested();
            attempts++;
            var candidate = await TryCopyClipboardOnceAsync(
                CraftStateSync.CopyAttemptTimeoutMs, throwOnClearFailure: false, token);

            if (candidate is null)
            {
                emptyCopies++;
            }
            else
            {
                var afterParsed = AffixEngine.ParseItem(candidate);
                var after = new CraftItemState(afterParsed.Rarity, afterParsed.ExplicitAffixCount);
                var changed = CraftStateSync.HasItemStateChanged(baseline, candidate);
                var check = CraftStateSync.CheckTransition(operation, before, after, changed);
                if (check.Accepted)
                {
                    Diag.Log($"[同步] {operationLabel} 状态转换已确认: " +
                             $"{before.Rarity}/{before.ExplicitAffixCount}→{after.Rarity}/{after.ExplicitAffixCount}, " +
                             $"total={stopwatch.ElapsedMilliseconds}ms, attempts={attempts}, " +
                             $"rejected={rejectedCopies}, empty={emptyCopies}");
                    return candidate;
                }

                rejectedCopies++;
                lastReason = check.Reason;
                Diag.Log($"[同步] {operationLabel} 尚未观察到预期转换: " +
                         $"total={stopwatch.ElapsedMilliseconds}ms, attempt={attempts}, reason={lastReason}");
                if (rejectedCopies >= Math.Max(1, _exhaustionThreshold)) break;
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs) break;
            await Task.Delay(CraftStateSync.RetryBackoffMs, token);
        }

        Diag.Log($"[同步] {operationLabel} 状态转换确认失败: total={stopwatch.ElapsedMilliseconds}ms, " +
                 $"attempts={attempts}, rejected={rejectedCopies}, empty={emptyCopies}, reason={lastReason}");
        throw new InvalidOperationException(
            $"{operationLabel}后未观察到预期物品变化（{lastReason}）；已安全停止，未继续使用其他通货");
    }

    private (AffixCheckResult Result, int AffixCount) CheckItem(string text)
    {
        var parsed = AffixEngine.ParseItem(text);
        var result = AffixEngine.CheckAffixes(parsed, _rules);
        if (result.HasAnyHit || result.HasExclude)
        {
            Diag.Log($"[词缀] rarity={parsed.Rarity}, affixes={parsed.ExplicitAffixCount}, " +
                     $"primary={result.PrimaryHits}[{string.Join(',', result.MatchedPrimaryRules)}], " +
                     $"secondary={result.SecondaryHits}[{string.Join(',', result.MatchedSecondaryRules)}], " +
                     $"exclude={result.HasExclude}[{string.Join(',', result.MatchedExcludeRules)}]");
        }
        return (result, parsed.ExplicitAffixCount);
    }

    private void ReportStatus(string text, bool force = false)
    {
        var now = DateTime.Now;
        if (!force && now - _lastStatusTime < TimeSpan.FromMilliseconds(250)) return; // 250ms 节流
        _lastStatusTime = now;
        StatusUpdated?.Invoke(new CraftStatus(text, UseCount, MatchCount, _running));
    }

    private void OnSuccess()
    {
        _completionReason = $"匹配成功! 共{UseCount}次";
        Diag.Log($"[引擎] OnSuccess: 匹配成功! 共{UseCount}次, soundEnabled={_soundEnabled}, selectedSound={_selectedSound}");
        ReportStatus($"匹配成功! 共{UseCount}次", force: true);
        MatchFound?.Invoke();
        if (_soundEnabled) _host.Sound.Play(_selectedSound);
        if (_popupEnabled) _host.Notification.Show($"匹配成功!\n使用次数: {UseCount}");
    }

    // ── Mode 1：单通货（Python _mode1）──

    private async Task Mode1Async(CancellationToken token)
    {
        var currCoord = _coordinates[_rules.SingleCurrency];
        var itemCoord = _coordinates["item"];

        ReportStatus("启动单通货模式...", force: true);
        Diag.Log($"[引擎] Mode1: 开始, 通货={_rules.SingleCurrency} ({currCoord.X},{currCoord.Y}), 物品 ({itemCoord.X},{itemCoord.Y})");

        await SelectCurrencyAsync(_rules.SingleCurrency, currCoord, token);
        await _host.Input.HoldShiftAsync(token);
        Diag.Log("[引擎] Mode1: HoldShift 完成，复制启动基线（不判定命中）");

        // 启动时的装备可能已命中，但 Mode 1 的产品语义是 F5 后仍先使用一次通货。
        // 因此此处只建立点击前基线，不调用 CheckItem。
        var baseline = await CopyBaselineWithRetryAsync("Mode1", token);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await AwaitForegroundAsync(token);
                // Mode 1 的 delay 已用于点击后的服务器同步窗口，此处不再叠加点击前等待。
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                UseCount++;

                var text = await WaitForChangedMode1ItemAsync(baseline, token);

                var (result, _) = CheckItem(text);
                MatchCount = result.TotalHits;
                var status = $"#{UseCount} 主:{result.PrimaryHits} 次:{result.SecondaryHits}";
                if (result.HasExclude) status += " [排除]";
                ReportStatus(status);

                if (result.MeetsFinalRules(_rules))
                {
                    OnSuccess();
                    return;
                }

                baseline = text;
            }
        }
        finally
        {
            try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
        }
    }

    // ── Mode 2：改造+增幅（Python _mode2）──

    private async Task Mode2Async(CancellationToken token)
    {
        var altCoord = _coordinates[Currency.Alteration];
        var augCoord = _coordinates[Currency.Augmentation];
        var itemCoord = _coordinates["item"];

        ReportStatus("启动改造+增幅模式...", force: true);

        // 启动状态只作为第一次改造的基线，不判定命中。
        var text = await CopyBaselineWithRetryAsync("Mode2", token);
        if (text.Contains("已污染", StringComparison.Ordinal) ||
            text.Contains("Corrupted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mode 2 不支持已污染物品，未执行通货操作");

        var initialItem = AffixEngine.ParseItem(text);
        if (initialItem.Rarity != ItemRarity.Magic)
            throw new InvalidOperationException(
                $"Mode 2 改造+增幅要求启动物品为魔法稀有度，当前为 {initialItem.Rarity}，未执行通货操作");

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await AwaitForegroundAsync(token);

            // Phase 1: 改造（右键一次，shift+click 循环）
            await SelectCurrencyAsync(Currency.Alteration, altCoord, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    await AwaitForegroundAsync(token);
                    await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                    UseCount++;

                    text = await WaitForExpectedTransitionAsync(text,
                        CraftCurrencyOperation.Alteration, "改造石", token);
                    var (result, _) = CheckItem(text);
                    MatchCount = result.TotalHits;

                    if (result.HasExclude)
                    {
                        ReportStatus($"#{UseCount} 排除命中，继续改造...");
                        continue;
                    }
                    if (result.HasAnyHit)
                        break; // 改造阶段：至少命中 1 条（主或次）

                    ReportStatus($"#{UseCount} 改造中...");
                }
            }
            finally
            {
                try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
            }

            // 检查词缀数：≥2 则跳过增幅直接终检
            var (lastResult, affixCount) = CheckItem(text);
            if (affixCount >= 2)
            {
                if (lastResult.MeetsFinalRules(_rules))
                {
                    OnSuccess();
                    return;
                }
                ReportStatus($"#{UseCount} 2条词缀不满足，重新改造...");
                continue;
            }

            // Phase 2: 增幅（仅在恰好 1 词缀时使用，铁律 #4）
            await SelectCurrencyAsync(Currency.Augmentation, augCoord, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                UseCount++;

                text = await WaitForExpectedTransitionAsync(text,
                    CraftCurrencyOperation.Augmentation, "增幅石", token);
                var (result, _) = CheckItem(text);
                MatchCount = result.TotalHits;

                if (result.MeetsFinalRules(_rules))
                {
                    OnSuccess();
                    return;
                }
            }
            finally
            {
                try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
            }

            ReportStatus($"#{UseCount} 增幅后不满足，重新改造...");
        }
    }

    // ── Mode 2 子模式：重铸+点金（Python _mode2_scour_alch）──

    private async Task Mode2ScourAlchAsync(CancellationToken token)
    {
        var alchCoord = _coordinates[Currency.Alchemy];
        var scourCoord = _coordinates[Currency.Scouring];
        var itemCoord = _coordinates["item"];

        ReportStatus("启动重铸+点金模式...", force: true);

        var text = await CopyBaselineWithRetryAsync("Mode2重铸点金", token);
        if (text.Contains("已污染", StringComparison.Ordinal) ||
            text.Contains("Corrupted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("重铸+点金不支持已污染物品，未执行通货操作");

        var currentItem = AffixEngine.ParseItem(text);
        if (currentItem.Rarity is not (ItemRarity.Normal or ItemRarity.Magic or ItemRarity.Rare))
            throw new InvalidOperationException(
                $"重铸+点金无法处理当前物品稀有度（{currentItem.Rarity}），未执行通货操作");

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await AwaitForegroundAsync(token);

            // 非普通物品先重铸；已是普通物品时跳过无效重铸，直接点金。
            if (currentItem.Rarity != ItemRarity.Normal)
            {
                ReportStatus($"#{UseCount} 重铸后重新点金...");
                await SelectCurrencyAsync(Currency.Scouring, scourCoord, token);
                await _host.Input.HoldShiftAsync(token);
                try
                {
                    await AwaitForegroundAsync(token);
                    await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                    UseCount++;
                    text = await WaitForExpectedTransitionAsync(text,
                        CraftCurrencyOperation.Scouring, "重铸石", token);
                    currentItem = AffixEngine.ParseItem(text);
                }
                finally
                {
                    try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
                }
            }

            // 点金（白→黄）并检查新结果
            await SelectCurrencyAsync(Currency.Alchemy, alchCoord, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                UseCount++;

                text = await WaitForExpectedTransitionAsync(text,
                    CraftCurrencyOperation.Alchemy, "点金石", token);
                currentItem = AffixEngine.ParseItem(text);
                var (result, _) = CheckItem(text);
                MatchCount = result.TotalHits;

                if (result.MeetsFinalRules(_rules))
                {
                    OnSuccess();
                    return;
                }
            }
            finally
            {
                try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
            }

            ReportStatus($"#{UseCount} 点金后不满足，继续下一轮...");
        }
    }

    // ── Mode 3：改造+增幅+富豪（+可选崇高）（Python _mode3）──

    private async Task Mode3Async(CancellationToken token)
    {
        var altCoord = _coordinates[Currency.Alteration];
        var augCoord = _coordinates[Currency.Augmentation];
        var regalCoord = _coordinates[Currency.Regal];
        var scourCoord = _coordinates[Currency.Scouring];
        var transCoord = _coordinates[Currency.Transmutation];
        var exaltCoord = _coordinates.TryGetValue(Currency.Exalted, out var e) ? e : default;
        var itemCoord = _coordinates["item"];

        ReportStatus("启动改造+增幅+富豪模式...", force: true);
        // 阈值 = 主+次命中数 - 1（富豪补 1，铁律 #6；方案 D4）
        var threshold = Math.Max(0, _rules.PrimaryHitCount + _rules.SecondaryHitCount - 1);

        // 启动时先读取实际装备状态，避免对蓝/黄装直接使用蜕变后继续读取旧状态。
        ReportStatus("检查 Mode 3 装备初始状态...", force: true);
        var initialText = await CopyBaselineWithRetryAsync("Mode3", token);
        if (initialText.Contains("已污染", StringComparison.Ordinal) ||
            initialText.Contains("Corrupted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mode 3 不支持已污染物品，未执行任何通货操作");

        var initialItem = AffixEngine.ParseItem(initialText);
        var startDecision = CraftDecisions.BeforeMode3(initialItem.Rarity);
        Diag.Log($"[引擎] Mode3 初始预检: rarity={initialItem.Rarity}, decision={startDecision}, " +
                 $"affixes={initialItem.ExplicitAffixCount}");
        if (startDecision == Mode3StartDecision.StopUnsupported)
            throw new InvalidOperationException($"Mode 3 无法处理当前物品稀有度（{initialItem.Rarity}），未执行通货操作");

        var currentText = initialText;

        if (startDecision == Mode3StartDecision.ScourFirst)
        {
            ReportStatus("当前为魔法/稀有物品，先重铸为普通物品...", force: true);
            await SelectCurrencyAsync(Currency.Scouring, scourCoord, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                UseCount++;
                currentText = await WaitForExpectedTransitionAsync(currentText,
                    CraftCurrencyOperation.Scouring, "重铸石（启动预处理）", token);
            }
            finally
            {
                try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
            }
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await AwaitForegroundAsync(token);

            // Phase 1: 蜕变（白→蓝）
            await SelectCurrencyAsync(Currency.Transmutation, transCoord, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                UseCount++;
                currentText = await WaitForExpectedTransitionAsync(currentText,
                    CraftCurrencyOperation.Transmutation, "蜕变石", token);
            }
            finally
            {
                try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
            }

            // ===== 魔法阶段内层循环（改造 + 可选增幅）=====
            var goToRegal = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await AwaitForegroundAsync(token);

                // Phase 2: 改造（右键一次，shift+click 循环）
                await SelectCurrencyAsync(Currency.Alteration, altCoord, token);
                await _host.Input.HoldShiftAsync(token);
                string text;
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        await AwaitForegroundAsync(token);
                        await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                        UseCount++;

                        text = await WaitForExpectedTransitionAsync(currentText,
                            CraftCurrencyOperation.Alteration, "改造石", token);
                        currentText = text;
                        var (result, affixCount) = CheckItem(text);
                        MatchCount = result.TotalHits;

                        if (result.HasExclude)
                        {
                            ReportStatus($"#{UseCount} 排除命中，继续改造...");
                            continue;
                        }

                        var hits = result.TotalHits;
                        var decision = CraftDecisions.AfterAlteration(affixCount, hits, threshold);
                        if (CraftDecisions.ShouldCaptureMode3Miss(affixCount, hits, threshold, result.HasExclude))
                        {
                            Mode3DiagnosticRecorder.Capture(
                                text,
                                _rules,
                                result,
                                affixCount,
                                threshold,
                                _runId,
                                UseCount);
                        }

                        switch (decision)
                        {
                            case Mode3MagicDecision.ProceedToRegal:
                                goToRegal = true;
                                break; // 2 词缀达标 → 去富豪
                            case Mode3MagicDecision.UseAugmentation:
                                break; // 1 词缀命中 → 去增幅
                            default:
                                // 2 词缀未达标 → 继续 shift+click 重 roll（不释放 Shift，铁律 #5/方案 D4）
                                ReportStatus(affixCount >= 2
                                    ? $"#{UseCount} 2词缀命中{hits}不足{threshold}，继续改造..."
                                    : $"#{UseCount} 改造中...");
                                continue;
                        }
                        break;
                    }
                }
                finally
                {
                    try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
                }

                if (goToRegal)
                    break; // 退出魔法阶段 → 富豪

                // 只有 1 词缀且命中 ≥1 → 增幅
                await SelectCurrencyAsync(Currency.Augmentation, augCoord, token);
                await _host.Input.HoldShiftAsync(token);
                try
                {
                    await AwaitForegroundAsync(token);
                    await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                    UseCount++;

                    text = await WaitForExpectedTransitionAsync(currentText,
                        CraftCurrencyOperation.Augmentation, "增幅石", token);
                    currentText = text;
                    var (result, _) = CheckItem(text);
                    MatchCount = result.TotalHits;
                    var hits = result.TotalHits;

                    if (CraftDecisions.AfterAugmentation(hits, threshold) == Mode3MagicDecision.ProceedToRegal)
                        break; // 增幅后达标 → 去富豪
                    ReportStatus($"#{UseCount} 增幅后命中{hits}不足{threshold}，继续改造...");
                    continue; // 继续改造（不重铸，方案 D4）
                }
                finally
                {
                    try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
                }
            }

            // ===== Phase 4: 富豪（蓝→黄）=====
            await SelectCurrencyAsync(Currency.Regal, regalCoord, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                UseCount++;

                currentText = await WaitForExpectedTransitionAsync(currentText,
                    CraftCurrencyOperation.Regal, "富豪石", token);
                var text = currentText;
                var (result, _) = CheckItem(text);
                MatchCount = result.TotalHits;

                if (result.MeetsFinalRules(_rules))
                {
                    OnSuccess();
                    return;
                }
            }
            finally
            {
                try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
            }

            // ===== Phase 4.5: 崇高（可选）=====
            if (_useExalt)
            {
                await SelectCurrencyAsync(Currency.Exalted, exaltCoord, token);
                await _host.Input.HoldShiftAsync(token);
                try
                {
                    await AwaitForegroundAsync(token);
                    await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                    UseCount++;

                    currentText = await WaitForExpectedTransitionAsync(currentText,
                        CraftCurrencyOperation.Exalted, "崇高石", token);
                    var text = currentText;
                    var (result, _) = CheckItem(text);
                    MatchCount = result.TotalHits;

                    if (result.MeetsFinalRules(_rules))
                    {
                        OnSuccess();
                        return;
                    }
                }
                finally
                {
                    try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
                }
            }

            // ===== Phase 5: 重铸（黄→白）→ 回外层循环（蜕变）=====
            ReportStatus($"#{UseCount} 富豪失败，重铸重来...");
            await SelectCurrencyAsync(Currency.Scouring, scourCoord, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, 0, token);
                UseCount++;
                currentText = await WaitForExpectedTransitionAsync(currentText,
                    CraftCurrencyOperation.Scouring, "重铸石", token);
            }
            finally
            {
                try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
            }
        }
    }
}
