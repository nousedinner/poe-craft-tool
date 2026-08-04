using System.Windows;
using ShiKe.Host;
using ShiKe.Tools.Hideout;

namespace ShiKe;

public partial class App : Application
{
    private ToolHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 组装：注册表 → 注册抽屉 → 宿主 → 初始化各抽屉
        var registry = new ToolRegistry();
        registry.Register(new HideoutTool());

        _host = new ToolHost(registry);

        foreach (var tool in registry.Tools)
            tool.Initialize(_host);

        var window = new MainWindow(registry, _host);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出清理：各抽屉 OnShutdown（阶段2起：停线程 → 保存设置 → 注销热键）
        if (_host != null)
        {
            foreach (var tool in _host.RegisteredTools)
                tool.OnShutdown();
            _host.EmergencyCts.Cancel();
        }

        base.OnExit(e);
    }
}
