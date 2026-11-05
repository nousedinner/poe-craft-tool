namespace ShiKe.Services;

// 热键回调在 UI 线程调用。仅提醒未配置目标，不改变非游戏前台的静默门禁。
internal sealed class MissingTargetReminder(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long? _lastShownAt;

    internal void ShowIfNeeded(bool enabled, string? targetProcess, Action show)
    {
        if (!enabled || !string.IsNullOrWhiteSpace(targetProcess)) return;

        var now = _timeProvider.GetTimestamp();
        if (_lastShownAt is { } last &&
            _timeProvider.GetElapsedTime(last, now) < TimeSpan.FromSeconds(5))
            return;

        _lastShownAt = now;
        show();
    }
}
