using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Tools.Clicker;
using ShiKe.Tools.Craft;
using ShiKe.Tools.Hideout;
using ShiKe.Tools.KeyLoop;

namespace ShiKe.Tools.Settings;

/// <summary>
/// 宿主设置抽屉。Id 使用 host，因此 settings.json 仍保持既定的五分节结构，
/// Settings 页面本身不创建额外持久化分节。
/// </summary>
public sealed class SettingsTool : ITool
{
    private readonly CraftTool _craft;
    private readonly ClickerTool _clicker;
    private readonly KeyLoopTool _keyLoop;
    private readonly HideoutTool _hideout;
    private ToolHost? _host;
    private SettingsPage? _page;
    private bool _hotkeysSuspendedForCapture;

    public SettingsTool(CraftTool craft, ClickerTool clicker, KeyLoopTool keyLoop, HideoutTool hideout)
    {
        _craft = craft;
        _clicker = clicker;
        _keyLoop = keyLoop;
        _hideout = hideout;
    }

    public string Id => "host";
    public string Name => "设置";
    public string IconKey => "settings";

    public void Initialize(ToolHost host)
    {
        _host = host;
        host.Sound.PlaybackStatusChanged += status => _page?.ShowSoundStatus(status);
    }

    public FrameworkElement CreatePage() => _page ??= new SettingsPage(this);

    public void OnActivate() => _page?.ActivatePage();

    public void OnDeactivate() => _page?.DeactivatePage();

    public void OnShutdown() { }

    public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() => [];

    internal SettingsApplyResult BeginHotkeyCapture()
    {
        var host = _host ?? throw new InvalidOperationException("SettingsTool 尚未初始化");
        if (HasActiveAutomation())
            return SettingsApplyResult.Fail("请先停止洗装、连点器和按键循环，再修改热键");
        if (_hotkeysSuspendedForCapture)
            return SettingsApplyResult.Ok("正在捕获热键");

        host.Hotkeys.UnregisterAll();
        _hotkeysSuspendedForCapture = true;
        Diag.Log("[设置页] 热键捕获开始：已临时暂停全局热键");
        return SettingsApplyResult.Ok("正在捕获热键");
    }

    internal IReadOnlyList<string> EndHotkeyCapture(bool hotkeysAlreadyRegistered)
    {
        if (!_hotkeysSuspendedForCapture) return [];
        _hotkeysSuspendedForCapture = false;
        if (hotkeysAlreadyRegistered)
        {
            Diag.Log("[设置页] 热键捕获结束：新热键已由设置事务注册");
            return [];
        }

        var host = _host;
        if (host is null) return ["热键服务尚未初始化"];
        var issues = host.Hotkeys.ReRegister(host.BuildHotkeyRequests());
        Diag.Log(issues.Count == 0
            ? "[设置页] 热键捕获结束：已恢复全局热键"
            : "[设置页] 热键捕获结束但恢复异常：" + string.Join(" | ", issues));
        return issues;
    }

    public SettingsDraft CaptureDraft()
    {
        var host = _host ?? throw new InvalidOperationException("SettingsTool 尚未初始化");
        return new SettingsDraft(
            new HotkeySettings(
                _craft.HotkeyStart,
                _craft.HotkeyStop,
                host.CoordinateHotkey,
                _clicker.Hotkey,
                _clicker.HoldHotkey,
                _keyLoop.Hotkey,
                _hideout.Hotkey),
            host.Foreground.TargetProcess ?? string.Empty,
            host.AutoDetectPoe,
            _craft.SoundEnabled,
            _craft.PopupEnabled,
            _craft.SelectedSound,
            _clicker.NotificationsEnabled,
            _keyLoop.NotificationsEnabled,
            _hideout.IsEnabled,
            _hideout.Command,
            _craft.IsEnabled,
            _clicker.IsEnabled,
            _keyLoop.IsEnabled);
    }

