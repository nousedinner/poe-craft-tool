using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Tools.Settings;

namespace ShiKe.Tools.KeyLoop;

public sealed partial class KeyLoopTool : ITool, IEnableableTool, IHotkeySlotConflictProvider
{
    private ToolHost? _host;
    private KeyLoopEngine? _engine;
    private KeyLoopPage? _page;
    private bool _lastReportedRunning;
    private bool _hotkeysPausedForCapture;
    private readonly MissingTargetReminder _missingTargetReminder = new();

    public string Id => "keyloop";
    public string Name => "按键循环";
    public string IconKey => "keyloop";

    public string Hotkey { get; set; } = SettingsDefaults.KeyLoopHotkey;
    public bool NotificationsEnabled { get; set; }
    private bool _enabled = SettingsDefaults.KeyLoopEnabled;
    public bool IsEnabled => _enabled;
    public List<KeyLoopSlot> Slots { get; } = Enumerable.Range(0, KeyLoopEngine.MaxSlots)
        .Select(_ => new KeyLoopSlot())
        .ToList();

    public bool IsRunning => _engine?.IsRunning ?? false;
    internal bool ShutdownCompleted => _engine?.ShutdownCompleted ?? true;
    public event Action<KeyLoopStatus>? StatusUpdated;

    public void Initialize(ToolHost host)
    {
        _host = host;
        _engine = new KeyLoopEngine(host);
        _engine.StatusUpdated += status =>
        {
            host.Statuses.Report(Id, status.Text, status.Running);
            StatusUpdated?.Invoke(status);
            var stateChanged = _lastReportedRunning != status.Running;
            _lastReportedRunning = status.Running;
            if (NotificationsEnabled && stateChanged)
                host.Notification.Show(status.Running ? "▶ 按键循环启动" : "⏹ 按键循环停止");
        };
        _engine.ErrorOccurred += host.Notification.ShowError;
    }

    public FrameworkElement CreatePage() => _page ??= new KeyLoopPage(this);

    internal void RefreshSettingsPresentation() => _page?.RefreshSettingsPresentation();

    public void OnActivate() { }

    public void OnDeactivate()
    {
        // 录制中切走页面也要恢复全局热键，否则热键会一直处于暂停状态。
        // 切页时没有对话框可挂，恢复失败由 EndSlotCapture 走置顶通知，不能只留一行日志。
        EndSlotCapture();
        SaveSettingsToStorage();
    }

    public void OnShutdown()
    {
        if (_engine is not null && !_engine.Shutdown(TimeSpan.FromSeconds(1)))
            Diag.Log("[按键循环] OnShutdown: 1 秒内未完成关闭");
    }

    public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() =>
    [
        new HotkeyRequest
        {
            Key = Hotkey,
            DisplayName = "按键循环切换",
            CheckForeground = true,
            Mode = HotkeyMode.Toggle,
            Handler = ToggleFromHotkey,
            ForegroundRejectedHandler = NotifyMissingTarget,
        },
    ];

    public void Stop() => _engine?.Stop();

    private void NotifyMissingTarget()
    {
        if (_host is null) return;
        _missingTargetReminder.ShowIfNeeded(_enabled, _host.Foreground.TargetProcess,
            () => _host.Notification.Show("尚未锁定游戏进程，请先在设置中选择目标进程"));
    }

    private void ToggleFromHotkey()
    {
        if (!_enabled)
        {
            Diag.Log("[按键循环] 热键被忽略：功能处于停用状态");
            return;
        }
        if (_engine is null) return;
        if (IsRunning)
        {
            _engine.Stop();
            return;
        }
        StartValidated();
    }

    private void StartValidated()
    {
        if (_engine is null || _host is null) return;
        var error = ValidateSlots();
        if (error is not null)
        {
            // 拒绝原因必须进日志：否则只剩一个转瞬即逝的提示，事后无法判断是哪一项不合法。
            Diag.Log($"[按键循环] 启动被拒绝: {error}");
            _host.Notification.ShowError(error);
            return;
        }
        if (_engine.Start(Slots)) return;
        // 引擎拒绝启动以前是静默的：界面像是“没反应”，日志里也没有任何痕迹。
        Diag.Log("[按键循环] 引擎拒绝启动：已在运行、正在退出，或没有可发送的槽位");
        _host.Notification.ShowError("按键循环未能启动，请检查槽位按键与延时设置");
    }

