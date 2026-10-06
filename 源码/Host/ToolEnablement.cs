using ShiKe.Services;

namespace ShiKe.Host;

public sealed record ToolEnablementResult(bool Success, string Message)
{
    public static ToolEnablementResult Ok(string message) => new(true, message);
    public static ToolEnablementResult Fail(string message) => new(false, message);
}

/// <summary>
/// 工具启用事务：安全校验、热键注册和原子保存全部成功后才报告成功；失败恢复旧启用配置。
/// </summary>
public static class ToolEnablement
{
    public static ToolEnablementResult TryApply(
        ToolHost host,
        ITool tool,
        bool enabled,
        Action<bool> applyState,
        Action stopWhenDisabled,
        Action saveSettings)
        => TryApply(tool, enabled, applyState, stopWhenDisabled, saveSettings,
            () => ValidateCompatibility(host.RegisteredTools),
            () => host.Hotkeys.ReRegister(host.BuildHotkeyRequests()));

    // 内部回归替换注册/保存操作，不创建宿主或发送真实输入。
    internal static ToolEnablementResult TryApply(
        ITool tool,
        bool enabled,
        Action<bool> applyState,
        Action stopWhenDisabled,
        Action saveSettings,
        Func<string?> validateCompatibility,
        Func<IReadOnlyList<string>> registerCurrent)
    {
        if (tool is not IEnableableTool enableable)
            return ToolEnablementResult.Fail($"{tool.Name}不支持启用状态");
        if (enableable.IsEnabled == enabled)
            return ToolEnablementResult.Ok(enabled ? $"已启用{tool.Name}" : $"已停用{tool.Name}");

        var previous = enableable.IsEnabled;
        var registrationAttempted = false;
        var stopped = false;
        string failure;
        try
        {
            applyState(enabled);
            var compatibilityError = validateCompatibility();
            if (compatibilityError is not null)
            {
                failure = compatibilityError;
            }
            else
            {
                registrationAttempted = true;
                var issues = registerCurrent();
                if (issues.Count > 0)
                {
                    failure = string.Join("\n", issues);
                }
                else
                {
                    if (!enabled)
                    {
                        stopWhenDisabled();
                        stopped = true;
                    }
                    saveSettings();
                    return ToolEnablementResult.Ok(enabled ? $"已启用{tool.Name}" : $"已停用{tool.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            Diag.Log($"[启用状态] {tool.Name}应用失败: {ex}");
        }

        var rollbackIssues = new List<string>();
        try { applyState(previous); }
        catch (Exception ex) { rollbackIssues.Add($"恢复原启用状态失败：{ex.Message}"); }
        if (registrationAttempted)
        {
            try { rollbackIssues.AddRange(registerCurrent()); }
            catch (Exception ex) { rollbackIssues.Add($"恢复原热键时发生异常：{ex.Message}"); }
        }
        var recovery = rollbackIssues.Count == 0 ? "已恢复原启用配置" : "已尝试恢复原启用配置";
        var message = $"无法{(enabled ? "启用" : "停用")}{tool.Name}，{recovery}：\n{failure}";
        if (stopped) message += "\n本次已停止的操作不会自动重新启动。";
        if (rollbackIssues.Count > 0)
        {
            message += "\n\n恢复时也遇到问题：\n" + string.Join("\n", rollbackIssues);
            Diag.Log($"[启用状态] {tool.Name}恢复失败: {string.Join(" | ", rollbackIssues)}");
        }
        return ToolEnablementResult.Fail(message);
    }

    /// <summary>
    /// Craft 会发送 Ctrl+Alt+C；当连点器的按住热键本身就是 Ctrl 时，两者不得同时启用。
    /// </summary>
    public static string? ValidateCompatibility(IReadOnlyList<ITool> tools)
    {
        var craftEnabled = IsEnabled(tools, "craft");
        var clickerEnabled = IsEnabled(tools, "clicker");
        if (!craftEnabled || !clickerEnabled) return null;

        var clicker = tools.FirstOrDefault(tool => tool.Id == "clicker");
        var hold = clicker?.GetHotkeyRequests().FirstOrDefault(request => request.Mode == HotkeyMode.Hold);
        var parsed = hold is null ? null : HotkeyParser.ParseHold(hold.Key);
        if (parsed is { Modifiers: 0, Vk: KeyCode.Control })
            return "连点器按住热键为 Ctrl 时，连点器与洗词缀不能同时启用。请先停用其中一个功能，或修改连点器按住热键。";

        return null;
    }

    private static bool IsEnabled(IEnumerable<ITool> tools, string id)
    {
        var tool = tools.FirstOrDefault(candidate => candidate.Id == id);
        return tool switch
        {
            IEnableableTool enableable => enableable.IsEnabled,
            not null => true,
            _ => false,
        };
    }
}
