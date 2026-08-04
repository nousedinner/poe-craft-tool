using ShiKe.Services;

namespace ShiKe.Host;

/// <summary>
/// 宿主门面：所有抽屉通过它访问公共服务，不直接引用 Service 实例。
/// 阶段2：注入 8 个服务（Tray 依赖 Window 生命周期，由 App 直接管理）。
/// </summary>
public sealed class ToolHost
{
    private readonly ToolRegistry _registry;
    private CancellationTokenSource _emergencyCts = new();

    public ToolHost(ToolRegistry registry)
    {
        _registry = registry;
        Input = new InputSimulator(_emergencyCts);
        Hotkeys = new HotkeyManager();
        Foreground = new ForegroundDetector();
        Storage = new StorageService();
        Notification = new NotificationService();
        Sound = new SoundService();
        Coordinates = new CoordinateRecorder(Storage);
        Network = new NetworkService();
    }

    public CancellationTokenSource EmergencyCts => _emergencyCts;

    /// <summary>
    /// 重置紧急停止令牌。CTS 一旦 Cancel 不可恢复（Python Event.clear() 可重置），
    /// 每次运行前检查并重置，避免"误触一次紧急停止 → 之后所有启动立即失效"。
    /// 注：InputSimulator 持有旧引用，其 CheckEmergencyStop 的 Cancel 仅对旧令牌生效（无害），
    /// 但抛出的 OperationCanceledException 仍会取消当前运行——紧急停止功能不受影响。
    /// </summary>
    public void ResetEmergencyStop() => _emergencyCts = new();

    public InputSimulator Input { get; }
    public HotkeyManager Hotkeys { get; }
    public ForegroundDetector Foreground { get; }
    public StorageService Storage { get; }
    public NotificationService Notification { get; }
    public SoundService Sound { get; }
    public CoordinateRecorder Coordinates { get; }
    public NetworkService Network { get; }

    /// <summary>已注册的全部抽屉。</summary>
    public IReadOnlyList<ITool> RegisteredTools => _registry.Tools;

    /// <summary>洗装是否运行中（关闭窗口时判断是否拦截到托盘）。阶段3由 CraftTool 设置。</summary>
    public Func<bool> IsCraftRunning { get; set; } = () => false;

    /// <summary>触发全局紧急停止（所有链接此 token 的后台 Task 自动停止）。</summary>
    public void TriggerEmergencyStop() => EmergencyCts.Cancel();
}