    public ToolEnablementResult SetEnabled(bool enabled)
    {
        if (_host is null) return ToolEnablementResult.Fail("按键循环尚未初始化");
        var result = ToolEnablement.TryApply(_host, this, enabled, value => _enabled = value, Stop, SaveSettingsToStorage);
        if (result.Success)
        {
            _page?.RefreshEnabledPresentation();
        }
        return result;
    }

    public string? ValidateSlots()
    {
        var enabledCount = 0;
        for (var index = 0; index < Slots.Count; index++)
        {
            var slot = Slots[index];
            if (!slot.Enabled) continue;
            enabledCount++;
            if (string.IsNullOrWhiteSpace(slot.Key))
                return $"按键槽位 {index + 1} 已启用，但尚未设置按键";
            if (KeyCode.Parse(slot.Key) == 0 && !MouseButtonKey.TryParse(slot.Key, out _))
                return $"按键槽位 {index + 1} 的按键“{slot.Key}”无法识别";
            if (slot.DelaySeconds is < 0.1 or > 999.0 || double.IsNaN(slot.DelaySeconds))
                return $"按键槽位 {index + 1} 的延时必须在 0.1～999 秒之间";
        }
        if (enabledCount == 0) return "请至少启用一个按键槽位";

        // 槽位键与全局热键、槽位与槽位都不能共用同一个触发器，否则一次按键会同时触发两条路径。
        return HotkeyConflictRules.ValidateSlotKeyConflicts(RegisteredHotkeyBindings(), SlotKeyBindings(null))
            .FirstOrDefault();
    }

    /// <summary>录制槽位键时的即时校验：候选键不能与全局热键或其他已启用槽位重复。</summary>
    internal string? ValidateSlotKey(int index, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return HotkeyConflictRules.ValidateSlotKeyConflicts(RegisteredHotkeyBindings(), SlotKeyBindings(index, key))
            .FirstOrDefault();
    }

    private IReadOnlyList<(string Name, string Key, HotkeyMode Mode)> RegisteredHotkeyBindings()
        => _host is null
            ? []
            : _host.BuildHotkeyRequests()
                .Where(request => !string.IsNullOrWhiteSpace(request.Key))
                .Select(request => (request.DisplayName, request.Key, request.Mode))
                .ToList();

    /// <summary>启用中的槽位键；<paramref name="editingIndex"/> 用候选键替换该槽位当前值。</summary>
    private List<(string Name, string Key)> SlotKeyBindings(int? editingIndex, string? candidateKey = null)
    {
        var bindings = new List<(string, string)>();
        for (var index = 0; index < Slots.Count; index++)
        {
            var slot = Slots[index];
            var key = index == editingIndex ? candidateKey : slot.Key;
            if (!slot.Enabled || string.IsNullOrWhiteSpace(key)) continue;
            bindings.Add(($"槽位 {index + 1}", key));
        }
        // 录制一个尚未启用的槽位时，它本身也要参与校验。
        if (editingIndex is { } target && Slots[target] is { Enabled: false } && !string.IsNullOrWhiteSpace(candidateKey))
            bindings.Add(($"槽位 {target + 1}", candidateKey));
        return bindings;
    }

    IReadOnlyList<(string Name, string Key)> IHotkeySlotConflictProvider.GetEnabledHotkeySlots()
        => SlotKeyBindings(null);

    bool IHotkeySlotConflictProvider.IsCapturingHotkeySlot => _hotkeysPausedForCapture;

    /// <summary>
    /// 进入槽位录制：临时注销全部全局热键。
    /// 否则按下的侧键或功能键会先触发热键（例如连点器按住），录制界面被连点冲刷，配置落不下来。
    /// 因此其他工具运行期间必须拒绝开始录制——和设置页修改热键的规则一致：
    /// 注销期间洗装的停止热键、连点器的热键都会失效，任务却还在跑。
    /// 返回非空表示拒绝原因，调用方负责展示。
    /// </summary>
    internal string? TryBeginSlotCapture()
    {
        if (_host is null) return "按键循环尚未初始化";
        if (_hotkeysPausedForCapture) return null;
        if (_host.RegisteredTools.OfType<IHotkeySlotConflictProvider>()
            .Any(provider => !ReferenceEquals(provider, this) && provider.IsCapturingHotkeySlot))
            return "另一个输入槽位正在录制，请先完成或取消录制后重试";
        if (ToolCapabilities.RunningToolsMessage(_host, "设置槽位按键", Id) is { } running)
            return running;
        _host.Hotkeys.UnregisterAll();
        _hotkeysPausedForCapture = true;
        Diag.Log("[按键循环] 槽位录制开始：已临时暂停全局热键");
        return null;
    }

