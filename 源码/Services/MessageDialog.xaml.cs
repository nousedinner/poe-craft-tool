using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ShiKe.Services;

internal partial class MessageDialog : Window
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    private readonly MessageBoxResult _dismissResult;
    internal MessageBoxResult Result { get; private set; }

    internal MessageDialog(string message, string caption, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult, bool topmost)
    {
        InitializeComponent();
        Title = caption;
        CaptionText.Text = caption;
        CaptionText.ToolTip = caption;
        MessageText.Text = message;
        DialogSurface.Background = PromptPalette.CreateSurface();
        Topmost = topmost;
        var work = SystemParameters.WorkArea;
        Width = Math.Min(460, Math.Max(240, work.Width - 32));
        MaxHeight = Math.Max(200, work.Height - 24);
        MessageScroll.MaxHeight = Math.Max(60, work.Height - 230);
        _dismissResult = DismissResult(buttons);

        (string Label, MessageBoxResult Result)[] choices = buttons switch
        {
            MessageBoxButton.OKCancel => [("确定", MessageBoxResult.OK), ("取消", MessageBoxResult.Cancel)],
            MessageBoxButton.YesNo => [("是", MessageBoxResult.Yes), ("否", MessageBoxResult.No)],
            MessageBoxButton.YesNoCancel => [("是", MessageBoxResult.Yes), ("否", MessageBoxResult.No), ("取消", MessageBoxResult.Cancel)],
            _ => [("确定", MessageBoxResult.OK)],
        };
        if (!choices.Any(choice => choice.Result == defaultResult)) defaultResult = choices[0].Result;
        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = choice.Label, Tag = choice.Result,
                Style = (Style)FindResource("PromptButton"),
                IsDefault = choice.Result == defaultResult,
                IsCancel = choice.Result == _dismissResult,
            };
            if (choice.Result == defaultResult)
            {
                button.Background = PromptPalette.Accent;
                button.Foreground = Brushes.White;
                button.BorderBrush = PromptPalette.Accent;
            }
            button.Click += (_, _) => { Result = choice.Result; Close(); };
            ButtonPanel.Children.Add(button);
        }
        IconText.Text = image switch { MessageBoxImage.Error => "!", MessageBoxImage.Warning => "!", MessageBoxImage.Question => "?", _ => "i" };
        IconText.Foreground = image switch
        {
            MessageBoxImage.Error => PromptPalette.Brush("#AD4F62"),
            MessageBoxImage.Warning => PromptPalette.Brush("#98772C"),
            _ => PromptPalette.Accent,
        };
        IconSurface.Background = image switch
        {
            MessageBoxImage.Error => PromptPalette.Brush("#F7E5EB"),
            MessageBoxImage.Warning => PromptPalette.Brush("#F7EFDB"),
            _ => PromptPalette.Brush("#E3F0F6"),
        };
        ContentRendered += (_, _) =>
        {
            ButtonPanel.Children.OfType<Button>().First(button => button.IsDefault).Focus();
            if (!Topmost) return;
            Activate();
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
        };
        Closed += (_, _) => { if (Result == MessageBoxResult.None) Result = _dismissResult; };
    }

    internal static MessageBoxResult DismissResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
        _ => MessageBoxResult.OK,
    };

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
