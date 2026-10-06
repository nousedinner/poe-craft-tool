using ShiKe.Services;

namespace ShiKe;

internal static class Bootstrapper
{
    [STAThread]
    public static int Main(string[] args)
    {
        var startup = new StartupPerformance();
        var profileRequested = args.Contains("--startup-profile", StringComparer.Ordinal);
        var lifecycleCheck = args.Contains("--startup-lifecycle-check", StringComparer.Ordinal);
        // 诊断副本不抢占日常实例、热键或网络；日常入口不能用参数绕过单实例。
        if ((lifecycleCheck && !profileRequested) ||
            (profileRequested && !StartupPerformance.IsIsolatedProfileDirectory(AppContext.BaseDirectory)))
            return 64;

        var application = new App(startup, profileRequested, lifecycleCheck);
        startup.Mark("Application 创建完成");
        application.InitializeComponent();
        startup.Mark("主题与应用资源加载完成");
        return application.Run();
    }
}
