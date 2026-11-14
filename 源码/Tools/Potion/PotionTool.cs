using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Potion;

/// <summary>按一次快捷键，依次轻按已选中的药剂槽各一次；不定时刷新。</summary>
public sealed class PotionTool : ITool, IEnableableTool, IToolRuntime,
    IEditableHotkeyProvider, IHotkeySlotConflictProvider
{
    public const int SlotCount = 5;
    public const int InterKeyDelayMs = 30;

    private readonly object _runGate = new();
    private readonly bool[] _enabledSlots = [true, true, true, true, true];
    private readonly string[] _slotKeys = ["1", "2", "3", "4", "5"];
    private readonly MissingTargetReminder _missingTargetReminder = new();
    private ToolHost? _host;
    private PotionPage? _page;
    private bool _enabled = SettingsDefaults.PotionEnabled;
    private string _hotkey = SettingsDefaults.PotionHotkey;
    private bool _running;
    private CancellationTokenSource? _runCts;
    private bool _hotkeysPausedForCapture;

    public string Id => "potion";
    public string Name => "一键吃药";
    public string IconKey => "potion";
    public bool IsEnabled => _enabled;
    public bool IsRunning { get { lock (_runGate) return _running; } }
    public bool ShutdownCompleted => !IsRunning;
    public string Hotkey { get => _hotkey; set => _hotkey = value?.Trim() ?? string.Empty; }

    void IToolRuntime.RequestStop() => Stop();
    bool IHotkeySlotConflictProvider.IsCapturingHotkeySlot => _hotkeysPausedForCapture;
    IReadOnlyList<(string Name, string Key)> IHotkeySlotConflictProvider.GetEnabledHotkeySlots()
        => GetSelectedSlots();
    void IEditableHotkeyProvider.RefreshSettingsPresentation() => _page?.RefreshHotkeyPresentation();

    public void Initialize(ToolHost host) => _host = host;

    public FrameworkElement CreatePage() => _page ??= new PotionPage(this);

    public void OnActivate() { }
    public void OnDeactivate() => _page?.CancelCapture(interactive: false);
    public void OnShutdown() => Stop();

    public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() =>
    [
        new HotkeyRequest
        {
            Key = Hotkey,
            DisplayName = "一键吃药",
            CheckForeground = true,
            Mode = HotkeyMode.Toggle,
            Handler = StartSequence,
            ForegroundRejectedHandler = NotifyMissingTarget,
        },
    ];

    public IReadOnlyList<EditableHotkey> GetEditableHotkeys() =>
    [
        new("potion_combo", "一键吃药", HotkeyMode.Toggle, SettingsDefaults.PotionHotkey,
            () => Hotkey, key => Hotkey = key, 80),
    ];

    public bool IsSlotEnabled(int index)
        => index is >= 0 and < SlotCount && _enabledSlots[index];

    public string SlotKey(int index)
        => index is >= 0 and < SlotCount ? _slotKeys[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public void SetSlotEnabled(int index, bool enabled)
        => UpdateSlot(index, enabled, key: null);

    public void SetSlotKey(int index, string key)
        => UpdateSlot(index, enabled: null, key);

    private void UpdateSlot(int index, bool? enabled, string? key)
    {
        if (index is < 0 or >= SlotCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (IsRunning) throw new InvalidOperationException("一键吃药运行期间不能修改药剂槽位");
        var nextEnabled = enabled ?? _enabledSlots[index];
        var nextKey = key is null ? _slotKeys[index] : key.Trim();
        if (nextEnabled && string.IsNullOrWhiteSpace(nextKey))
            throw new InvalidOperationException($"药剂槽 {index + 1} 已启用，但尚未设置按键。");
        if (!string.IsNullOrWhiteSpace(nextKey) && KeyCode.Parse(nextKey) == 0 && !MouseButtonKey.TryParse(nextKey, out _))
            throw new InvalidOperationException($"药剂槽 {index + 1} 的按键“{nextKey}”无法识别。");
        if (_enabledSlots[index] == nextEnabled && string.Equals(_slotKeys[index], nextKey, StringComparison.Ordinal)) return;

        var previousEnabled = _enabledSlots[index];
        var previousKey = _slotKeys[index];
        _enabledSlots[index] = nextEnabled;
        _slotKeys[index] = nextKey;
        try
        {
            if (_host is { } host)
            {
                var providers = host.RegisteredTools.OfType<IHotkeySlotConflictProvider>().ToArray();
                if (providers.Any(provider => !ReferenceEquals(provider, this) && provider.IsCapturingHotkeySlot))
                    throw new InvalidOperationException("正在录制输入按键，请先完成或取消录制后再修改一键吃药设置。");
                if (_enabled && ValidateSelectedSlotValues() is { } slotError)
                    throw new InvalidOperationException(slotError);
                if (_enabled && ValidateActiveSlotBindings(host) is { } activeConflict)
                    throw new InvalidOperationException(activeConflict);
                if (key is not null && (!_enabled || !nextEnabled) && !string.IsNullOrWhiteSpace(nextKey) &&
                    ValidateSlotKey(index, nextKey) is { } candidateConflict)
                {
                    throw new InvalidOperationException(candidateConflict);
                }
            }
            SaveSettingsToStorage();
        }
        catch
        {
            _enabledSlots[index] = previousEnabled;
            _slotKeys[index] = previousKey;
            throw;
        }
    }

    public ToolEnablementResult SetEnabled(bool enabled)
    {
        if (_host is null) return ToolEnablementResult.Fail("一键吃药尚未初始化");
        if (enabled && string.IsNullOrWhiteSpace(Hotkey))
            return ToolEnablementResult.Fail("请先在设置中绑定一键吃药快捷键");
        if (enabled && ValidateSelectedSlotValues() is { } slotError)
            return ToolEnablementResult.Fail(slotError);

        var result = ToolEnablement.TryApply(_host, this, enabled,
            value => _enabled = value, Stop, SaveSettingsToStorage);
        if (result.Success) _page?.RefreshEnabledPresentation();
        return result;
    }

    public void Stop()
    {
        lock (_runGate) _runCts?.Cancel();
    }

    public void LoadSettings(JsonElement section)
    {
        if (section.TryGetProperty("enabled", out var enabled) &&
            enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _enabled = enabled.GetBoolean();
        Hotkey = HotkeySetting.Read(section, "hotkey", SettingsDefaults.PotionHotkey);

        if (!section.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Array) return;
        Array.Clear(_enabledSlots, 0, _enabledSlots.Length);
        var index = 0;
        foreach (var item in slots.EnumerateArray())
        {
            if (index >= SlotCount) break;
            if (item.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                // 兼容本地未发布预览版的 bool 槽位格式。
                _enabledSlots[index] = item.GetBoolean();
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("enabled", out var slotEnabled) &&
                    slotEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    _enabledSlots[index] = slotEnabled.GetBoolean();
                if (item.TryGetProperty("key", out var slotKey) && slotKey.ValueKind == JsonValueKind.String)
                    _slotKeys[index] = slotKey.GetString()?.Trim() ?? _slotKeys[index];
            }
            index++;
        }
    }

    public void SaveSettings(Utf8JsonWriter writer) => CreateSettingsNode().WriteTo(writer);

    private JsonObject CreateSettingsNode()
    {
        var slots = new JsonArray();
        for (var index = 0; index < SlotCount; index++)
            slots.Add(new JsonObject { ["enabled"] = _enabledSlots[index], ["key"] = _slotKeys[index] });
        return new JsonObject
        {
            ["enabled"] = _enabled,
            ["hotkey"] = Hotkey,
            ["slots"] = slots,
        };
    }

    private void SaveSettingsToStorage()
    {
        if (_host is null) return;
        _host.Storage.UpdateSettings(settings => settings[Id] = CreateSettingsNode());
    }

    private (string Name, string Key)[] GetSelectedSlots()
        => Enumerable.Range(0, SlotCount)
            .Where(index => _enabledSlots[index])
            .Select(index => ($"药剂槽 {index + 1}", _slotKeys[index]))
            .ToArray();

    internal string? TryBeginSlotCapture()
    {
        if (_host is null) return "一键吃药尚未初始化";
        if (_hotkeysPausedForCapture) return null;
        if (IsRunning) return "一键吃药正在执行，请完成后再设置药剂按键";
        if (_host.RegisteredTools.OfType<IHotkeySlotConflictProvider>()
            .Any(provider => !ReferenceEquals(provider, this) && provider.IsCapturingHotkeySlot))
            return "另一个输入槽位正在录制，请先完成或取消录制后重试";
        if (ToolCapabilities.RunningToolsMessage(_host, "设置药剂按键", Id) is { } running)
            return running;

        _host.Hotkeys.UnregisterAll();
        _hotkeysPausedForCapture = true;
        Diag.Log("[一键吃药] 槽位录制开始：已临时暂停全局热键");
        return null;
    }

    internal IReadOnlyList<string> EndSlotCapture(bool notify = true)
    {
        if (_host is null || !_hotkeysPausedForCapture) return [];

        List<string> issues;
        try
        {
            issues = _host.Hotkeys.ReRegister(_host.BuildHotkeyRequests());
            _hotkeysPausedForCapture = false;
        }
        catch (Exception error)
        {
            Diag.Log($"[一键吃药] 槽位录制结束但恢复热键抛出异常：{error.GetType().Name}: {error.Message}");
            issues = [$"恢复热键时发生异常：{error.Message}"];
        }

        Diag.Log(issues.Count == 0
            ? "[一键吃药] 槽位录制结束：已恢复全局热键"
            : "[一键吃药] 槽位录制结束但恢复异常：" + string.Join(" | ", issues));
        if (notify && issues.Count > 0)
            _host.Notification.ShowError("槽位录制结束后恢复热键失败：\n" + string.Join("\n", issues));
        return issues;
    }

    private string? ValidateSlotKey(int index, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || _host is null) return null;
        var slots = _host.RegisteredTools
            .Where(tool => tool is not IEnableableTool enableable || enableable.IsEnabled)
            .OfType<IHotkeySlotConflictProvider>()
            .Where(provider => !ReferenceEquals(provider, this))
            .SelectMany(provider => provider.GetEnabledHotkeySlots())
            .ToList();
        for (var slotIndex = 0; slotIndex < SlotCount; slotIndex++)
        {
            if (slotIndex != index && !_enabledSlots[slotIndex]) continue;
            var slotKey = slotIndex == index ? key : _slotKeys[slotIndex];
            if (!string.IsNullOrWhiteSpace(slotKey)) slots.Add(($"药剂槽 {slotIndex + 1}", slotKey));
        }

        var hotkeys = _host.BuildHotkeyRequests()
            .Where(request => !string.IsNullOrWhiteSpace(request.Key))
            .Select(request => (request.DisplayName, request.Key, request.Mode))
            .ToArray();
        return HotkeyConflictRules.ValidateSlotKeyConflicts(hotkeys, slots).FirstOrDefault();
    }

    private string? ValidateSelectedSlotValues()
    {
        if (!_enabledSlots.Any(slot => slot)) return "请至少选择一个药剂槽位";
        for (var index = 0; index < SlotCount; index++)
        {
            if (!_enabledSlots[index]) continue;
            if (string.IsNullOrWhiteSpace(_slotKeys[index]))
                return $"药剂槽 {index + 1} 已启用，但尚未设置按键";
            if (KeyCode.Parse(_slotKeys[index]) == 0 && !MouseButtonKey.TryParse(_slotKeys[index], out _))
                return $"药剂槽 {index + 1} 的按键“{_slotKeys[index]}”无法识别";
        }
        return null;
    }

    private string? ValidateActiveSlotBindings(ToolHost host)
    {
        var slots = host.RegisteredTools
            .Where(tool => tool is not IEnableableTool enableable || enableable.IsEnabled)
            .OfType<IHotkeySlotConflictProvider>()
            .SelectMany(provider => provider.GetEnabledHotkeySlots())
            .ToArray();
        var hotkeys = host.BuildHotkeyRequests()
            .Where(request => !string.IsNullOrWhiteSpace(request.Key))
            .Select(request => (request.DisplayName, request.Key, request.Mode))
            .ToArray();
        return HotkeyConflictRules.ValidateSlotKeyConflicts(hotkeys, slots).FirstOrDefault();
    }

    private void StartSequence()
    {
        var host = _host;
        if (host is null || !_enabled) return;
        if (ValidateSelectedSlotValues() is { } slotError)
        {
            Diag.Log($"[一键吃药] 启动被拒绝：{slotError}");
            host.Notification.ShowError(slotError);
            return;
        }
        var keys = GetSelectedSlots().Select(slot => slot.Key).ToArray();
        if (ValidateActiveSlotBindings(host) is { } conflict)
        {
            Diag.Log($"[一键吃药] 启动被拒绝：{conflict}");
            host.Notification.ShowError(conflict);
            return;
        }
        if (!host.Foreground.IsTargetForeground())
        {
            NotifyMissingTarget();
            return;
        }
        if (host.EmergencyCts.IsCancellationRequested) host.ResetEmergencyStop();

        CancellationTokenSource runCts;
        lock (_runGate)
        {
            if (_running) return;
            _running = true;
            runCts = CancellationTokenSource.CreateLinkedTokenSource(host.EmergencyCts.Token);
            _runCts = runCts;
        }

        _page?.RefreshRunningPresentation();
        _ = RunSequenceAsync(host, keys, runCts);
    }

    private async Task RunSequenceAsync(ToolHost host, string[] keys, CancellationTokenSource runCts)
    {
        var sent = 0;
        try
        {
            for (var index = 0; index < keys.Length; index++)
            {
                runCts.Token.ThrowIfCancellationRequested();
                if (!host.Foreground.IsTargetForeground())
                {
                    host.Notification.Show("游戏失去前台，一键吃药已停止");
                    break;
                }

                await host.Input.PressAndReleaseAsync(keys[index], runCts.Token);
                sent++;
                if (index < keys.Length - 1)
                    await Task.Delay(InterKeyDelayMs, runCts.Token);
            }
            Diag.Log($"[一键吃药] 本次完成：已发送 {sent}/{keys.Length} 个药剂键");
        }
        catch (OperationCanceledException)
        {
            Diag.Log($"[一键吃药] 本次停止：已发送 {sent}/{keys.Length} 个药剂键");
        }
        catch (Exception error)
        {
            Diag.Log($"[一键吃药] 输入失败：{error.GetType().Name}: {error.Message}");
            host.Notification.ShowError($"一键吃药执行失败：{error.Message}");
        }
        finally
        {
            lock (_runGate)
            {
                if (ReferenceEquals(_runCts, runCts))
                {
                    _runCts = null;
                    _running = false;
                }
            }
            runCts.Dispose();
            _page?.RefreshRunningPresentation();
        }
    }

    private void NotifyMissingTarget()
    {
        if (_host is null) return;
        _missingTargetReminder.ShowIfNeeded(_enabled, _host.Foreground.TargetProcess,
            () => _host.Notification.Show("尚未锁定游戏进程，请先在设置中选择目标进程"));
    }
}
