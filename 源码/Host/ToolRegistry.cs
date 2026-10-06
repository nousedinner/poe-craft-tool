namespace ShiKe.Host;

/// <summary>
/// 抽屉注册表：注册顺序即导航顺序。
/// 宿主零改动 = 新抽屉只需在此 Register 一行。
/// </summary>
public sealed class ToolRegistry
{
    private readonly List<ITool> _tools = [];

    public IReadOnlyList<ITool> Tools { get; }

    public ToolRegistry() => Tools = _tools.AsReadOnly();

    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Id) || tool.Id != tool.Id.Trim())
            throw new ArgumentException("工具 ID 不能为空或带首尾空格", nameof(tool));
        if (_tools.Any(existing => string.Equals(existing.Id, tool.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"工具 ID“{tool.Id}”已注册");
        _tools.Add(tool);
    }
}