    public SettingsApplyResult ApplyDraft(SettingsDraft requested)
    {
        var host = _host ?? throw new InvalidOperationException("SettingsTool 尚未初始化");
        var candidate = requested.Normalize();
        var validationErrors = SettingsValidation.Validate(candidate);
        if (validationErrors.Count > 0)
            return SettingsApplyResult.Fail(string.Join("\n", validationErrors));

        var previous = CaptureDraft().Normalize();
        var hotkeysChanged = previous.Hotkeys != candidate.Hotkeys;
        var targetChanged = !string.Equals(previous.TargetProcess, candidate.TargetProcess,
            StringComparison.OrdinalIgnoreCase);
        if ((hotkeysChanged || targetChanged) && HasActiveAutomation())
            return SettingsApplyResult.Fail("请先停止洗装、连点器和按键循环，再修改热键或目标进程");

        if (hotkeysChanged)
        {
            var hotkeyResult = HotkeySettingsTransaction.TryApply(
                previous.Hotkeys,
                candidate.Hotkeys,
                ApplyHotkeys,
                () => host.Hotkeys.ReRegister(host.BuildHotkeyRequests()));
            if (!hotkeyResult.Success)
            {
                RefreshPresentations();
                return hotkeyResult;
            }
        }

        try
        {
            ApplyRuntime(candidate);
            SaveAllSections();
            RefreshPresentations();
            return SettingsApplyResult.Ok();
        }
        catch (Exception ex)
        {
            Diag.Log($"[设置页] 保存失败，恢复旧配置: {ex.GetType().Name}: {ex.Message}");
            ApplyRuntime(previous);
            if (hotkeysChanged)
            {
                var rollback = host.Hotkeys.ReRegister(host.BuildHotkeyRequests());
                if (rollback.Count > 0)
                    Diag.Log("[设置页] 保存失败后的热键恢复异常: " + string.Join(" | ", rollback));
            }
            try { SaveAllSections(); }
            catch (Exception rollbackEx) { Diag.Log($"[设置页] 旧设置回写失败: {rollbackEx.Message}"); }
            RefreshPresentations();
            return SettingsApplyResult.Fail($"设置保存失败，已恢复修改前配置：{ex.Message}");
        }
    }

    public IReadOnlyList<string> GetRunningProcesses()
        => ForegroundDetector.GetRunningProcesses();

    public IReadOnlyList<string> GetSounds()
        => _host?.Sound.ScanSounds() ?? [];

    public string SoundDirectory => _host?.Sound.SoundDirectory ?? Path.Combine(AppContext.BaseDirectory, "sounds");

