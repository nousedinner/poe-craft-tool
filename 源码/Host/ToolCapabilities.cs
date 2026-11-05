namespace ShiKe.Host;

/// <summary>运行状态与实际关闭完成分开；停止请求不代表输入清理已经完成。</summary>
public interface IToolRuntime
{
    bool IsRunning { get; }
    bool ShutdownCompleted { get; }
    void RequestStop();
}

/// <summary>明确声明纯页面工具：没有需要停止或等待清理的后台自动操作。</summary>
public interface IPassiveTool { }

/// <summary>settings.json 之外的兼容数据；仅由宿主在配置已加载且允许写入时保存。</summary>
public interface IToolAuxiliaryState
{
    void SaveAuxiliaryState();
}

/// <summary>工具拥有字段与默认值，设置页只负责呈现和执行整组事务。</summary>
public sealed record EditableHotkey(
    string Id, string Name, HotkeyMode Mode, string DefaultKey,
    Func<string> Read, Action<string> Apply, int Order = 1000);

public interface IEditableHotkeyProvider
{
    IReadOnlyList<EditableHotkey> GetEditableHotkeys();
    void RefreshSettingsPresentation() { }
}

public static class ToolCapabilities
{
    public static bool HasActiveAutomation(IEnumerable<ITool> tools)
        => tools.Any(tool => (tool is IToolRuntime) == (tool is IPassiveTool) ||
                             tool is IToolRuntime runtime && runtime.IsRunning);

    public static bool AllShutdownCompleted(IEnumerable<ITool> tools)
        => tools.All(tool => tool switch
        {
            IToolRuntime when tool is IPassiveTool => false,
            IToolRuntime runtime => runtime.ShutdownCompleted,
            IPassiveTool => true,
            _ => false,
        });

    internal static void ValidateLifecycle(ITool tool)
    {
        if ((tool is IToolRuntime) == (tool is IPassiveTool))
            throw new InvalidOperationException($"工具“{tool.Name}”必须明确声明运行能力或纯页面能力，且只能选择一种");
    }

    public static IReadOnlyList<EditableHotkey> CollectEditableHotkeys(IEnumerable<ITool> tools)
    {
        var entries = tools.OfType<IEditableHotkeyProvider>()
            .SelectMany(tool => tool.GetEditableHotkeys()).OrderBy(entry => entry.Order).ToArray();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || entry.Id != entry.Id.Trim() || !ids.Add(entry.Id))
                throw new InvalidOperationException($"可编辑热键 ID 为空或重复：{entry.Id}");
        }
        return entries;
    }

    public static void SaveAuxiliaryStates(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools.OfType<IToolAuxiliaryState>()) tool.SaveAuxiliaryState();
    }
}
