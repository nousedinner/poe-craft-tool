using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ShiKe;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Tools.Clicker;
using ShiKe.Tools.Craft;
using ShiKe.Tools.KeyLoop;

internal static class ArchitectureBoundaryChecks
{
    public static void CoreIsIndependentOfDesktop()
    {
        var core = typeof(AffixEngine).Assembly;
        Require(core != typeof(ToolHost).Assembly, "业务核心必须是独立程序集");
        Require(typeof(ClickerEngine).Assembly == core && typeof(KeyLoopEngine).Assembly == core,
            "两种运行引擎必须实际位于核心，而不只是抽出模型");
        var forbidden = new[] { "拾刻", "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms" };
        Require(!core.GetReferencedAssemblies().Any(reference => forbidden.Contains(reference.Name)),
            "核心不得反向依赖桌面入口或WPF/WinForms");
        Require(NetworkService.CurrentVersion == typeof(Bootstrapper).Assembly.GetName().Version,
            "网络与更新版本必须来自桌面入口，不能跟随核心库版本");
    }

    public static void CheckDeclaredCount(MainWindow window, ToolRegistry registry, ToolHost host)
    {
        var navigation = (ListBox)window.FindName("ToolList");
        var previous = navigation.SelectedItem;
        var probe = new CountTool();
        registry.Register(probe);
        navigation.Items.Refresh();
        host.Statuses.Report(probe.Id, "虚拟工具已结束", false, 17);
        navigation.SelectedItem = probe;
        window.RefreshStatus();
        var count = (TextBlock)window.FindName("UseCountLabel");
        Require(count.Text == "已使用 17 次" && count.Visibility == Visibility.Visible,
            "新的计数工具必须无需宿主按ID特判即可显示次数");
        host.Statuses.Report(probe.Id, "虚拟工具不提供计数", false);
        window.RefreshStatus();
        Require(count.Visibility == Visibility.Collapsed, "没有声明计数时必须隐藏");
        navigation.SelectedItem = previous;
    }

    private sealed class CountTool : ITool
    {
        public string Id => "architecture-count-probe";
        public string Name => "虚拟计数工具";
        public string IconKey => "";
        public void Initialize(ToolHost host) { }
        public FrameworkElement CreatePage() => new Grid();
        public void OnActivate() { }
        public void OnDeactivate() { }
        public void OnShutdown() { }
        public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() => [];
        public void LoadSettings(JsonElement section) { }
        public void SaveSettings(Utf8JsonWriter writer) { writer.WriteStartObject(); writer.WriteEndObject(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