    public SettingsApplyResult PreviewSound(string soundFileName)
    {
        if (_host is null) return SettingsApplyResult.Fail("声音服务尚未初始化");
        var normalized = soundFileName.Trim();
        if (!GetSounds().Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return SettingsApplyResult.Fail("所选音效不在 sounds 目录中，请先刷新列表");
        var request = _host.Sound.TryPlay(normalized);
        return request.Accepted
            ? SettingsApplyResult.Ok(request.Message)
            : SettingsApplyResult.Fail(request.Message);
    }

    /// <summary>启动自动检测成功后同步运行状态、Settings 页面和 host 分节。</summary>
    public void ApplyDetectedTarget(string targetProcess)
    {
        if (_host is null || string.IsNullOrWhiteSpace(targetProcess)) return;
        _host.Foreground.TargetProcess = targetProcess.Trim();
        SaveHostSection();
        _page?.RefreshFromTool(refreshLists: false);
    }

    private bool HasActiveAutomation()
        => _craft.IsRunning || _clicker.IsRunning || _keyLoop.IsRunning;

    private void ApplyRuntime(SettingsDraft draft)
    {
        ApplyHotkeys(draft.Hotkeys);
        var host = _host!;
        host.Foreground.TargetProcess = draft.TargetProcess;
        host.AutoDetectPoe = draft.AutoDetectPoe;
        _craft.SoundEnabled = draft.CraftSoundEnabled;
        _craft.PopupEnabled = draft.CraftPopupEnabled;
        _craft.SelectedSound = draft.SelectedSound;
        _clicker.NotificationsEnabled = draft.ClickerNotificationsEnabled;
        _keyLoop.NotificationsEnabled = draft.KeyLoopNotificationsEnabled;
        _hideout.ApplySharedSettings(draft.HideoutEnabled, draft.Hotkeys.Hideout, draft.HideoutCommand);
    }

    private void ApplyHotkeys(HotkeySettings hotkeys)
    {
        var host = _host!;
        _craft.HotkeyStart = hotkeys.CraftStart;
        _craft.HotkeyStop = hotkeys.CraftStop;
        host.CoordinateHotkey = hotkeys.Coordinate;
        _clicker.Hotkey = hotkeys.ClickerToggle;
        _clicker.HoldHotkey = hotkeys.ClickerHold;
        _keyLoop.Hotkey = hotkeys.KeyLoop;
        _hideout.ApplySharedSettings(_hideout.IsEnabled, hotkeys.Hideout, _hideout.Command);
    }

    private void RefreshPresentations()
    {
        _craft.RefreshSettingsPresentation();
        _clicker.RefreshSettingsPresentation();
        _keyLoop.RefreshSettingsPresentation();
        _page?.RefreshFromTool(refreshLists: false);
    }

    private void SaveAllSections()
    {
        var host = _host!;
        var hostSection = CreateHostSection();
        var craftSection = SerializeTool(_craft);
        var clickerSection = SerializeTool(_clicker);
        var keyLoopSection = SerializeTool(_keyLoop);
        var hideoutSection = SerializeTool(_hideout);
        host.Storage.UpdateSettings(root =>
        {
            root["host"] = hostSection;
            root["craft"] = craftSection;
            root["clicker"] = clickerSection;
            root["keyloop"] = keyLoopSection;
            root["hideout"] = hideoutSection;
        });
    }

    private void SaveHostSection()
    {
        var section = CreateHostSection();
        _host!.Storage.UpdateSettings(root => root["host"] = section);
    }

    private static JsonObject SerializeTool(ITool tool)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            tool.SaveSettings(writer);
        return JsonNode.Parse(stream.ToArray()) as JsonObject ?? [];
    }

    private JsonObject CreateHostSection() => new()
    {
        ["target_process"] = _host?.Foreground.TargetProcess ?? string.Empty,
        ["auto_detect_poe"] = _host?.AutoDetectPoe ?? SettingsDefaults.AutoDetectPoe,
        ["hotkeys"] = new JsonObject
        {
            ["start"] = _craft.HotkeyStart,
            ["stop"] = _craft.HotkeyStop,
            ["coordinate"] = _host?.CoordinateHotkey ?? SettingsDefaults.HotkeySetCoord,
        },
    };

    public void LoadSettings(JsonElement section)
    {
        if (_host is null) return;
        if (section.TryGetProperty("target_process", out var process) && process.ValueKind == JsonValueKind.String)
            _host.Foreground.TargetProcess = process.GetString()?.Trim() ?? string.Empty;
        if (section.TryGetProperty("auto_detect_poe", out var autoDetect) &&
            autoDetect.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _host.AutoDetectPoe = autoDetect.GetBoolean();
        if (!section.TryGetProperty("hotkeys", out var hotkeys) || hotkeys.ValueKind != JsonValueKind.Object) return;

        _craft.HotkeyStart = ReadHotkey(hotkeys, "start", SettingsDefaults.HotkeyStart);
        _craft.HotkeyStop = ReadHotkey(hotkeys, "stop", SettingsDefaults.HotkeyStop);
        _host.CoordinateHotkey = ReadHotkey(hotkeys, "coordinate", SettingsDefaults.HotkeySetCoord);
    }

    private static string ReadHotkey(JsonElement section, string key, string fallback)
        => section.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : fallback;

    public void SaveSettings(Utf8JsonWriter writer) => CreateHostSection().WriteTo(writer);
}
