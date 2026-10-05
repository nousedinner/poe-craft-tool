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

    public SettingsApplyResult ApplyHotkey(string id, string key)
    {
        var current = CaptureDraft();
        var resolution = HotkeyConflictResolver.Resolve(current.Hotkeys.WithKey(id, key), id);
        var result = ApplyDraft(current with { Hotkeys = resolution.Hotkeys });
        if (!result.Success) return result;

        var entry = resolution.Hotkeys.GetEntries().Single(entry => entry.Id == id);
        var message = string.IsNullOrEmpty(entry.Key)
            ? $"{entry.Name}热键已清空"
            : $"{entry.Name}热键已设为 {entry.Key}";
        if (resolution.ClearedNames.Count > 0)
            message += $"；已自动清空冲突项：{string.Join("、", resolution.ClearedNames)}";
        if (string.IsNullOrEmpty(resolution.Hotkeys.CraftStop))
            message += "\n洗装停止热键未绑定，重新绑定前无法启动洗装";
        return SettingsApplyResult.Ok(message);
    }

    public SettingsApplyResult ApplyDraft(SettingsDraft requested)
    {
        var host = _host ?? throw new InvalidOperationException("SettingsTool 尚未初始化");
        return ApplyDraft(requested, () => host.Hotkeys.ReRegister(host.BuildHotkeyRequests()), SaveAllSections);
    }

    // 注册与持久化可在内部验证中替换，验证失败回滚时不调用 Win32 或用户数据目录。
    internal SettingsApplyResult ApplyDraft(SettingsDraft requested,
        Func<IReadOnlyList<string>> registerCurrent, Action saveSections)
    {
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
                registerCurrent);
            if (!hotkeyResult.Success)
            {
                RefreshPresentations();
                return hotkeyResult;
            }
        }

        try
        {
            ApplyRuntime(candidate);
            saveSections();
            RefreshPresentations();
            return SettingsApplyResult.Ok();
        }
        catch (Exception ex)
        {
            Diag.Log($"[设置页] 保存失败，恢复旧配置: {ex.GetType().Name}: {ex.Message}");
            var rollbackIssues = new List<string>();
            try { ApplyRuntime(previous); }
            catch (Exception rollbackEx) { rollbackIssues.Add($"恢复旧字段失败：{rollbackEx.Message}"); }
            if (hotkeysChanged)
            {
                try { rollbackIssues.AddRange(registerCurrent()); }
                catch (Exception rollbackEx) { rollbackIssues.Add($"恢复旧热键失败：{rollbackEx.Message}"); }
            }
            try { saveSections(); }
            catch (Exception rollbackEx) { rollbackIssues.Add($"旧设置回写失败：{rollbackEx.Message}"); }
            RefreshPresentations();
            var message = rollbackIssues.Count == 0
                ? $"设置保存失败，已恢复修改前配置：{ex.Message}"
                : $"设置保存失败，已尝试恢复修改前配置：{ex.Message}\n\n警告：恢复时也遇到问题：\n{string.Join("\n", rollbackIssues)}";
            if (rollbackIssues.Count > 0)
                Diag.Log("[设置页] 保存失败后的恢复异常: " + string.Join(" | ", rollbackIssues));
            return SettingsApplyResult.Fail(message);
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
        => ApplyDetectedTarget(targetProcess, SaveHostSection);

    internal void ApplyDetectedTarget(string targetProcess, Action saveSettings)
    {
        if (_host is null || string.IsNullOrWhiteSpace(targetProcess)) return;
        var previous = _host.Foreground.TargetProcess;
        _host.Foreground.TargetProcess = targetProcess.Trim();
        try { saveSettings(); }
        catch
        {
            _host.Foreground.TargetProcess = previous;
            _page?.RefreshFromTool(refreshLists: false);
            throw;
        }
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
        ToolSettingsPersistence.SaveSections(_host!.Storage, [this, _craft, _clicker, _keyLoop, _hideout]);
    }

    private void SaveHostSection()
    {
        ToolSettingsPersistence.SaveSections(_host!.Storage, [this]);
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

        _craft.HotkeyStart = HotkeySetting.Read(hotkeys, "start", SettingsDefaults.HotkeyStart);
        _craft.HotkeyStop = HotkeySetting.Read(hotkeys, "stop", SettingsDefaults.HotkeyStop);
        _host.CoordinateHotkey = HotkeySetting.Read(hotkeys, "coordinate", SettingsDefaults.HotkeySetCoord);
    }

    public void SaveSettings(Utf8JsonWriter writer) => CreateHostSection().WriteTo(writer);
}
