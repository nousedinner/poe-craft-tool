using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShiKe.Services;

internal static class PromptChecks
{
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);

    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? owner = null;
            try
            {
                owner = new Window { Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false };
                owner.Show();
                CheckErrorSound();
                foreach (var (buttons, chosen, defaultChoice) in new[]
                {
                    (MessageBoxButton.OK, MessageBoxResult.OK, MessageBoxResult.OK),
                    (MessageBoxButton.OKCancel, MessageBoxResult.Cancel, MessageBoxResult.Cancel),
                    (MessageBoxButton.YesNo, MessageBoxResult.No, MessageBoxResult.No),
                    (MessageBoxButton.YesNoCancel, MessageBoxResult.Yes, MessageBoxResult.Yes),
                })
                {
                    var result = MessageBoxResult.None;
                    WithResponse(() => result = ThemedMessageBox.Show(owner, "需要确认的操作", "配置提示", buttons,
                        MessageBoxImage.Warning, defaultChoice), chosen, dialog =>
                    {
                        Require(ReferenceEquals(dialog.Owner, owner), "确认窗口必须归属原软件窗口");
                        Require(!IsWindowEnabled(new WindowInteropHelper(owner).Handle), "确认期间必须保持原模态语义");
                        var panel = (StackPanel)dialog.FindName("ButtonPanel");
                        Require((MessageBoxResult)panel.Children.OfType<Button>().Single(button => button.IsDefault).Tag == defaultChoice,
                            "默认按钮必须保留原安全选项");
                        Require(((Border)dialog.Content).Background is LinearGradientBrush { IsFrozen: true }, "确认窗口应使用静态淡彩背景");
                        Require(dialog.ActualWidth <= SystemParameters.WorkArea.Width && dialog.ActualHeight <= SystemParameters.WorkArea.Height,
                            "确认窗口不能溢出工作区");
                        if (buttons == MessageBoxButton.YesNo) Snapshot(dialog, "确认弹窗.png");
                    });
                    Require(result == chosen && IsWindowEnabled(new WindowInteropHelper(owner).Handle),
                        "关闭后应返回所选结果并恢复原窗口");
                }
                Require(MessageDialog.DismissResult(MessageBoxButton.YesNo) == MessageBoxResult.No &&
                    MessageDialog.DismissResult(MessageBoxButton.YesNoCancel) == MessageBoxResult.Cancel,
                    "取消关闭不得确认删除或覆盖");
                var closedResult = MessageBoxResult.None;
                WithResponse(() => closedResult = ThemedMessageBox.Show(owner, "关闭不能确认删除", "删除预设", MessageBoxButton.YesNo),
                    MessageBoxResult.None);
                Require(closedResult == MessageBoxResult.No, "直接关闭确认窗口必须返回否");
                WithResponse(() => ThemedMessageBox.ShowRuntimeError(new string('测', 2000)), MessageBoxResult.OK,
                    dialog => Require(dialog.Topmost && ((ScrollViewer)dialog.FindName("MessageScroll")).ScrollableHeight > 0,
                        "运行错误仍须置顶，长文本必须可滚动"));
                using var notification = new NotificationService();
                WithResponse(() => notification.ShowError("只显示一次"), MessageBoxResult.OK, _ =>
                {
                    notification.ShowError("重复错误不能再弹窗");
                    Require(PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                        .OfType<MessageDialog>().Count() == 1, "错误防风暴必须保留");
                });
                notification.Show("✓ 已找到符合规则的词缀");
                var overlay = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                    .OfType<Window>().Single(window => !ReferenceEquals(window, owner));
                Require(overlay.IsVisible && !overlay.ShowActivated && !overlay.ShowInTaskbar && overlay.Topmost,
                    "自动提醒必须仍不抢焦点、不进任务栏且置顶");
                var overlayStyle = GetWindowLong(new WindowInteropHelper(overlay).Handle, -20);
                Require((overlayStyle & 0x08000020) == 0x08000020, "提醒窗口句柄必须真正设置鼠标穿透和禁止激活标志");
                Snapshot(overlay, "自动消失提醒.png");
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame);
                Require(!overlay.IsVisible, "自动提醒仍须在约2秒后消失");
            }
            catch (Exception error) { failure = error; }
            finally { owner?.Close(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(12)), "提示窗口检查超时");
        if (failure is not null) throw new InvalidOperationException("提示窗口行为检查失败: " + failure, failure);
    }

    private static void CheckErrorSound()
    {
        foreach (var image in new[] { MessageBoxImage.Error, MessageBoxImage.Warning, MessageBoxImage.None })
        {
            var requests = 0;
            var dialog = new MessageDialog("提示音检查", "提示", MessageBoxButton.OK, image, MessageBoxResult.OK,
                false, () => requests++);
            Require(requests == 0, "仅构造窗口不能提前播放错误音");
            var rendered = false;
            dialog.ContentRendered += (_, _) => rendered = true;
            try
            {
                dialog.Show();
                WaitFor(() => rendered);
                var expected = image is MessageBoxImage.Error or MessageBoxImage.Warning ? 1 : 0;
                Require(requests == expected, "仅已显示的错误/警告窗提交一次系统提示音");
                // 初次显示走真实WPF绘制；额外框架通知检验重复回调不会重复播放。
                var renderMethod = typeof(Window).GetMethod("OnContentRendered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                renderMethod.Invoke(dialog, [EventArgs.Empty]); renderMethod.Invoke(dialog, [EventArgs.Empty]);
                Require(requests == expected, "重复绘制通知不能重复响铃");
            }
            finally { dialog.Close(); }
        }
        var failed = new MessageDialog("音效失败仍需可确认", "错误", MessageBoxButton.OK, MessageBoxImage.Error,
            MessageBoxResult.OK, false, () => throw new InvalidOperationException("隔离音效失败"));
        var failedRendered = false;
        failed.ContentRendered += (_, _) => failedRendered = true;
        try
        {
            failed.Show(); WaitFor(() => failedRendered);
            var button = ((StackPanel)failed.FindName("ButtonPanel")).Children.OfType<Button>().Single();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(failed.Result == MessageBoxResult.OK, "音效异常不能阻止错误窗关闭");
        }
        finally { if (failed.IsVisible) failed.Close(); }
    }

    private static void WaitFor(Func<bool> ready)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(ready(), "提示窗口未完成真实显示");
    }

    internal static string WithResponse(Action show, MessageBoxResult choice, Action<MessageDialog>? inspect = null)
    {
        var observed = false;
        Exception? failure = null;
        var text = "";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            var dialog = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                .OfType<MessageDialog>().SingleOrDefault();
            if (dialog is null) return;
            timer.Stop(); observed = true;
            try
            {
                text = ((TextBox)dialog.FindName("MessageText")).Text;
                inspect?.Invoke(dialog);
                if (choice == MessageBoxResult.None) { dialog.Close(); return; }
                var button = ((StackPanel)dialog.FindName("ButtonPanel")).Children.OfType<Button>()
                    .Single(button => (MessageBoxResult)button.Tag == choice);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        timer.Start();
        try { show(); }
        finally { timer.Stop(); }
        Require(observed, "预期的确认弹窗没有显示");
        if (failure is not null) throw new InvalidOperationException("弹窗内容或按钮检查失败", failure);
        return text;
    }

    internal static void Snapshot(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("SHIKE_UI_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 1.5), (int)Math.Ceiling(window.ActualHeight * 1.5),
            144, 144, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
