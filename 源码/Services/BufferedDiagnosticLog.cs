using System.Diagnostics;
using System.Text;

namespace ShiKe.Services;

internal sealed record DiagnosticLogOptions(int QueueCapacity = 1024, int BatchSize = 128, int BatchDelayMs = 25,
    int EnqueueWaitMs = 100);
internal sealed record DiagnosticLogSnapshot(long AcceptedLines, long WrittenLines, long FailedLines, long FailedOperations,
    long LateLines, long AbandonedLines, int PendingLines, long WriteOperations, long Truncations,
    bool Stopped, string? LastError, long DroppedLines, long DroppedCommands);

internal interface IDiagnosticLogFiles
{
    void Append(string path, string text);
    bool TrimIfOversized(string path, long maximumBytes, string replacement);
}

/// <summary>
/// 单写入者，固定条数队列及批次。队列满时短暂背压；持续阻塞时丢弃诊断消息而不无限等待业务线程。
/// 文件仅在批次中打开；写入失败沿用原失败跳过语义，计数并在恢复后写标记。
/// </summary>
internal sealed class BufferedDiagnosticLog
{
    private sealed record Entry(long Sequence, string? Line);
    private readonly object _gate = new();
    private readonly Queue<Entry> _queue = new();
    private readonly string _path;
    private readonly DiagnosticLogOptions _options;
    private readonly IDiagnosticLogFiles _files;
    private readonly Action<string>? _reportFailure;
    private readonly Thread _writer;
    private bool _closing, _abandon, _stopped, _urgent, _lastWriteFailed;
    private long _lastSequence, _completedSequence, _acceptedLines, _writtenLines, _failedLines;
    private long _lateLines, _abandonedLines, _unreportedFailedLines, _writeOperations, _truncations;
    private long _failedOperations, _unreportedFailedOperations;
    private long _droppedLines, _droppedCommands, _unreportedDroppedLines, _unreportedDroppedCommands;
    private long _lastDropReportTimestamp;
    private int _pendingLines;
    private string? _lastError;

    internal BufferedDiagnosticLog(string path, DiagnosticLogOptions? options = null,
        IDiagnosticLogFiles? files = null, Action<string>? reportFailure = null)
    {
        _path = path;
        _options = options ?? new();
        if (_options.QueueCapacity < 1 || _options.BatchSize < 1 || _options.BatchDelayMs < 0 ||
            _options.EnqueueWaitMs < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _files = files ?? new DiagnosticLogFiles();
        _reportFailure = reportFailure;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "拾刻诊断日志" };
        _writer.Start();
    }

    /// <summary>返回接收顺序号；0 表示超时丢弃或停机后拒收。内容及其换行不做裁剪。</summary>
    internal long WriteLine(string line) => Enqueue(line);
    internal void ClearOldLog() => Enqueue(null);

    private long Enqueue(string? line)
    {
        var timedOut = false;
        var dropped = false;
        var reportDrop = false;
        long droppedLines = 0, droppedCommands = 0;
        lock (_gate)
        {
            if (_queue.Count >= _options.QueueCapacity && !_closing)
            {
                var timer = Stopwatch.StartNew();
                while (_queue.Count >= _options.QueueCapacity && !_closing)
                {
                    var remaining = TimeSpan.FromMilliseconds(_options.EnqueueWaitMs) - timer.Elapsed;
                    if (remaining <= TimeSpan.Zero) { timedOut = true; break; }
                    Monitor.Wait(_gate, remaining);
                }
            }
            if (!_closing && !timedOut)
            {
                var sequence = ++_lastSequence;
                _queue.Enqueue(new(sequence, line));
                if (line is not null) { _acceptedLines++; _pendingLines++; }
                if (_queue.Count == 1) Monitor.PulseAll(_gate);
                return sequence;
            }
            if (timedOut && !_closing)
            {
                dropped = true;
                if (line is not null) { _droppedLines++; _unreportedDroppedLines++; }
                else { _droppedCommands++; _unreportedDroppedCommands++; }
                var now = Stopwatch.GetTimestamp();
                if (_lastDropReportTimestamp == 0 ||
                    Stopwatch.GetElapsedTime(_lastDropReportTimestamp, now) >= TimeSpan.FromSeconds(30))
                {
                    _lastDropReportTimestamp = now;
                    reportDrop = true;
                    droppedLines = _droppedLines;
                    droppedCommands = _droppedCommands;
                }
            }
            else if (line is not null) _lateLines++;
        }
        if (dropped)
        {
            if (reportDrop) Report(DiagnosticFailureKind.Receive,
                $"[日志] 队列持续满额，已限时放弃诊断消息；累计丢弃 {droppedLines} 行、{droppedCommands} 次清理请求。");
            return 0;
        }
        Report(DiagnosticFailureKind.LateMessage, "[日志] 写入器已关闭，晚到消息未写入文件；未重新启动线程。");
        return 0;
    }

    internal DiagnosticLogSnapshot Snapshot()
    {
        lock (_gate)
            return new(_acceptedLines, _writtenLines, _failedLines, _failedOperations, _lateLines, _abandonedLines,
                _pendingLines, _writeOperations, _truncations, _stopped, _lastError, _droppedLines, _droppedCommands);
    }

