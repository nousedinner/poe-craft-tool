namespace ShiKe.Host;

/// <summary>
/// 抽屉注册表：注册顺序即导航顺序。
/// 宿主零改动 = 新抽屉只需在此 Register 一行。
/// </summary>
public sealed class ToolRegistry
{
    private readonly List<ITool> _tools = [];

    public IReadOnlyList<ITool> Tools => _tools;

    public void Register(ITool tool) => _tools.Add(tool);
}
