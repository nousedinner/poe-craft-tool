using System.Diagnostics.Tracing;

namespace ShiKe.Services;

internal enum DiagnosticFailureKind
{
    StorageWrite = 1,
    Receive = 2,
    Shutdown = 3,
    LateMessage = 4,
}

internal sealed record DiagnosticFailure(long Sequence, DateTimeOffset OccurredUtc,
    DiagnosticFailureKind Kind, string Message)
{
    internal bool CanNotify => Kind is DiagnosticFailureKind.StorageWrite or DiagnosticFailureKind.Receive;
}

/// <summary>独立于故障日志文件的Release诊断通道，不创建文件、工作线程或需要权限的系统资源。</summary>
internal static class DiagnosticFailureReporter
{
    private static readonly DiagnosticFailureHub Hub = new(TimeProvider.System,
        failure => DiagnosticFailureEventSource.Log.Failure(failure.Sequence, (int)failure.Kind, failure.Message));

    internal static DiagnosticFailure? Latest => Hub.Latest;
    internal static event Action<DiagnosticFailure> NotificationRequested
    {
        add => Hub.NotificationRequested += value;
        remove => Hub.NotificationRequested -= value;
    }

    internal static void Report(DiagnosticFailureKind kind, string message) => Hub.Report(kind, message);
}

/// <summary>只保留最后一条错误和累计序号；UI通知限频，诊断事件不依赖UI是否已订阅。</summary>
internal sealed class DiagnosticFailureHub(TimeProvider clock, Action<DiagnosticFailure> emit)
{
    internal const int MaximumMessageCharacters = 1000;
    private static readonly TimeSpan NotificationInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private DiagnosticFailure? _latest;
    private long _sequence;
    private long _lastNotification;
    private bool _hasNotification;

    internal DiagnosticFailure? Latest { get { lock (_gate) return _latest; } }
    internal event Action<DiagnosticFailure>? NotificationRequested;

    internal void Report(DiagnosticFailureKind kind, string message)
    {
        DiagnosticFailure failure;
        Action<DiagnosticFailure>? notify;
        lock (_gate)
        {
            failure = new(++_sequence, clock.GetUtcNow(), kind, BoundMessage(message));
            _latest = failure;
            var now = clock.GetTimestamp();
            var notificationDue = failure.CanNotify && (!_hasNotification ||
                clock.GetElapsedTime(_lastNotification, now) >= NotificationInterval);
            notify = notificationDue ? NotificationRequested : null;
            if (notify is not null)
            {
                _hasNotification = true;
                _lastNotification = now;
            }
        }

        // EventListener及UI观察者均是外部回调，不能占用状态锁，也不能回写故障日志。
        try { emit(failure); } catch { }
        if (notify is null) return;
        foreach (Action<DiagnosticFailure> observer in notify.GetInvocationList())
        {
            try { observer(failure); } catch { }
        }
    }

    private static string BoundMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "诊断日志发生未知错误";
        if (message.Length <= MaximumMessageCharacters) return message;
        var length = MaximumMessageCharacters - 1;
        if (char.IsHighSurrogate(message[length - 1])) length--;
        return message[..length] + "…";
    }
}

[EventSource(Name = "ShiKe-DiagnosticFailures")]
internal sealed class DiagnosticFailureEventSource : EventSource
{
    internal static readonly DiagnosticFailureEventSource Log = new();

    [Event(1, Level = EventLevel.Error)]
    public void Failure(long sequence, int kind, string message)
        => WriteEvent(1, sequence, kind, message);
}
