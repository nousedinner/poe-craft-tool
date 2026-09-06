using ShiKe.Services;

namespace ShiKe.Host;

public sealed record ToolEnablementResult(bool Success, string Message)
{
    public static ToolEnablementResult Ok(string message) => new(true, message);
    public static ToolEnablementResult Fail(string message) => new(false, message);
}

/// <summary>
/// 工具启用状态的轻量事务：先验证跨工具安全约束，再统一重注册热键；失败时恢复旧状态。
/// </summary>
public static class ToolEnablement
{
    public static ToolEnablementResult TryApply(
        ToolHost host,
        ITool tool,
        bool enabled,
        Action<bool> applyState,
        Action stopWhenDisabled)
    {
        if (tool is not IEnableableTool enableable)
            return ToolEnablementResult.Fail($"{tool.Name}不支持启用状态");
        if (enableable.IsEnabled == enabled)
            return ToolEnablementResult.Ok(enabled ? $"已启用{tool.Name}" : $"已停用{tool.Name}");

        var previous = enableable.IsEnabled;
        applyState(enabled);

        var compatibilityError = ValidateCompatibility(host.RegisteredTools);
        if (compatibilityError is not null)
        {
            applyState(previous);
            return ToolEnablementResult.Fail(compatibilityError);
        }

        IReadOnlyList<string> issues;
        try
        {
            issues = host.Hotkeys.ReRegister(host.BuildHotkeyRequests());
        }
        catch (Exception ex)
        {
            issues = [$"热键重新注册发生异常：{ex.Message}"];
        }
        if (issues.Count > 0)
        {
            applyState(previous);
            IReadOnlyList<string> rollbackIssues;
            try
            {
                rollbackIssues = host.Hotkeys.ReRegister(host.BuildHotkeyRequests());
            }
            catch (Exception ex)
            {
                rollbackIssues = [$"恢复原热键时发生异常：{ex.Message}"];
            }
            var message = $"无法{(enabled ? "启用" : "停用")}{tool.Name}，已恢复原状态：\n" +
                          string.Join("\n", issues);
            if (rollbackIssues.Count > 0)
                message += "\n\n恢复原热键时也遇到问题：\n" + string.Join("\n", rollbackIssues);
            return ToolEnablementResult.Fail(message);
        }

        if (!enabled) stopWhenDisabled();
        return ToolEnablementResult.Ok(enabled ? $"已启用{tool.Name}" : $"已停用{tool.Name}");
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
