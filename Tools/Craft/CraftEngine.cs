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
/// - 时序细节逐行对齐源码：右键后 Mode1 不额外等待 / Mode2/3 额外 0.15s；ShiftClick 拆分点击；CtrlAltC 后 max(delay×5,0.15)
/// - 紧急停止：光标 ≤(1,1) → InputSimulator 触发 EmergencyCts.Cancel（对齐 Python FAILSAFE）
/// - 状态推送 250ms 节流；错误先停线程再上报（坑 #17）
/// </summary>
public sealed class CraftEngine
{
    private readonly ToolHost _host;
    private readonly ManualResetEventSlim _wakeSignal = new(false);
    private CancellationTokenSource? _workCts;
    private bool _running;
    private Task? _thread;
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
    private ushort _stopKeyVk; // F6 停止键的虚拟键码（轮询兜底用）

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

            _rules = rules;
            _coordinates = coordinates;
            _delayMs = delayMs;
            _soundEnabled = soundEnabled;
            _popupEnabled = popupEnabled;
            _selectedSound = selectedSound;
            _exhaustionThreshold = exhaustionThreshold;
            _mode2ScourAlch = mode2ScourAlch;
            _useExalt = useExalt;
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

    /// <summary>设置停止键 VK 码（CraftTool 启动前调用，用于 GetAsyncKeyState 轮询兜底）。</summary>
    public void SetStopKey(string key) => _stopKeyVk = KeyCode.Parse(key);

    /// <summary>
    /// 轮询停止键物理状态（对齐 Python _check_stop_key）。
    /// RegisterHotKey 在游戏前台可能被吞掉，此方法作为兜底。
    /// </summary>
    private bool IsStopKeyPhysicallyPressed()
    {
        if (_stopKeyVk == 0) return false;
        return (GetAsyncKeyState(_stopKeyVk) & 0x8000) != 0;
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
                    Diag.Log($"[引擎] Loop: Stopped 事件触发, runId={myRunId}");
                    Stopped?.Invoke("已停止");
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
        // Python _interruptible_sleep：非目标进程时 0.2s 轮询等待
        if (string.IsNullOrEmpty(_host.Foreground.TargetProcess)) return;
        while (!_host.Foreground.IsTargetForeground())
        {
            // 节流提示：让用户知道引擎在等什么（修复"启动后无响应"困惑）
            ReportStatus($"⏳ 等待切换至游戏窗口（{_host.Foreground.TargetProcess}）...");
            await Task.Delay(200, token);
        }
    }

    private async Task<string> CopyClipboardAsync(int delayMs, CancellationToken token)
    {
        await AwaitForegroundAsync(token);
        await _host.Input.CtrlAltCAsync(delayMs, token);
        return ClipboardHelper.GetText();
    }

    private (AffixCheckResult Result, int AffixCount) CheckItem(string text)
    {
        var lines = AffixEngine.ParseItemText(text);
        return (AffixEngine.CheckAffixes(lines, _rules), lines.Count);
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

        await _host.Input.RightClickAsync((int)currCoord.X, (int)currCoord.Y, _delayMs, token);
        Diag.Log("[引擎] Mode1: 通货右键完成");
        await _host.Input.HoldShiftAsync(token);
        Diag.Log("[引擎] Mode1: HoldShift 完成，进入循环");

        var lastClip = "";
        var clipSameCount = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, _delayMs, token);
                UseCount++;

                var text = await CopyClipboardAsync(_delayMs, token);

                // 对齐 Python _mode1：剪贴板为空 → 停止（物品不在光标处/剪贴板被占用）
                if (text.Length == 0)
                {
                    Diag.Log("[引擎] Mode1: 剪贴板为空，停止");
                    ReportStatus("剪贴板为空，停止", force: true);
                    if (_soundEnabled) _host.Sound.Play(_selectedSound);
                    if (_popupEnabled) _host.Notification.Show("剪贴板为空，已停止");
                    return;
                }

                // 耗尽检测：连续 N 次剪贴板相同（Python 逻辑）
                if (text.Length > 0 && text == lastClip)
                {
                    clipSameCount++;
                    if (clipSameCount >= _exhaustionThreshold)
                    {
                        Diag.Log($"[引擎] Mode1: 通货耗尽 (连续{clipSameCount}次未变), soundEnabled={_soundEnabled}");
                        ReportStatus($"通货可能已耗尽 (连续{clipSameCount}次未变)", force: true);
                        if (_soundEnabled) _host.Sound.Play(_selectedSound);
                        if (_popupEnabled) _host.Notification.Show($"通货可能已耗尽\n连续{clipSameCount}次未变");
                        return;
                    }
                }
                else
                {
                    clipSameCount = 0;
                    lastClip = text;
                }

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

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await AwaitForegroundAsync(token);

