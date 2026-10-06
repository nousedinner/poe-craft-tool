using System.Diagnostics;
using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.KeyLoop;

public sealed class KeyLoopSlot
{
    public bool Enabled { get; set; }
    public string Key { get; set; } = string.Empty;
    public double DelaySeconds { get; set; } = 1.0;

    public KeyLoopSlot CreateSnapshot() => new()
    {
        Enabled = Enabled,
        Key = Key,
        DelaySeconds = Math.Clamp(DelaySeconds, 0.1, 999.0),
    };
}

public sealed record KeyLoopStatus(bool Running, IReadOnlyList<int> PressCounts, string Text);

/// <summary>10 个常驻槽位任务；空闲等待信号，每次运行使用独立 CTS 和配置快照。</summary>
public sealed class KeyLoopEngine
{
    public const int MaxSlots = 10;

    private sealed class SlotRuntime
    {
        public required int Index { get; init; }
        public SemaphoreSlim WakeSignal { get; } = new(0);
        public CancellationTokenSource? RunCts { get; set; }
        public KeyLoopSlot Configuration { get; set; } = new();
        public int Count { get; set; }
        public int RunId { get; set; }
        public bool Active { get; set; }
        public Task Worker { get; set; } = Task.CompletedTask;
    }

    private readonly ToolHost _host;
    private readonly Func<bool> _isTargetForeground;
    private readonly Func<string, CancellationToken, Task> _pressAction;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SlotRuntime[] _slots;
    private bool _running;
    private bool _shutdownRequested;
    private int _runId;
    private long _lastStatusTimestamp;

    public event Action<KeyLoopStatus>? StatusUpdated;
    public event Action<string>? ErrorOccurred;

    public KeyLoopEngine(ToolHost host)
        : this(host, host.Foreground.IsTargetForeground, host.Input.PressAndReleaseAsync)
    {
    }

    internal KeyLoopEngine(
        ToolHost host,
        Func<bool> isTargetForeground,
        Func<string, CancellationToken, Task> pressAction)
    {
        _host = host;
        _isTargetForeground = isTargetForeground;
        _pressAction = pressAction;
        _slots = Enumerable.Range(0, MaxSlots)
            .Select(index => new SlotRuntime { Index = index })
            .ToArray();
        foreach (var slot in _slots)
            slot.Worker = Task.Run(() => RunSlotWorkerAsync(slot));
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public IReadOnlyList<int> PressCounts
    {
        get { lock (_gate) return _slots.Select(slot => slot.Count).ToArray(); }
    }

    public bool Toggle(IReadOnlyList<KeyLoopSlot> configurations)
    {
        if (IsRunning)
        {
            Stop();
            return false;
        }
        return Start(configurations);
    }

    public bool Start(IReadOnlyList<KeyLoopSlot> configurations)
    {
        var snapshots = Enumerable.Range(0, MaxSlots)
            .Select(index => index < configurations.Count ? configurations[index].CreateSnapshot() : new KeyLoopSlot())
            .ToArray();
        var activeIndexes = new List<int>();

        lock (_gate)
        {
            if (_running || _shutdownRequested) return false;
            if (_host.EmergencyCts.IsCancellationRequested)
                _host.ResetEmergencyStop();

            _runId++;
            for (var index = 0; index < MaxSlots; index++)
            {
                var runtime = _slots[index];
                var configuration = snapshots[index];
                runtime.Configuration = configuration;
                runtime.Count = 0;
                runtime.RunId = _runId;
                runtime.Active = configuration.Enabled && KeyCode.Parse(configuration.Key) != 0;
                runtime.RunCts = runtime.Active
                    ? CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, _host.EmergencyCts.Token)
                    : null;
                if (runtime.Active) activeIndexes.Add(index);
            }

            if (activeIndexes.Count == 0) return false;
            _running = true;
            _lastStatusTimestamp = 0;
        }

        foreach (var index in activeIndexes) _slots[index].WakeSignal.Release();
        ReportStatus("按键循环运行中...", force: true);
        Diag.Log($"[按键循环] 启动: active={string.Join(',', activeIndexes.Select(i => i + 1))}");
        return true;
    }

