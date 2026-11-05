using System.Diagnostics;
using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Clicker;

public enum ClickerMouseButton
{
    Left,
    Right,
}

public sealed record ClickerStatus(bool Running, int ClickCount, string Text);

/// <summary>
/// 连点器常驻任务：初始化时创建一次，空闲零 CPU；每次运行使用独立 CTS。
/// 实际输入仍通过宿主 InputSimulator，循环额外在每次点击前检查游戏前台。
/// </summary>
public sealed class ClickerEngine
{
    private readonly ToolHost _host;
    private readonly Func<bool> _isTargetForeground;
    private readonly Func<ClickerMouseButton, CancellationToken, Task> _clickAction;
    private readonly Action _releaseMouseButtons;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wakeSignal = new(0);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _worker;

    private CancellationTokenSource? _runCts;
    private CancellationToken _emergencyToken;
    private bool _workerOwnsRun;
    private bool _running;
    private bool _shutdownRequested;
    private int _runId;
    private int _clickCount;
    private ClickerMouseButton _button;
    private int _intervalMs = SettingsDefaults.ClickerIntervalMs;
    private long _lastStatusTimestamp;

    public event Action<ClickerStatus>? StatusUpdated;
    public event Action<string>? ErrorOccurred;

    public ClickerEngine(ToolHost host)
        : this(host, host.Foreground.IsTargetForeground,
            (button, token) => button == ClickerMouseButton.Left
                ? host.Input.ClickAsync(0, token)
                : host.Input.RightClickAsync(0, token),
            host.Input.ReleaseMouseButtons)
    {
    }

    internal ClickerEngine(
        ToolHost host,
        Func<bool> isTargetForeground,
        Func<ClickerMouseButton, CancellationToken, Task> clickAction,
        Action? releaseMouseButtons = null)
    {
        _host = host;
        _isTargetForeground = isTargetForeground;
        _clickAction = clickAction;
        _releaseMouseButtons = releaseMouseButtons ?? (() => { });
        _worker = Task.Run(RunWorkerAsync);
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public int ClickCount
    {
        get { lock (_gate) return _clickCount; }
    }

    public bool Toggle(ClickerMouseButton button, int intervalMs)
    {
        if (IsRunning)
        {
            Stop("连点器已停止");
            return false;
        }
        return Start(button, intervalMs);
    }

    public bool Start(ClickerMouseButton button, int intervalMs)
    {
        int runId;
        lock (_gate)
        {
            if (_running || _shutdownRequested) return false;
            if (_host.EmergencyCts.IsCancellationRequested)
                _host.ResetEmergencyStop();

            _button = button;
            _intervalMs = Math.Clamp(intervalMs, 10, 200);
            _clickCount = 0;
            runId = ++_runId;
            _emergencyToken = _host.EmergencyCts.Token;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdownCts.Token, _emergencyToken);
            _workerOwnsRun = false;
            _running = true;
            _lastStatusTimestamp = 0;
            _wakeSignal.Release();
        }

        ReportStatus("连点中...", running: true, force: true, runId: runId);
        Diag.Log($"[连点] 启动: button={button}, interval={Math.Clamp(intervalMs, 10, 200)}ms");
        return true;
    }

    public bool Stop(string reason = "连点器已停止")
    {
        CancellationTokenSource? runCts;
        bool dispose;
        int runId;
        lock (_gate)
        {
            if (!_running) return false;
            _running = false;
            runId = _runId;
            runCts = _runCts;
            dispose = !_workerOwnsRun;
            if (dispose) _runCts = null;
        }

        CancelRun(runCts, dispose);
        ReportStatus(reason, running: false, force: true, runId: runId);
        Diag.Log($"[连点] 停止: reason={reason}, count={ClickCount}");
        return true;
    }

    public bool Shutdown(TimeSpan timeout)
    {
        CancellationTokenSource? runCts;
        bool dispose;
        lock (_gate)
        {
            if (_shutdownRequested) return _worker.IsCompleted;
            _shutdownRequested = true;
            _running = false;
            runCts = _runCts;
            dispose = !_workerOwnsRun;
            if (dispose) _runCts = null;
        }
        CancelRun(runCts, dispose);
        _shutdownCts.Cancel();
        try { return _worker.Wait(timeout); }
        catch (AggregateException) { return _worker.IsCompleted; }
    }

    private async Task RunWorkerAsync()
    {
        while (true)
        {
            try
            {
                await _wakeSignal.WaitAsync(_shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            int runId;
            int intervalMs;
            ClickerMouseButton button;
            CancellationTokenSource? runCts;
            CancellationToken emergencyToken;
            lock (_gate)
            {
                if (_shutdownRequested) return;
                if (!_running || _runCts is null) continue;
                runId = _runId;
                intervalMs = _intervalMs;
                button = _button;
                runCts = _runCts;
                emergencyToken = _emergencyToken;
                _workerOwnsRun = true;
            }

            string? error = null;
            try
            {
                while (!runCts.IsCancellationRequested)
                {
                    if (!_isTargetForeground())
                    {
                        await Task.Delay(100, runCts.Token);
                        continue;
                    }

                    await _clickAction(button, runCts.Token);
                    lock (_gate)
                    {
                        if (_runId != runId || !_running) break;
                        _clickCount++;
                    }
                    ReportStatus("连点中...", running: true, runId: runId);
                    await Task.Delay(intervalMs, runCts.Token);
                }
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested)
            {
                // 正常停止、全局紧急停止或退出。
            }
            catch (Exception ex)
            {
                error = $"连点器运行失败：{ex.Message}";
                Diag.Log($"[连点] 运行异常: {ex}");
            }
            finally
            {
                runCts.Dispose();
                lock (_gate)
                {
                    if (_runId == runId)
                    {
                        var reportStop = _running;
                        _running = false;
                        if (ReferenceEquals(_runCts, runCts))
                        {
                            _runCts = null;
                            _workerOwnsRun = false;
                        }

                        // 检查、鼠标释放与发布在同一锁内，不能被新 Start 插入。
                        try { _releaseMouseButtons(); }
                        catch (Exception ex) { Diag.Log($"[连点] 释放鼠标按键失败: {ex.Message}"); }

                        if (reportStop)
                        {
                            var reason = error ?? (emergencyToken.IsCancellationRequested
                                ? "连点器已紧急停止"
                                : "连点器已停止");
                            ReportStatus(reason, running: false, force: true, runId: runId);
                            if (error is not null && _runId == runId) ErrorOccurred?.Invoke(error);
                        }
                    }
                }
            }
        }
    }

    // worker 可能在停止方取得源的快照后完成并释放；已结束的源无需再次取消。
    internal static void CancelRun(CancellationTokenSource? source, bool dispose)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { if (dispose) source.Dispose(); }
    }

    private void ReportStatus(string text, bool running, bool force = false, int? runId = null)
    {
        lock (_gate)
        {
            if (runId is { } expected && expected != _runId) return;
            if (_running != running) return;
            var now = Stopwatch.GetTimestamp();
            if (!force && _lastStatusTimestamp != 0 &&
                Stopwatch.GetElapsedTime(_lastStatusTimestamp, now) < TimeSpan.FromMilliseconds(250))
                return;
            _lastStatusTimestamp = now;
            var status = new ClickerStatus(_running, _clickCount, text);
            StatusUpdated?.Invoke(status);
        }
    }
}
