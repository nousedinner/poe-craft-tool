using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShiKe.Services.Updates;

internal static class ReleaseNotesWindowChecks
{
    // 由现有AppearanceChecks调用，复用同一个Application；不触碰剪贴板或发送真实键盘输入。
    internal static void Check(Window owner)
    {
        const string notes = "• 优化运行状态显示。\r\n• 保留已有配置和热键。\r\n\r\n本次说明按纯文本显示：<更新> [内容]。";
        var dialog = new ReleaseNotesWindow("1.0.55", notes) { Owner = owner, ShowActivated = false };
        Require(!dialog.RenderedOnce, "仅构造更新说明窗口不能算已展示");
        try
        {
            dialog.Show();
            WaitForRender(dialog);
            Require(dialog.Title == "更新完成" && ReferenceEquals(dialog.Owner, owner), "标题及所属窗口应保持约定");
            Require(Find<TextBlock>(dialog, "VersionText").Text == "已更新至 v1.0.55", "版本必须显示在窗口内");
            var text = Find<TextBox>(dialog, "NotesText");
            Require(text.Text == notes && text.IsReadOnly && text.TextWrapping == TextWrapping.Wrap,
                "说明必须按只读纯文本保留换行、项目符号和内容");
            const string selectableLine = "• 优化运行状态显示。";
            text.Select(0, selectableLine.Length);
            Require(text.SelectedText == selectableLine && ApplicationCommands.Copy.CanExecute(null, text),
                "更新内容应允许选择和复制，但检查本身不能改写剪贴板");
            var button = Find<Button>(dialog, "AcknowledgeButton");
            Require(Equals(button.Content, "知道了") && button.IsDefault && button.IsCancel,
                "唯一确认按钮应同时提供默认与取消语义");
            Require(((Border)dialog.Content).Background is LinearGradientBrush { IsFrozen: true },
                "更新说明应复用静态淡彩背景");
            CheckBounds(dialog, button);
            text.Select(0, 0);
            PromptChecks.Snapshot(dialog, "更新说明.png");
            var closed = false;
            dialog.Closed += (_, _) => closed = true;
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(closed && !dialog.IsVisible && dialog.RenderedOnce, "知道了应关闭窗口并保留已展示记录");
        }
        finally { dialog.Close(); }

        var longNotes = string.Join("\n", Enumerable.Range(1, 180).Select(index => $"• 第 {index} 项更新：详细说明保持完整，可滚动查看。"));
        var longDialog = new ReleaseNotesWindow("1.0.55", longNotes) { Owner = owner, ShowActivated = false };
        try
        {
            longDialog.Show();
            WaitForRender(longDialog);
            var scroll = Find<ScrollViewer>(longDialog, "NotesScroll");
            Require(scroll.ScrollableHeight > 0, "长更新说明必须提供滚动区域");
            scroll.ScrollToEnd();
            Pump();
            Require(scroll.VerticalOffset > 0 && Find<TextBox>(longDialog, "NotesText").Text == longNotes,
                "长说明应能滚动到底部且不截断原文");
            CheckBounds(longDialog, Find<Button>(longDialog, "AcknowledgeButton"));
            var text = Find<TextBox>(longDialog, "NotesText");
            var closed = false;
            longDialog.Closed += (_, _) => closed = true;
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(longDialog),
                Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            text.RaiseEvent(escape);
            Require(escape.Handled && closed && !longDialog.IsVisible,
                "更新正文的Escape路由必须关闭窗口");
        }
        finally { longDialog.Close(); }

        var closeDialog = new ReleaseNotesWindow("1.0.55", notes) { Owner = owner, ShowActivated = false };
        try
        {
            closeDialog.Show();
            WaitForRender(closeDialog);
            var closed = false;
            closeDialog.Closed += (_, _) => closed = true;
            Find<Button>(closeDialog, "CloseButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(closed && closeDialog.RenderedOnce, "右上关闭应正常关闭且保留已展示记录");
        }
        finally { closeDialog.Close(); }
    }

    private static void CheckBounds(Window window, Button button)
    {
        var work = SystemParameters.WorkArea;
        Require(window.ActualWidth > 0 && window.ActualWidth <= work.Width &&
                window.ActualHeight > 0 && window.ActualHeight <= work.Height,
            "更新说明窗口不能溢出工作区尺寸");
        var bottom = button.TranslatePoint(new Point(0, button.ActualHeight), window);
        Require(button.IsVisible && bottom.Y <= window.ActualHeight, "长说明不能挤走知道了按钮");
    }

    private static void WaitForRender(ReleaseNotesWindow dialog)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (dialog.RenderedOnce || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(dialog.RenderedOnce && dialog.IsVisible, "更新说明没有完成真实内容显示");
        Pump();
    }

    private static T Find<T>(FrameworkElement root, string name) where T : class
        => root.FindName(name) as T ?? throw new InvalidOperationException("找不到控件: " + name);

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