    /// <summary>
    /// 结束槽位录制并恢复全局热键；可重复调用。
    /// 默认在恢复失败时直接通知用户——这样即使调用点漏掉返回值，也不会变成静默失败；
    /// 需要自己弹对话框的调用方传 <paramref name="notify"/> = false 并消费返回值。
    /// </summary>
    internal IReadOnlyList<string> EndSlotCapture(bool notify = true)
    {
        if (_host is null || !_hotkeysPausedForCapture) return [];

        List<string> issues;
        try
        {
            issues = _host.Hotkeys.ReRegister(_host.BuildHotkeyRequests());
            // 只有把注册流程走完才解除暂停：中途抛异常时保留标志，后续出口还能重试，
            // 否则标志先被清掉，之后所有恢复路径都成了空操作，热键再也回不来。
            _hotkeysPausedForCapture = false;
        }
        catch (Exception ex)
        {
            Diag.Log($"[按键循环] 槽位录制结束但恢复热键抛出异常：{ex.GetType().Name}: {ex.Message}");
            issues = [$"恢复热键时发生异常：{ex.Message}"];
        }

        Diag.Log(issues.Count == 0
            ? "[按键循环] 槽位录制结束：已恢复全局热键"
            : "[按键循环] 槽位录制结束但恢复异常：" + string.Join(" | ", issues));
        if (notify) NotifyRestoreFailure(issues);
        return issues;
    }

    internal bool HotkeysPausedForCapture => _hotkeysPausedForCapture;

    /// <summary>恢复失败必须让用户看见：只写日志会出现“录制结束了，但热键其实没回来”。</summary>
    private void NotifyRestoreFailure(IReadOnlyList<string> issues)
    {
        if (issues.Count == 0) return;
        _host?.Notification.ShowError("槽位录制结束后恢复热键失败：\n" + string.Join("\n", issues));
    }


    public void SaveSettingsToStorage()
    {
        if (_host is null) return;
        _host.Storage.UpdateSettings(settings => settings[Id] = CreateSettingsNode());
    }

    private JsonObject CreateSettingsNode()
    {
        var slots = new JsonArray();
        foreach (var slot in Slots.Take(KeyLoopEngine.MaxSlots))
        {
            slots.Add(new JsonObject
            {
                ["enabled"] = slot.Enabled,
                ["key"] = slot.Key,
                ["delay_s"] = Math.Clamp(slot.DelaySeconds, 0.1, 999.0),
            });
        }
        return new JsonObject
        {
            ["enabled"] = _enabled,
            ["hotkey"] = Hotkey,
            ["slots"] = slots,
            ["notifications_enabled"] = NotificationsEnabled,
        };
    }

    public void LoadSettings(JsonElement section)
    {
        if (section.TryGetProperty("enabled", out var toolEnabled) &&
            toolEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _enabled = toolEnabled.GetBoolean();
        Hotkey = HotkeySetting.Read(section, "hotkey", Hotkey);
        if (section.TryGetProperty("notifications_enabled", out var notifications) &&
            notifications.ValueKind is JsonValueKind.True or JsonValueKind.False)
            NotificationsEnabled = notifications.GetBoolean();

        if (!section.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Array) return;
        var index = 0;
        foreach (var item in slots.EnumerateArray())
        {
            if (index >= KeyLoopEngine.MaxSlots) break;
            if (item.ValueKind != JsonValueKind.Object)
            {
                index++;
                continue;
            }

            var slot = Slots[index++];
            if (item.TryGetProperty("enabled", out var enabled) &&
                enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                slot.Enabled = enabled.GetBoolean();
            if (item.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String)
                slot.Key = key.GetString()?.Trim() ?? string.Empty;
            if (TryReadDelay(item, out var delay))
                slot.DelaySeconds = Math.Clamp(delay, 0.1, 999.0);
        }
    }

    private static bool TryReadDelay(JsonElement item, out double delay)
    {
        if (item.TryGetProperty("delay_s", out var current) && current.ValueKind == JsonValueKind.Number &&
            current.TryGetDouble(out delay)) return true;
        if (item.TryGetProperty("delay", out var legacy) && legacy.ValueKind == JsonValueKind.Number &&
            legacy.TryGetDouble(out delay)) return true;
        delay = 1.0;
        return false;
    }

    public void SaveSettings(Utf8JsonWriter writer) => CreateSettingsNode().WriteTo(writer);
}
