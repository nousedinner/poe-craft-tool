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
        var probe = new CountTool();
        registry.Register(probe);
        // 2026-11-13：底栏只保留「锁定状态」一行（状态圆点、使用次数、工具运行状态已移除——
        // 游戏在前台时本窗口被遮住，常驻状态看不到）。状态仍写入 ToolStatusStore，
        // 存储与上报逻辑保留，计数边界改在存储层验证。
        host.Statuses.Report(probe.Id, "虚拟工具已结束", false, 17);
        var (counted, _) = host.Statuses.Read(probe.Id);
        Require(counted is { Text: "虚拟工具已结束", Running: false, UseCount: 17 },
            "新的计数工具必须无需宿主按ID特判即可上报次数");
        host.Statuses.Report(probe.Id, "虚拟工具不提供计数", false);
        var (uncounted, _) = host.Statuses.Read(probe.Id);
        Require(uncounted is { Text: "虚拟工具不提供计数" } && uncounted.UseCount is null,
            "没有声明计数时必须保持无计数");
        Require(window.FindName("UseCountLabel") is null && window.FindName("StatusDot") is null,
            "底栏不应再显示使用次数与状态圆点");
        Require(window.FindName("StatusText") is not null, "底栏应保留锁定状态一行");
    }

    private sealed class CountTool : ITool, IPassiveTool
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
