using ShiKe.Services;

namespace ShiKe.Host;

/// <summary>
/// 宿主门面：所有抽屉通过它访问公共服务，不直接引用 Service 实例。
/// 阶段2：注入 8 个服务（Tray 依赖 Window 生命周期，由 App 直接管理）。
/// </summary>
public sealed class ToolHost
{
    private readonly ToolRegistry _registry;

    public ToolHost(ToolRegistry registry)
    {
        _registry = registry;
        EmergencyCts = new CancellationTokenSource();
        Input = new InputSimulator(EmergencyCts);
        Hotkeys = new HotkeyManager();
        Foreground = new ForegroundDetector();
        Storage = new StorageService();
        Notification = new NotificationService();
        Sound = new SoundService();
        Coordinates = new CoordinateRecorder(Storage);
        Network = new NetworkService();
    }

    /// <summary>全局紧急停止令牌源（InputSimulator 光标 ≤(1,1) 时 Cancel）。</summary>
    public CancellationTokenSource EmergencyCts { get; }

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
