using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Tools.KeyLoop;

/// <summary>
/// 槽位录制提交时保存失败（只读/占用）的路径：
/// 热键必须仍然恢复、保存异常必须继续向外抛、解除只读后能正常重试。
/// 页面提交时序在真实 WPF 对象上跑，热键注册用替换实现计数，不碰系统热键。
/// </summary>
internal static class KeyLoopCaptureChecks
{
    public static void SaveFailureStillRestoresHotkeys() => RunOnSta(() =>
    {
        // 只在进程里还没有 Application 时自建；已有时必须借用它的 Dispatcher（见 RunOnSta）。
        if (Application.Current is null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/拾刻;component/Themes/DefaultTheme.xaml", UriKind.Relative),
            });
        }

        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "keyloop-capture-" + Guid.NewGuid().ToString("N")));
        ToolHost? host = null;
        try
        {
            var registry = new ToolRegistry();
            host = new ToolHost(registry, new StorageService(directory));
            var keyLoop = new KeyLoopTool();
            registry.Register(keyLoop);
            keyLoop.Initialize(host);

            var registers = 0;
            host.Hotkeys.RegistrationOverride = _ => { registers++; return []; };

            var settingsPath = Path.Combine(directory, "settings.json");
            host.Storage.UpdateSettings(root => root["keyloop"] = new JsonObject { ["enabled"] = true });
            var original = File.ReadAllText(settingsPath);
            var attributes = File.GetAttributes(settingsPath);

            Require(keyLoop.TryBeginSlotCapture() is null, "没有其他工具运行时应允许开始录制");
            var page = new KeyLoopPage(keyLoop);
            var commit = typeof(KeyLoopPage).GetMethod("CommitCapturedKey", BindingFlags.Instance | BindingFlags.NonPublic)!;

            try
            {
                File.SetAttributes(settingsPath, attributes | FileAttributes.ReadOnly);
                var failure = Catch(() => commit.Invoke(page, [0, "q"]));

                Require(failure is StorageException,
                    $"保存失败必须继续向外抛 StorageException，实际：{failure?.GetType().Name ?? "(未抛异常)"}");
                Require(registers == 1, "保存失败后必须仍然重注册一次热键");
                Require(!keyLoop.HotkeysPausedForCapture, "保存失败后暂停标志必须解除");
                Require(File.ReadAllText(settingsPath) == original, "失败时不得破坏原配置文件");
            }
            finally { File.SetAttributes(settingsPath, attributes); }

            // 解除只读后再走一遍完整录制：失败那次已经把热键恢复，重试必须能再来一轮。
            Require(keyLoop.TryBeginSlotCapture() is null, "解除只读后必须能重新开始录制");
            commit.Invoke(page, [0, "q"]);
            Require(registers == 2, "重试提交必须再重注册一次热键");
            Require(File.ReadAllText(settingsPath) != original, "正常提交必须真实落盘");
            Require(!keyLoop.HotkeysPausedForCapture, "正常提交后暂停标志也必须解除");
        }
        finally
        {
            if (host is not null)
            {
                host.Hotkeys.UnregisterAll();
                host.Sound.Dispose();
                host.Notification.Dispose();
                host.Network.Dispose();
            }
            var root = Path.GetFullPath(AppContext.BaseDirectory);
            if (!directory.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("录制检查目录越界");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    });

    private static void RunOnSta(Action action)
    {
        // 一个 AppDomain 只能有一个 Application；真实窗口用例（T11）已经创建过的话，
        // 资源字典属于它所在的线程，直接新建会报"不能创建多个 Application"，
        // 在本线程访问它的资源又会报线程亲和性错误。此时改用既有 Application 的 Dispatcher。
        //
        // 但 T11 的 Application 是建在它自己的临时 STA 线程上的，那个线程 Join 完就退出了，
        // Dispatcher 不再泵消息：直接 Invoke 会永久阻塞（本用例曾把整套跑挂在这里）。
        // 所以先限时投递，20 秒内没轮到就判定「无人泵消息」，退回到自建线程路径。
        if (Application.Current is { } existing)
        {
            Exception? crossThreadFailure = null;
            var operation = existing.Dispatcher.InvokeAsync(() =>
            {
                try { action(); }
                catch (Exception error) { crossThreadFailure = error; }
            });
            if (operation.Wait(TimeSpan.FromSeconds(20)) == System.Windows.Threading.DispatcherOperationStatus.Completed)
            {
                if (crossThreadFailure is not null)
                    throw new InvalidOperationException(crossThreadFailure.ToString(), crossThreadFailure);
                return;
            }
            // 走到这里说明 Dispatcher 没人泵：既没执行也没抛错，不能当成通过。
        }

        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(20)), "槽位录制保存失败检查超时");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    /// <summary>反射调用会把异常包一层，比较时只关心真正的原因。</summary>
    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (TargetInvocationException error) when (error.InnerException is not null) { return error.InnerException; }
        catch (Exception error) { return error; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