    public bool Stop(string reason = "按键循环已停止")
    {
        List<CancellationTokenSource> tokens;
        lock (_gate)
        {
            if (!_running) return false;
            _running = false;
            tokens = _slots.Where(slot => slot.RunId == _runId && slot.RunCts is not null)
                .Select(slot => slot.RunCts!)
                .ToList();
            foreach (var slot in _slots.Where(slot => slot.RunId == _runId)) slot.Active = false;
        }

        foreach (var token in tokens) token.Cancel();
        ReportStatus(reason, force: true);
        Diag.Log($"[按键循环] 停止: reason={reason}");
        return true;
    }

    public bool Shutdown(TimeSpan timeout)
    {
        List<CancellationTokenSource> tokens;
        lock (_gate)
        {
            if (_shutdownRequested) return _slots.All(slot => slot.Worker.IsCompleted);
            _shutdownRequested = true;
            _running = false;
            tokens = _slots.Where(slot => slot.RunCts is not null).Select(slot => slot.RunCts!).ToList();
            foreach (var slot in _slots) slot.Active = false;
        }
        foreach (var token in tokens) token.Cancel();
        _shutdownCts.Cancel();
        try { return Task.WhenAll(_slots.Select(slot => slot.Worker)).Wait(timeout); }
        catch (AggregateException) { return _slots.All(slot => slot.Worker.IsCompleted); }
    }

    private async Task RunSlotWorkerAsync(SlotRuntime runtime)
    {
        while (true)
        {
            try
            {
                await runtime.WakeSignal.WaitAsync(_shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            int runId;
            KeyLoopSlot configuration;
            CancellationTokenSource? runCts;
            lock (_gate)
            {
                if (_shutdownRequested) return;
                if (!runtime.Active || runtime.RunCts is null) continue;
                runId = runtime.RunId;
                configuration = runtime.Configuration;
                runCts = runtime.RunCts;
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

                    await _pressAction(configuration.Key, runCts.Token);
                    lock (_gate)
                    {
                        if (runtime.RunId != runId) break;
                        runtime.Count++;
                    }
                    ReportStatus("按键循环运行中...");
                    await Task.Delay(TimeSpan.FromSeconds(configuration.DelaySeconds), runCts.Token);
                }
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested)
            {
                // 正常停止、紧急停止或退出。
            }
            catch (Exception ex)
            {
                error = $"按键槽位 {runtime.Index + 1}（{configuration.Key}）运行失败：{ex.Message}";
                Diag.Log($"[按键循环] 槽位 {runtime.Index + 1} 异常: {ex}");
            }
            finally
            {
                lock (_gate)
                {
                    if (runtime.RunId == runId)
                    {
                        runtime.Active = false;
                        if (ReferenceEquals(runtime.RunCts, runCts)) runtime.RunCts = null;
                    }
                }
                runCts.Dispose();
            }

            if (error is not null)
            {
                StopRunFromWorker(runId, error);
                ErrorOccurred?.Invoke(error);
            }
            else if (_host.EmergencyCts.IsCancellationRequested)
            {
                StopRunFromWorker(runId, "按键循环已紧急停止");
            }
        }
    }

    private void StopRunFromWorker(int runId, string reason)
    {
        List<CancellationTokenSource> tokens;
        lock (_gate)
        {
            if (_runId != runId || !_running) return;
            _running = false;
            tokens = _slots.Where(slot => slot.RunId == runId && slot.RunCts is not null)
                .Select(slot => slot.RunCts!)
                .ToList();
            foreach (var slot in _slots.Where(slot => slot.RunId == runId)) slot.Active = false;
        }
        foreach (var token in tokens) token.Cancel();
        ReportStatus(reason, force: true);
    }

    private void ReportStatus(string text, bool force = false)
    {
        KeyLoopStatus status;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (!force && _lastStatusTimestamp != 0 &&
                Stopwatch.GetElapsedTime(_lastStatusTimestamp, now) < TimeSpan.FromMilliseconds(250))
                return;
            _lastStatusTimestamp = now;
            status = new KeyLoopStatus(_running, _slots.Select(slot => slot.Count).ToArray(), text);
        }
        StatusUpdated?.Invoke(status);
    }
}