    /// <summary>历史失败/超时丢弃查看快照；成功只表示此前已接收队列处理完且最近写入成功。</summary>
    internal bool Flush(TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        lock (_gate)
        {
            var target = _lastSequence;
            _urgent = true;
            Monitor.PulseAll(_gate);
            while (_completedSequence < target && !_abandon && !_stopped)
            {
                var remaining = timeout - timer.Elapsed;
                if (remaining <= TimeSpan.Zero) return false;
                Monitor.Wait(_gate, remaining);
            }
            return _completedSequence >= target && !_lastWriteFailed && !_abandon;
        }
    }

    internal bool Shutdown(TimeSpan timeout)
    {
        lock (_gate)
        {
            _closing = true;
            _urgent = true;
            Monitor.PulseAll(_gate);
        }
        if (_writer.Join(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout))
        {
            lock (_gate) return !_lastWriteFailed && !_abandon;
        }
        lock (_gate)
        {
            _abandon = true;
            var abandoned = _queue.Count(entry => entry.Line is not null);
            _abandonedLines += abandoned;
            _pendingLines -= abandoned;
            _queue.Clear();
            Monitor.PulseAll(_gate);
        }
        Report(DiagnosticFailureKind.Shutdown, "[日志] 退出排空超过预算，已放弃尚未写出的队列；正在进行的系统写入由后台线程结束。");
        return false;
    }

    private void WriteLoop()
    {
        try
        {
            while (true)
            {
                List<Entry> batch;
                long previousFailures;
                long previousFailedOperations;
                long previousDroppedLines;
                long previousDroppedCommands;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_closing) Monitor.Wait(_gate);
                    if (_abandon || (_queue.Count == 0 && _closing)) return;
                    if (!_urgent && !_closing && _queue.Count < _options.BatchSize)
                        Monitor.Wait(_gate, _options.BatchDelayMs);
                    if (_abandon) return;
                    batch = new(_options.BatchSize);
                    var first = _queue.Dequeue();
                    batch.Add(first);
                    if (first.Line is not null)
                        while (batch.Count < _options.BatchSize && _queue.TryPeek(out var next) && next.Line is not null)
                            batch.Add(_queue.Dequeue());
                    previousFailures = _unreportedFailedLines;
                    previousFailedOperations = _unreportedFailedOperations;
                    previousDroppedLines = _unreportedDroppedLines;
                    previousDroppedCommands = _unreportedDroppedCommands;
                    _urgent = _queue.Count > 0 && _urgent;
                    Monitor.PulseAll(_gate);
                }

                var lineCount = batch.Count(entry => entry.Line is not null);
                Exception? failure = null;
                var truncated = false;
                try
                {
                    if (batch[0].Line is null)
                        truncated = _files.TrimIfOversized(_path, 512 * 1024,
                            $"[{DateTime.Now}] === 日志已截断 ==={Environment.NewLine}");
                    else
                    {
                        var text = new StringBuilder();
                        if (previousFailedOperations > 0)
                            text.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] [日志] 写入已恢复；此前因磁盘写入失败有 {previousFailures} 条日志未保存（{previousFailedOperations} 次操作失败）。");
                        if (previousDroppedLines > 0 || previousDroppedCommands > 0)
                            text.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] [日志] 队列曾持续满额；有 {previousDroppedLines} 条消息、{previousDroppedCommands} 次清理请求未接收。");
                        foreach (var entry in batch) text.Append(entry.Line).Append(Environment.NewLine);
                        _files.Append(_path, text.ToString());
                    }
                }
                catch (Exception error) { failure = error; }

                lock (_gate)
                {
                    _completedSequence = batch[^1].Sequence;
                    _pendingLines -= lineCount;
                    _lastWriteFailed = failure is not null;
                    if (lineCount > 0) _writeOperations++;
                    if (failure is null)
                    {
                        _writtenLines += lineCount;
                        if (lineCount > 0)
                        {
                            _unreportedFailedLines = 0;
                            _unreportedFailedOperations = 0;
                            _unreportedDroppedLines -= previousDroppedLines;
                            _unreportedDroppedCommands -= previousDroppedCommands;
                        }
                        if (truncated) _truncations++;
                    }
                    else
                    {
                        _failedLines += lineCount;
                        _unreportedFailedLines += lineCount;
                        _failedOperations++;
                        _unreportedFailedOperations++;
                        _lastError = failure.Message;
                    }
                    Monitor.PulseAll(_gate);
                }
                if (failure is not null) Report(DiagnosticFailureKind.StorageWrite,
                    $"[日志] 磁盘写入失败，{lineCount} 条消息未保存：{failure.Message}");
            }
        }
        finally
        {
            lock (_gate) { _stopped = true; _closing = true; Monitor.PulseAll(_gate); }
        }
    }

    private void Report(DiagnosticFailureKind kind, string message)
    {
        try
        {
            if (_reportFailure is not null) _reportFailure(message);
            else DiagnosticFailureReporter.Report(kind, message);
        }
        catch { }
    }

    private sealed class DiagnosticLogFiles : IDiagnosticLogFiles
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        public void Append(string path, string text)
        {
            var bytes = Utf8.GetBytes(text);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            var originalLength = stream.Length;
            stream.Position = originalLength;
            try { stream.Write(bytes); stream.Flush(); }
            catch
            {
                // 写入期间无其他写入者；尽力撤销半个批次，避免后续接在残缺 UTF-8 字符后。
                try { stream.SetLength(originalLength); } catch { }
                throw;
            }
        }
        public bool TrimIfOversized(string path, long maximumBytes, string replacement)
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= maximumBytes) return false;
            File.WriteAllText(path, replacement, Utf8);
            return true;
        }
    }
}
