namespace ShiKe.Host;

public sealed record ToolRuntimeStatus(string Text, bool Running, int? UseCount);

/// <summary>按工具保存最后状态；全局活动标记与当前页面详情独立计算。</summary>
public sealed class ToolStatusStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ToolRuntimeStatus> _statuses = new(StringComparer.Ordinal);
    public event Action? Changed;

    public void Report(string toolId, string text, bool running, int? useCount = null)
    {
        var status = new ToolRuntimeStatus(text, running, useCount);
        lock (_gate)
        {
            if (_statuses.TryGetValue(toolId, out var previous) && previous == status) return;
            _statuses[toolId] = status;
        }
        // 视图观察者失败不能让后台输入任务失败，也不持有状态锁调用 UI。
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { ShiKe.Services.Diag.Log($"[状态栏] 观察者失败: {ex.Message}"); }
        }
    }

    public (ToolRuntimeStatus? Selected, bool AnyRunning) Read(string toolId)
    {
        lock (_gate)
            return (_statuses.GetValueOrDefault(toolId), _statuses.Values.Any(status => status.Running));
    }
}
