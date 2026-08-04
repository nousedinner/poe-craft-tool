using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Hideout;

/// <summary>
/// 一键回城抽屉（阶段4 实装）。
/// - 热键声明：F2（Toggle, CheckForeground=true 由抽屉自查——对齐 CraftTool 模式）
/// - 行为：对齐 Python _execute_hideout（main.py:26-35）：
///   Enter → sleep(0.1s) → 输入 /hideout → Enter
/// - 存储：hideout 节 {enabled, hotkey}，默认 false / F2（storage.py:123-124）
/// </summary>
public sealed class HideoutTool : ITool
{
    private ToolHost? _host;
    private bool _enabled;
    private string _hotkey = "F2";

    public string Id => "hideout";
    public string Name => "一键回城";
    public string IconKey => "hideout";

    public bool IsEnabled => _enabled;
    public string Hotkey => _hotkey;

    public void Initialize(ToolHost host)
    {
        _host = host;
        try
        {
            var settings = host.Storage.LoadSettings();
            if (settings["hideout"] is JsonObject section)
            {
                if (section["enabled"] is JsonValue e && e.TryGetValue<bool>(out var en))
                    _enabled = en;
                if (section["hotkey"] is JsonValue h && h.TryGetValue<string>(out var hk) &&
                    !string.IsNullOrWhiteSpace(hk))
                    _hotkey = hk;
            }
        }
        catch (Exception)
        {
            // 设置损坏按默认值
        }
    }

    public FrameworkElement CreatePage() => new HideoutPage(this);

    public void OnActivate() { }

    public void OnDeactivate() { }

    public void OnShutdown() { }

    /// <summary>F2 一键回城（Toggle；CheckForeground 标志由抽屉自查）。</summary>
    public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() => [new HotkeyRequest
    {
        Key = _hotkey,
        DisplayName = "一键回城",
        CheckForeground = true,
        Mode = HotkeyMode.Toggle,
        Handler = OnHotkey,
    }];

    private void OnHotkey()
    {
        if (_host is null || !_enabled)
        {
            Diag.Log($"[回城] OnHotkey: 忽略, host={_host != null}, enabled={_enabled}");
            return;
        }

        // 前台检查（对齐 Python _check_foreground：目标进程不在前台 → 静默忽略，不弹窗）
        if (!string.IsNullOrEmpty(_host.Foreground.TargetProcess) &&
            !_host.Foreground.IsTargetForeground())
        {
            var current = ForegroundDetector.GetForegroundProcessName();
            Diag.Log($"[回城] OnHotkey: 前台检查失败, TargetProcess={_host.Foreground.TargetProcess}, 当前前台={current ?? "(null)"}");
            return;
        }

        Diag.Log("[回城] OnHotkey: 执行回城");
        _ = ExecuteHideoutAsync();
    }

    /// <summary>执行一键回城：Enter → 0.1s → /hideout → Enter（对齐 Python _execute_hideout，静默失败）。</summary>
    private async Task ExecuteHideoutAsync()
    {
        try
        {
            var input = _host!.Input;
            input.PressAndRelease("enter"); // 打开聊天框
            await Task.Delay(100);
            await input.TypeTextAsync("/hideout", CancellationToken.None);
            input.PressAndRelease("enter"); // 发送
        }
        catch (Exception)
        {
            // 对齐 Python except: pass（失败静默）
        }
    }

    /// <summary>页面开关调用：更新启用状态并立即保存。</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        SaveNow();
    }

    private void SaveNow()
    {
        if (_host is null) return;
        var settings = _host.Storage.LoadSettings();
        settings["hideout"] = new JsonObject
        {
            ["enabled"] = _enabled,
            ["hotkey"] = _hotkey,
        };
        _host.Storage.SaveSettings(settings);
    }

    public void LoadSettings(JsonElement section)
    {
        if (section.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True)
            _enabled = true;
        if (section.TryGetProperty("hotkey", out var h) && h.ValueKind == JsonValueKind.String)
        {
            var hk = h.GetString();
            if (!string.IsNullOrWhiteSpace(hk))
                _hotkey = hk;
        }
    }

    public void SaveSettings(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", _enabled);
        writer.WriteString("hotkey", _hotkey);
        writer.WriteEndObject();
    }
}
