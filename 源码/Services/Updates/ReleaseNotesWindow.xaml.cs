using System.Windows;
using System.Windows.Input;

namespace ShiKe.Services.Updates;

/// <summary>仅展示调用方提供的本次更新说明；是否需要展示由自动更新流程决定。</summary>
internal partial class ReleaseNotesWindow : Window
{
    internal bool RenderedOnce { get; private set; }

    internal ReleaseNotesWindow(string version, string text)
    {
        InitializeComponent();
        VersionText.Text = $"已更新至 v{version}";
        NotesText.Text = text;
        DialogSurface.Background = PromptPalette.CreateSurface();
        AcknowledgeButton.Background = PromptPalette.Accent;
        AcknowledgeButton.BorderBrush = PromptPalette.Accent;

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(520, Math.Max(240, workArea.Width - 32));
        MaxHeight = Math.Max(200, workArea.Height - 24);
        NotesScroll.MaxHeight = Math.Max(50, Math.Min(380, workArea.Height - 260));
        ContentRendered += OnContentRendered;
    }

    private void OnContentRendered(object? sender, EventArgs args)
    {
        if (RenderedOnce) return;
        RenderedOnce = true;
        AcknowledgeButton.Focus();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs args) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        args.Handled = true;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.Left && args.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}
