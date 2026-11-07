using ShiKe.Services;
using ShiKe.Services.Updates;
using System.Windows;

namespace ShiKe;

internal static class Bootstrapper
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { return Run(args); }
        finally
        {
            // 停止所有应用工作后再刷新日志。未使用日志的助手/拒绝入口不创建日志服务。
            if (!Diag.Shutdown(TimeSpan.FromMilliseconds(1500)))
                System.Diagnostics.Debug.WriteLine("[日志] 退出刷新未完成；不延长程序退出或改变更新结果。");
        }
    }

    private static int Run(string[] args)
    {
        UpdateStartup? updateStartup;
        try
        {
            if (UpdateBootstrap.TryRunHelper(args, out var exitCode, out var helperMessage))
            {
                if (args.Contains("--recover-update", StringComparer.Ordinal))
                {
                    var message = helperMessage ?? "恢复未能完成；请保留会话目录和备份。";
                    try { Console.Error.WriteLine(message); } catch { }
                    try { MessageBox.Show(message, "拾刻更新恢复", MessageBoxButton.OK,
                        exitCode == 3 ? MessageBoxImage.Information : MessageBoxImage.Error); }
                    catch { /* 不能让提示窗口故障覆盖更新器的退出结果。 */ }
                }
                return exitCode;
            }
            var acceptance = UpdateAcceptanceContext.LoadFromEnvironment();
            if (acceptance is not null && (args.Contains("--startup-profile", StringComparer.Ordinal) ||
                    args.Contains("--startup-lifecycle-check", StringComparer.Ordinal) ||
                    (acceptance.IsOrdinaryLaunch ? args.Length != 0 : args.Length != 3 || args[0] != "--update-start")))
                throw new InvalidDataException("更新验收参数不能与普通诊断或非本案启动混用");
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
            if (Environment.GetEnvironmentVariable(UpdateAcceptanceContext.CaseVariable) is not null ||
                Environment.GetEnvironmentVariable(UpdateAcceptanceContext.TokenVariable) is not null ||
                Environment.GetEnvironmentVariable(UpdateAcceptanceContext.StageVariable) is not null)
            {
                UpdateAcceptanceContext.Current?.Record("bootstrap-rejected", error.Message);
                UpdateAcceptanceContext.Current?.WriteExitReport(65, false, false);
                return 65; // 无效验收输入不能触碰日常data日志，也不能降级为普通启动。
            }
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