            // Phase 1: 改造（右键一次，shift+click 循环）
            await _host.Input.RightClickAsync((int)altCoord.X, (int)altCoord.Y, _delayMs, token);
            await Task.Delay(150, token);
            await _host.Input.HoldShiftAsync(token);
            string text;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    await AwaitForegroundAsync(token);
                    await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, _delayMs, token);
                    UseCount++;

                    text = await CopyClipboardAsync(_delayMs, token);
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
            await _host.Input.RightClickAsync((int)augCoord.X, (int)augCoord.Y, _delayMs, token);
            await Task.Delay(150, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, _delayMs, token);
                UseCount++;

                text = await CopyClipboardAsync(_delayMs, token);
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

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await AwaitForegroundAsync(token);

            // 点金（白→黄）
            await _host.Input.RightClickAsync((int)alchCoord.X, (int)alchCoord.Y, _delayMs, token);
            await Task.Delay(150, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, _delayMs, token);
                UseCount++;

                var text = await CopyClipboardAsync(_delayMs, token);
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

            // 不满足 → 重铸（黄→白）
            ReportStatus($"#{UseCount} 点金后不满足，重铸重来...");
            await _host.Input.RightClickAsync((int)scourCoord.X, (int)scourCoord.Y, _delayMs, token);
            await Task.Delay(150, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, _delayMs, token);
                UseCount++;
            }
            finally
            {
                try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
            }
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

        while (true)
        {
            token.ThrowIfCancellationRequested();
            await AwaitForegroundAsync(token);

            // Phase 1: 蜕变（白→蓝）
            await _host.Input.RightClickAsync((int)transCoord.X, (int)transCoord.Y, _delayMs, token);
            await Task.Delay(150, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, _delayMs, token);
                UseCount++;
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
                await _host.Input.RightClickAsync((int)altCoord.X, (int)altCoord.Y, _delayMs, token);
                await Task.Delay(150, token);
                await _host.Input.HoldShiftAsync(token);
                string text;
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        await AwaitForegroundAsync(token);
                        await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, _delayMs, token);
                        UseCount++;

                        text = await CopyClipboardAsync(_delayMs, token);
                        var (result, affixCount) = CheckItem(text);
                        MatchCount = result.TotalHits;

                        if (result.HasExclude)
                        {
                            ReportStatus($"#{UseCount} 排除命中，继续改造...");
                            continue;
                        }

                        var hits = result.TotalHits;
                        if (affixCount >= 2)
                        {
                            if (hits >= threshold)
                            {
                                goToRegal = true;
                                break; // 2 词缀达标 → 去富豪
                            }
                            // 2 词缀未达标 → 继续 shift+click 重 roll（不释放 Shift，铁律 #5/方案 D4）
                            ReportStatus($"#{UseCount} 2词缀命中{hits}不足{threshold}，继续改造...");
                        }
                        else // 1 词缀
                        {
                            if (hits >= 1)
                                break; // 1 词缀命中 → 去增幅
                            ReportStatus($"#{UseCount} 改造中...");
                        }
                    }
                }
                finally
                {
                    try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
                }

                if (goToRegal)
                    break; // 退出魔法阶段 → 富豪

                // 只有 1 词缀且命中 ≥1 → 增幅
                await _host.Input.RightClickAsync((int)augCoord.X, (int)augCoord.Y, _delayMs, token);
                await Task.Delay(150, token);
                await _host.Input.HoldShiftAsync(token);
                try
                {
                    await AwaitForegroundAsync(token);
                    await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, _delayMs, token);
                    UseCount++;

                    text = await CopyClipboardAsync(_delayMs, token);
                    var (result, _) = CheckItem(text);
                    MatchCount = result.TotalHits;
                    var hits = result.TotalHits;

                    if (result.MeetsFinalRules(_rules))
                    {
                        OnSuccess();
                        return;
                    }

                    if (hits >= threshold)
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
            await _host.Input.RightClickAsync((int)regalCoord.X, (int)regalCoord.Y, _delayMs, token);
            await Task.Delay(150, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, _delayMs, token);
                UseCount++;

                var text = await CopyClipboardAsync(_delayMs, token);
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
                await _host.Input.RightClickAsync((int)exaltCoord.X, (int)exaltCoord.Y, _delayMs, token);
                await Task.Delay(150, token);
                await _host.Input.HoldShiftAsync(token);
                try
                {
                    await AwaitForegroundAsync(token);
                    await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, _delayMs, token);
                    UseCount++;

                    var text = await CopyClipboardAsync(_delayMs, token);
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
            await _host.Input.RightClickAsync((int)scourCoord.X, (int)scourCoord.Y, _delayMs, token);
            await Task.Delay(150, token);
            await _host.Input.HoldShiftAsync(token);
            try
            {
                await AwaitForegroundAsync(token);
                await _host.Input.ShiftClickAsync((int)itemCoord.X, (int)itemCoord.Y, _delayMs, token);
                UseCount++;
            }
            finally
            {
                try { await _host.Input.ReleaseShiftAsync(CancellationToken.None); } catch { }
            }
        }
    }
}
