using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Clicker;

public sealed class ClickerTool : ITool, IEnableableTool
{
    private ToolHost? _host;
    private ClickerEngine? _engine;
    private ClickerPage? _page;
    private bool _holdStarted;
    private bool _lastReportedRunning;

    public string Id => "clicker";
    public string Name => "连点器";
    public string IconKey => "clicker";

    public string Hotkey { get; set; } = SettingsDefaults.ClickerHotkey;
    public string HoldHotkey { get; set; } = SettingsDefaults.ClickerHoldHotkey;
    public int IntervalMs { get; set; } = SettingsDefaults.ClickerIntervalMs;
    public ClickerMouseButton MouseButton { get; set; } = ClickerMouseButton.Left;
    public bool NotificationsEnabled { get; set; }
    private bool _enabled = SettingsDefaults.ClickerEnabled;
    public bool IsEnabled => _enabled;

    public bool IsRunning => _engine?.IsRunning ?? false;
    public int ClickCount => _engine?.ClickCount ?? 0;
    public event Action<ClickerStatus>? StatusUpdated;

    public void Initialize(ToolHost host)
    {
        _host = host;
        _engine = new ClickerEngine(host);
        _engine.StatusUpdated += status =>
        {
            host.Statuses.Report(Id, status.Text, status.Running);
            StatusUpdated?.Invoke(status);
            var stateChanged = _lastReportedRunning != status.Running;
            _lastReportedRunning = status.Running;
            if (NotificationsEnabled && stateChanged)
                host.Notification.Show(status.Running ? "▶ 连点器启动" : "⏹ 连点器停止");
        };
        _engine.ErrorOccurred += host.Notification.ShowError;
    }

    public FrameworkElement CreatePage() => _page ??= new ClickerPage(this);

    internal void RefreshSettingsPresentation() => _page?.RefreshSettingsPresentation();

    public void OnActivate() { }

    public void OnDeactivate()
    {
        _page?.CollectSettingsFromUi();
        SaveSettingsToStorage();
    }

    public void OnShutdown()
    {
        _page?.CollectSettingsFromUi();
        if (_engine is not null && !_engine.Shutdown(TimeSpan.FromSeconds(1)))
            Diag.Log("[连点] OnShutdown: 1 秒内未完成关闭");
    }

    public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() =>
    [
        new HotkeyRequest
        {
            Key = Hotkey,
            DisplayName = "连点器切换",
            CheckForeground = true,
            Mode = HotkeyMode.Toggle,
            Handler = ToggleFromHotkey,
        },
        new HotkeyRequest
        {
            Key = HoldHotkey,
            DisplayName = "连点器按住",
            CheckForeground = true,
            Mode = HotkeyMode.Hold,
            Handler = StartHoldFromHotkey,
            ReleaseHandler = StopHoldFromHotkey,
        },
    ];

    public void ToggleFromPage()
    {
        _page?.CollectSettingsFromUi();
        if (_engine is null || _host is null) return;
        if (!_enabled)
        {
            _host.Notification.ShowError("连点器当前未启用，请先打开功能开关");
            return;
        }
        if (!IsRunning && string.IsNullOrWhiteSpace(_host.Foreground.TargetProcess))
        {
            _host.Notification.ShowError("尚未锁定游戏进程，无法启动连点器");
            return;
        }
        _holdStarted = false;
        _engine.Toggle(MouseButton, IntervalMs);
    }

    public void Stop() => _engine?.Stop();

    private void ToggleFromHotkey()
    {
        if (!_enabled) return;
        _page?.CollectSettingsFromUi();
        _holdStarted = false;
        _engine?.Toggle(MouseButton, IntervalMs);
    }

    private void StartHoldFromHotkey()
    {
        if (!_enabled) return;
        _page?.CollectSettingsFromUi();
        _holdStarted = _engine?.Start(MouseButton, IntervalMs) == true;
    }

    private void StopHoldFromHotkey()
    {
        if (!_holdStarted) return;
        _holdStarted = false;
        _engine?.Stop("按住连点已停止");
    }

    public ToolEnablementResult SetEnabled(bool enabled)
    {
        if (_host is null) return ToolEnablementResult.Fail("连点器尚未初始化");
        var result = ToolEnablement.TryApply(_host, this, enabled, value => _enabled = value, Stop, SaveSettingsToStorage);
        if (result.Success)
        {
            _page?.RefreshEnabledPresentation();
        }
        return result;
    }

    public void SaveSettingsToStorage()
    {
        if (_host is null) return;
        _host.Storage.UpdateSettings(settings => settings[Id] = CreateSettingsNode());
    }

    private JsonObject CreateSettingsNode() => new()
    {
        ["enabled"] = _enabled,
        ["hotkey"] = Hotkey,
        ["hold_hotkey"] = HoldHotkey,
        ["interval_ms"] = Math.Clamp(IntervalMs, 10, 200),
        ["button"] = MouseButton == ClickerMouseButton.Right ? "right" : "left",
        ["notifications_enabled"] = NotificationsEnabled,
    };

    public void LoadSettings(JsonElement section)
    {
        if (section.TryGetProperty("enabled", out var enabled) &&
            enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _enabled = enabled.GetBoolean();
        Hotkey = HotkeySetting.Read(section, "hotkey", Hotkey);
        HoldHotkey = HotkeySetting.Read(section, "hold_hotkey", HoldHotkey);
        if (section.TryGetProperty("interval_ms", out var interval) && interval.ValueKind == JsonValueKind.Number &&
            interval.TryGetInt32(out var intervalMs))
            IntervalMs = Math.Clamp(intervalMs, 10, 200);
        if (section.TryGetProperty("button", out var button) && button.ValueKind == JsonValueKind.String)
            MouseButton = string.Equals(button.GetString(), "right", StringComparison.OrdinalIgnoreCase)
                ? ClickerMouseButton.Right
                : ClickerMouseButton.Left;
        if (section.TryGetProperty("notifications_enabled", out var notifications) &&
            notifications.ValueKind is JsonValueKind.True or JsonValueKind.False)
            NotificationsEnabled = notifications.GetBoolean();
    }

    public void SaveSettings(Utf8JsonWriter writer)
    {
        CreateSettingsNode().WriteTo(writer);
    }
}
