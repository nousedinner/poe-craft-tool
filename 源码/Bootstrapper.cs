using ShiKe.Services;
using ShiKe.Services.Updates;
using System.Windows;

namespace ShiKe;

internal static class Bootstrapper
{
    [STAThread]
    public static int Main(string[] args)
    {
        UpdateStartup? updateStartup;
        try
        {
            if (UpdateBootstrap.TryRunHelper(args, out var exitCode)) return exitCode;
            updateStartup = UpdateBootstrap.ReadStartup(args);
            if (updateStartup is null && !args.Contains("--startup-profile", StringComparer.Ordinal) &&
                UpdateBootstrap.IsUpdateBusy(Environment.ProcessPath!))
            {
                MessageBox.Show("拾刻正在更新，请等待更新完成。", "拾刻更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }
            if (updateStartup is null && !args.Contains("--startup-profile", StringComparer.Ordinal))
                UpdateBootstrap.EnsureNoIncompleteTransaction(Environment.ProcessPath!);
        }
        catch (Exception error)
        {
            Diag.Log($"[更新入口] 拒绝无效更新启动：{error}");
            if (args.Length == 0)
                MessageBox.Show(error.Message, "拾刻更新未完成", MessageBoxButton.OK, MessageBoxImage.Error);
            return 65;
        }
        var startup = new StartupPerformance();
        var profileRequested = args.Contains("--startup-profile", StringComparer.Ordinal);
        var lifecycleCheck = args.Contains("--startup-lifecycle-check", StringComparer.Ordinal);
        // 诊断副本不抢占日常实例、热键或网络；日常入口不能用参数绕过单实例。
        if ((lifecycleCheck && !profileRequested) ||
            (profileRequested && !StartupPerformance.IsIsolatedProfileDirectory(AppContext.BaseDirectory)))
            return 64;

        var application = new App(startup, profileRequested, lifecycleCheck, updateStartup);
        startup.Mark("Application 创建完成");
        application.InitializeComponent();
        startup.Mark("主题与应用资源加载完成");
        return application.Run();
    }
}
