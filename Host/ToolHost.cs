namespace ShiKe.Host;

/// <summary>
/// 宿主门面：所有抽屉通过它访问公共服务，不直接引用 Service 实例。
/// 阶段1：仅全局紧急停止令牌 + 已注册抽屉列表；服务属性阶段2注入。
/// </summary>
public sealed class ToolHost
{
    private readonly ToolRegistry _registry;

    public ToolHost(ToolRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>全局紧急停止令牌源（阶段2 InputSimulator 接入：光标 ≤(1,1) 时 Cancel）。</summary>
    public CancellationTokenSource EmergencyCts { get; } = new();

    /// <summary>已注册的全部抽屉。</summary>
    public IReadOnlyList<ITool> RegisteredTools => _registry.Tools;

    /// <summary>触发全局紧急停止（所有链接此 token 的后台 Task 自动停止）。</summary>
    public void TriggerEmergencyStop() => EmergencyCts.Cancel();
}
