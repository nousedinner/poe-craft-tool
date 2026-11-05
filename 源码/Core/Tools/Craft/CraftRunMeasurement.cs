using System.Diagnostics;
using System.Text.Json;

namespace ShiKe.Tools.Craft;

internal sealed record CraftRunReport(string Text, string? Path);

internal sealed record CraftRunSummary(
    double ElapsedMs, double ForegroundPauseMs, double ActiveMs,
    int SentClicks, int ConfirmedTransitions, int MatchedCycles,
    int CopyAttempts, int ClipboardReads, int RejectedCopies, int UnchangedCopies,
    int EmptyCopies, int SyncFailures, double ConfirmedPerSecond, double MatchedPerSecond,
    double? MatchCycleMedianMs, double? MatchCycleP95Ms, string StopReason);

internal sealed record CraftRunSample(int Number, string Currency, bool ClickSent,
    bool Confirmed, bool Matched, double ActiveMs, int CopyAttempts, int ClipboardReads,
    int RejectedCopies, int EmptyCopies, int? PrimaryHits, int? SecondaryHits, bool? Excluded);

internal sealed record CraftRunTrace(CraftRunSample Sample, string? BeforeText, string? AfterText);

/// <summary>
/// 完整任务用单调时钟计时；循环计时从装备点击前到匹配结束。
/// 游戏循环内只更新内存，停止后一次写报告，避免逐条测速落盘影响对照。
/// </summary>
internal sealed class CraftRunMeasurement
{
    private const int SampleLimit = 10000;
    private const int TraceLimit = 30;
    private readonly Func<double> _clock;
    private readonly double _started;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly List<CraftRunSample> _samples = [];
    private readonly Queue<CraftRunTrace> _traces = new();
    private readonly List<double> _matchDurations = [];
    private double? _stopped;
    private double? _pauseStarted;
    private double _pausedMs;
    private Step? _step;
    private string? _lastText;
    private int _stepNumber;
    private int _sentClicks;
    private int _confirmed;
    private int _matched;
    private int _copies;
    private int _reads;
    private int _rejected;
    private int _unchanged;
    private int _empty;
    private int _syncFailures;

    private sealed class Step
    {
        public required int Number;
        public required string Currency;
        public required double StartedActiveMs;
        public required int CopiesBefore;
        public required int ReadsBefore;
        public required int RejectedBefore;
        public required int EmptyBefore;
        public string? BeforeText;
        public string? AfterText;
        public bool ClickSent;
        public bool Confirmed;
        public double? ConfirmedActiveMs;
    }

    public CraftRunMeasurement(Func<double>? clock = null)
    {
        _clock = clock ?? (() => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency);
        _started = _clock();
    }

    private double Now => _stopped ?? _clock();
    private double PauseMs => _pausedMs + (_pauseStarted.HasValue ? Now - _pauseStarted.Value : 0);
    private double ActiveMs => Math.Max(0, Now - _started - PauseMs);

    public void PauseForeground()
    {
        if (!_stopped.HasValue) _pauseStarted ??= _clock();
    }

    public void ResumeForeground()
    {
        if (!_pauseStarted.HasValue || _stopped.HasValue) return;
        _pausedMs += _clock() - _pauseStarted.Value;
        _pauseStarted = null;
    }

    public void RecordBaseline(string text) => _lastText = text;

    public void BeginClick(string currency)
    {
        FinishStep(null); // 重铸等仅确认转换的操作也独立记录，不假记为完成匹配。
        _step = new Step
        {
            Number = ++_stepNumber, Currency = currency, StartedActiveMs = ActiveMs,
            CopiesBefore = _copies, ReadsBefore = _reads, RejectedBefore = _rejected,
            EmptyBefore = _empty, BeforeText = _lastText,
        };
    }

    public void ClickSent()
    {
        if (_step is null || _step.ClickSent) return;
        _step.ClickSent = true;
        _sentClicks++;
    }

