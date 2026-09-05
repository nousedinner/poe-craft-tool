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
        Foreground = new ForegroundDetector();
        Hotkeys = new HotkeyManager(Foreground.IsTargetForeground);
        // InputSimulator 动态取当前 EmergencyCts（审查 D：Reset 后不再持有旧 CTS）+ 前台检测（审查 B）
        Input = new InputSimulator(() => _emergencyCts, Foreground);
        Storage = new StorageService();
        Notification = new NotificationService();
        Sound = new SoundService();
        Coordinates = new CoordinateRecorder(Storage);
        Network = new NetworkService();
    }

    public CancellationTokenSource EmergencyCts => _emergencyCts;

    /// <summary>宿主级坐标录制热键；由 SettingsTool 读写 host.hotkeys.coordinate。</summary>
    public string CoordinateHotkey { get; set; } = SettingsDefaults.HotkeySetCoord;

    /// <summary>是否在启动后自动检测 PoE 进程；由 SettingsTool 读写 host.auto_detect_poe。</summary>
    public bool AutoDetectPoe { get; set; } = SettingsDefaults.AutoDetectPoe;

    /// <summary>
    /// 重置紧急停止令牌。CTS 一旦 Cancel 不可恢复（Python Event.clear() 可重置），
    /// 每次运行前检查并重置，避免"误触一次紧急停止 → 之后所有启动立即失效"。
    /// 注：InputSimulator 持有旧引用，其 CheckEmergencyStop 的 Cancel 仅对旧令牌生效（无害），
    /// 但抛出的 OperationCanceledException 仍会取消当前运行——紧急停止功能不受影响。
    /// </summary>
    public void ResetEmergencyStop()
    {
        Diag.Log("[宿主] ResetEmergencyStop: 创建新 CTS");
        _emergencyCts = new();
    }

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

    /// <summary>
    /// 汇总全部工具热键和宿主级坐标录制热键。启动注册与设置页重注册必须共用此入口，
    /// 防止两处热键清单逐渐漂移。
    /// </summary>
    public IReadOnlyList<HotkeyRequest> BuildHotkeyRequests()
    {
        var requests = RegisteredTools.SelectMany(tool => tool.GetHotkeyRequests()).ToList();
        if (RegisteredTools.Any(tool => tool is ICoordinateProvider))
        {
            requests.Add(new HotkeyRequest
            {
                Key = CoordinateHotkey,
                DisplayName = "坐标录制",
                CheckForeground = false,
                Mode = HotkeyMode.Toggle,
                Handler = Coordinates.OnRecordHotkey,
            });
        }
        return requests;
    }
}
