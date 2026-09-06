using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.KeyLoop;

public sealed class KeyLoopTool : ITool, IEnableableTool
{
    private ToolHost? _host;
    private KeyLoopEngine? _engine;
    private KeyLoopPage? _page;
    private bool _lastReportedRunning;

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
    public event Action<KeyLoopStatus>? StatusUpdated;

    public void Initialize(ToolHost host)
    {
        _host = host;
        _engine = new KeyLoopEngine(host);
        _engine.StatusUpdated += status =>
        {
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
        _page?.CollectSettingsFromUi();
        SaveSettingsToStorage();
    }

    public void OnShutdown()
    {
        _page?.CollectSettingsFromUi();
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
        },
    ];

    public void ToggleFromPage()
    {
        _page?.CollectSettingsFromUi();
        if (_engine is null || _host is null) return;
        if (!_enabled)
        {
            _host.Notification.ShowError("按键循环当前未启用，请先打开功能开关");
            return;
        }
        if (IsRunning)
        {
            _engine.Stop();
            return;
        }
        if (string.IsNullOrWhiteSpace(_host.Foreground.TargetProcess))
        {
            _host.Notification.ShowError("尚未锁定游戏进程，无法启动按键循环");
            return;
        }
        StartValidated();
    }

    public void Stop() => _engine?.Stop();

    private void ToggleFromHotkey()
    {
        if (!_enabled) return;
        _page?.CollectSettingsFromUi();
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
            _host.Notification.ShowError(error);
            return;
        }
        _engine.Start(Slots);
    }

    public ToolEnablementResult SetEnabled(bool enabled)
    {
        if (_host is null) return ToolEnablementResult.Fail("按键循环尚未初始化");
        var result = ToolEnablement.TryApply(_host, this, enabled, value => _enabled = value, Stop);
        if (result.Success)
        {
            SaveSettingsToStorage();
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
            if (KeyCode.Parse(slot.Key) == 0)
                return $"按键槽位 {index + 1} 的按键“{slot.Key}”无法识别";
            if (slot.DelaySeconds is < 0.1 or > 999.0 || double.IsNaN(slot.DelaySeconds))
                return $"按键槽位 {index + 1} 的延时必须在 0.1～999 秒之间";
        }
        return enabledCount == 0 ? "请至少启用一个按键槽位" : null;
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
        if (section.TryGetProperty("hotkey", out var hotkey) && hotkey.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(hotkey.GetString()))
            Hotkey = hotkey.GetString()!;
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
        if (item.TryGetProperty("delay_s", out var current) && current.TryGetDouble(out delay)) return true;
        if (item.TryGetProperty("delay", out var legacy) && legacy.TryGetDouble(out delay)) return true;
        delay = 1.0;
        return false;
    }

    public void SaveSettings(Utf8JsonWriter writer) => CreateSettingsNode().WriteTo(writer);
}
