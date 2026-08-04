using System.Windows;
using System.Windows.Controls;

namespace ShiKe.Host;

public partial class MainWindow : Window
{
    private readonly ToolRegistry _registry;
    private readonly ToolHost _host;
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    private ITool? _currentTool;

    public MainWindow(ToolRegistry registry, ToolHost host)
    {
        InitializeComponent();
        _registry = registry;
        _host = host;

        // 导航绑定注册表，注册顺序 = 导航顺序
        ToolList.ItemsSource = registry.Tools;
        ToolList.DisplayMemberPath = nameof(ITool.Name);
        ToolList.SelectedIndex = 0; // 默认选中第一个抽屉
    }

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is not ITool tool)
            return;

        // 切走通知
        _currentTool?.OnDeactivate();
        _currentTool = tool;

        // 懒加载：首次选中创建页面，后续复用缓存
        if (!_pages.TryGetValue(tool.Id, out var page))
        {
            page = tool.CreatePage();
            _pages[tool.Id] = page;
        }

        ToolContent.Content = page;
        tool.OnActivate();
    }
}