    public void CopyAttempt() => _copies++;
    public void ClipboardRead() => _reads++;
    public void EmptyCopy() => _empty++;
    public void RejectCopy(bool unchanged) { _rejected++; if (unchanged) _unchanged++; }
    public void SyncFailed() => _syncFailures++;

    public void ConfirmTransition(string text)
    {
        if (_step is not null && _step.ClickSent && !_step.Confirmed)
        {
            _step.Confirmed = true;
            _step.ConfirmedActiveMs = ActiveMs;
            _step.AfterText = text;
            _confirmed++;
        }
        _lastText = text;
    }

    public void CompleteMatching(string text, AffixCheckResult result)
    {
        // 启动基线及重复判定不能制造成功循环；必须对应已发送且已确认的本次点击。
        if (_step is not { Confirmed: true } || _step.AfterText != text) return;
        _matched++;
        if (_matchDurations.Count < SampleLimit) _matchDurations.Add(ActiveMs - _step.StartedActiveMs);
        FinishStep(result);
    }

    private void FinishStep(AffixCheckResult? result)
    {
        if (_step is null) return;
        var finishedAt = result is null && _step.ConfirmedActiveMs.HasValue
            ? _step.ConfirmedActiveMs.Value : ActiveMs;
        var sample = new CraftRunSample(_step.Number, _step.Currency, _step.ClickSent,
            _step.Confirmed, result is not null, Math.Max(0, finishedAt - _step.StartedActiveMs),
            _copies - _step.CopiesBefore, _reads - _step.ReadsBefore,
            _rejected - _step.RejectedBefore, _empty - _step.EmptyBefore,
            result?.PrimaryHits, result?.SecondaryHits, result?.HasExclude);
        if (_samples.Count < SampleLimit) _samples.Add(sample);
        _traces.Enqueue(new(sample, _step.BeforeText, _step.AfterText));
        while (_traces.Count > TraceLimit) _traces.Dequeue();
        _step = null;
    }

    public CraftRunSummary Stop(string reason)
    {
        _stopped ??= _clock();
        FinishStep(null);
        var activeMs = ActiveMs;
        var ordered = _matchDurations.Order().ToArray();
        double? Percentile(double proportion) => ordered.Length == 0 ? null
            : ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * proportion) - 1, 0, ordered.Length - 1)];
        return new(Now - _started, PauseMs, activeMs, _sentClicks, _confirmed, _matched,
            _copies, _reads, _rejected, _unchanged, _empty, _syncFailures,
            activeMs <= 0 ? 0 : _confirmed * 1000d / activeMs,
            activeMs <= 0 ? 0 : _matched * 1000d / activeMs,
            Percentile(0.5), Percentile(0.95), reason);
    }

    public string SerializeReport(CraftRunSummary summary, object context) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, startedUtc = _startedUtc, context, summary,
        scope = "实际引擎运行计时；不验证未被复制到的中间状态，不据此证明没有漏洗",
        timingScope = "任务平均包含启动基线、选通货、复制、判定及原有日志；扣除引擎明确等待前台的时间。循环耗时为装备点击前到判定完成。",
        sampleLimit = SampleLimit, sampleCount = _samples.Count,
        samplesTruncated = _stepNumber > _samples.Count,
        percentileScope = "前10000次已完成匹配循环；仅确认转换及失败循环不参与",
        samples = _samples, recentItemStates = _traces.ToArray(),
    }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    public CraftRunReport SaveReport(CraftRunSummary summary, object context, string dataDirectory)
    {
        var message = $"完成判定{summary.MatchedCycles}次，任务平均{summary.MatchedPerSecond:0.00}次/秒；" +
                      $"转换失败{summary.SyncFailures}次，拒绝{summary.RejectedCopies}次，空复制{summary.EmptyCopies}次。";
        try
        {
            var directory = System.IO.Path.Combine(dataDirectory, "craft-runs");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, $"run-{_startedUtc:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, SerializeReport(summary, context));
            return new(message, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return new(message + $" 报告未保存：{error.Message}", null);
        }
    }
}
