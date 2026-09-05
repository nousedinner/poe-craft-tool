using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Hideout;

/// <summary>
/// 一键回城抽屉（阶段4 实装）。
/// - 热键声明：F2（Toggle, CheckForeground=true，由公共热键层拦截并在抽屉内二次防护）
/// - 行为：对齐 Python _execute_hideout（main.py:26-35）：
///   Enter → sleep(0.1s) → 输入 /hideout → Enter
/// - 存储：hideout 节 {enabled, hotkey}，默认 false / F2（storage.py:123-124）
/// </summary>
public sealed class HideoutTool : ITool
{
    private ToolHost? _host;
    private bool _enabled;
    private string _hotkey = "F2";
    private string _command = SettingsDefaults.HideoutCommand;
    private readonly object _operationGate = new();
    private CancellationTokenSource? _operationCts;
    private Task? _operationTask;

    public string Id => "hideout";
    public string Name => "一键回城";
    public string IconKey => "hideout";

    public bool IsEnabled => _enabled;
    public string Hotkey => _hotkey;
    public string Command => _command;

    public void Initialize(ToolHost host)
    {
        _host = host;
    }

    public FrameworkElement CreatePage() => new HideoutPage(this);

    public void OnActivate() { }

    public void OnDeactivate() { }

    public void OnShutdown()
    {
        Task? task;
        lock (_operationGate)
        {
            _operationCts?.Cancel();
            task = _operationTask;
        }
        if (task is null) return;
        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(1)))
                Diag.Log("[回城] OnShutdown: 1 秒内未完成关闭");
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
    }

    /// <summary>F2 一键回城（Toggle；公共热键层检查前台，抽屉保留二次防护）。</summary>
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
        if (string.IsNullOrWhiteSpace(_host.Foreground.TargetProcess))
        {
            Diag.Log("[回城] OnHotkey: 未配置目标进程，静默忽略");
            return;
        }
        if (!_host.Foreground.IsTargetForeground())
        {
            var current = ForegroundDetector.GetForegroundProcessName();
            Diag.Log($"[回城] OnHotkey: 前台检查失败, TargetProcess={_host.Foreground.TargetProcess}, 当前前台={current ?? "(null)"}");
            return;
        }

        Diag.Log("[回城] OnHotkey: 执行回城");
        lock (_operationGate)
        {
            if (_operationTask is { IsCompleted: false })
            {
                Diag.Log("[回城] OnHotkey: 上一次操作仍在执行，忽略重复触发");
                return;
            }
            _operationCts?.Dispose();
            _operationCts = CancellationTokenSource.CreateLinkedTokenSource(_host.EmergencyCts.Token);
            _operationTask = ExecuteHideoutAsync(_command, _operationCts.Token);
        }
    }

    /// <summary>执行一键回城：Enter → 0.1s → /hideout → Enter（对齐 Python _execute_hideout，静默失败）。</summary>
    private async Task ExecuteHideoutAsync(string command, CancellationToken token)
    {
        try
        {
            var input = _host!.Input;
            token.ThrowIfCancellationRequested();
            input.PressAndRelease("enter"); // 打开聊天框
            await Task.Delay(100, token);
            await input.TypeTextAsync(command, token);
            token.ThrowIfCancellationRequested();
            input.PressAndRelease("enter"); // 发送
        }
        catch (OperationCanceledException)
        {
            Diag.Log("[回城] ExecuteHideout: 已取消");
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

    public void SetCommand(string command)
    {
        _command = string.IsNullOrWhiteSpace(command) ? SettingsDefaults.HideoutCommand : command.Trim();
        SaveNow();
    }

    private void SaveNow()
    {
        if (_host is null) return;
        _host.Storage.UpdateSettings(settings => settings["hideout"] = new JsonObject
        {
            ["enabled"] = _enabled,
            ["hotkey"] = _hotkey,
            ["command"] = _command,
        });
    }

    public void LoadSettings(JsonElement section)
    {
        if (section.TryGetProperty("enabled", out var e) &&
            (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False))
            _enabled = e.GetBoolean();
        if (section.TryGetProperty("hotkey", out var h) && h.ValueKind == JsonValueKind.String)
        {
            var hk = h.GetString();
            if (!string.IsNullOrWhiteSpace(hk))
                _hotkey = hk;
        }
        if (section.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(command.GetString()))
            _command = command.GetString()!.Trim();
    }

    public void SaveSettings(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", _enabled);
        writer.WriteString("hotkey", _hotkey);
        writer.WriteString("command", _command);
        writer.WriteEndObject();
    }
}
