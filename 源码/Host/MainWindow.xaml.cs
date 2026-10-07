using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShiKe.Services;

namespace ShiKe.Host;

public partial class MainWindow : Window
{
    private const string GuideUrl = "https://open.cancanneed.top/guide.html";
    private const string FeedbackUrl = "https://docs.qq.com/form/page/DZkpVa05VSEtUZkhT";

    private readonly ToolRegistry _registry;
    private readonly ToolHost _host;
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    private ITool? _currentTool;
    private string? _bottomAdUrl;
    private volatile bool _statusUiVisible;
    private int _statusRefreshPending;
    private bool _restoringGradientSpeed;
    private static readonly SolidColorBrush ActiveStatusBrush = MakeStatusBrush(0x1B, 0x8A, 0x3E);
    private static readonly SolidColorBrush IdleStatusBrush = MakeStatusBrush(0xAA, 0xAA, 0xAA);

    public MainWindow(ToolRegistry registry, ToolHost host)
    {
        InitializeComponent();
        // 高 DPI / 小工作区时缩到可见范围；三栏各自滚动，避免窗口底部落到屏幕外。
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
        _registry = registry;
        _host = host;
        GradientSpeed.Value = host.GradientSpeed;
        GradientSpeed.ValueChanged += GradientSpeed_ValueChanged;
        _host.Statuses.Changed += ScheduleStatusRefresh;
        IsVisibleChanged += (_, _) =>
        {
            _statusUiVisible = IsVisible;
            if (_statusUiVisible) RefreshStatus();
        };
        Closed += (_, _) =>
        {
            _statusUiVisible = false;
            _host.Statuses.Changed -= ScheduleStatusRefresh;
        };

        // 导航绑定注册表，注册顺序 = 导航顺序
        ToolList.ItemsSource = registry.Tools;
        ToolList.DisplayMemberPath = nameof(ITool.Name);
        ToolList.SelectedIndex = 0; // 默认选中第一个抽屉
        BottomAdButton.Click += BottomAdButton_Click;
    }

    private void GradientSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (_restoringGradientSpeed) return;
        try { _host.SaveGradientSpeed((int)args.NewValue); }
        catch (StorageException error)
        {
            _restoringGradientSpeed = true;
            try { GradientSpeed.Value = _host.GradientSpeed; }
            finally { _restoringGradientSpeed = false; }
            NotificationService.ShowConfigurationError(error.Message, this);
        }
    }

    // ── 导航 ──

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is not ITool tool)
            return;

        // 切走通知
        try { _currentTool?.OnDeactivate(); }
        catch (StorageException error)
        {
            NotificationService.ShowConfigurationError(error.Message, this);
        }
        _currentTool = tool;

        // 懒加载：首次选中创建页面，后续复用缓存
        if (!_pages.TryGetValue(tool.Id, out var page))
        {
            page = tool.CreatePage();
            _pages[tool.Id] = page;
        }

        ToolContent.Content = page;
        tool.OnActivate();
        RefreshStatus();
    }

    // ── 状态栏 ──

    private static SolidColorBrush MakeStatusBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void ScheduleStatusRefresh()
    {
        if (!_statusUiVisible || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished ||
            Interlocked.Exchange(ref _statusRefreshPending, 1) != 0) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _statusRefreshPending, 0);
                if (_statusUiVisible) RefreshStatus();
            });
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _statusRefreshPending, 0); }
    }

    public void RefreshStatus()
    {
        var (selected, anyRunning) = _host.Statuses.Read(_currentTool?.Id ?? "");
        var text = selected?.Text ?? (anyRunning ? "其他工具正在运行" :
            string.IsNullOrWhiteSpace(_host.Foreground.TargetProcess) ? "就绪" : $"已锁定: {_host.Foreground.TargetProcess}");
        if (selected is { Running: false } && anyRunning) text += "；另有工具运行中";
        SetStatus(text, anyRunning);
        SetUseCount(_currentTool?.Id == "craft" ? selected?.UseCount ?? 0 : 0);
    }

    private void SetStatus(string text, bool active)
    {
        StatusText.Text = text;
        StatusDot.Foreground = active ? ActiveStatusBrush : IdleStatusBrush;
    }

    /// <summary>已使用次数（洗装模式下显示）。</summary>
    public void SetUseCount(int count)
    {
        UseCountLabel.Text = $"已使用 {count} 次";
        UseCountLabel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 广告 ──

    /// <summary>
    /// 应用广告数据（对齐 Python _update_ads）：
    /// 内容下方横幅：全部 top 广告（多条），无则"广告位招租"占位。
    /// 横幅右侧：第一条 bottom 广告。服务端字段和链接行为保持。
    /// 注：服务器 style 是 Qt CSS，WPF 不兼容，统一用默认链接样式。
    /// </summary>
    public void ApplyAds(List<AdItem> ads)
    {
        TopAdPanel.Children.Clear();

        var topAds = ads.Where(a => a.Location == "top" && a.Type == "text" && !string.IsNullOrWhiteSpace(a.Text)).ToList();
        if (topAds.Count == 0)
        {
            TopAdPanel.Children.Add(MakeAdPlaceholder());
        }
        else
        {
            foreach (var ad in topAds)
            {
                if (ad.Type != "text")
                    continue;
                var btn = new Button
                {
                    Content = ad.Text,
                    Style = (Style)FindResource("LinkButton"),
                    FontSize = 12,
                    MaxWidth = 500,
                    Margin = new Thickness(0, 0, 16, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (!string.IsNullOrEmpty(ad.Link))
                {
                    var url = ad.Link;
                    btn.Click += (_, _) => OpenBrowser(url);
                }
                TopAdPanel.Children.Add(btn);
            }
        }

        var bottomAd = ads.FirstOrDefault(a => a.Location == "bottom" && a.Type == "text" && !string.IsNullOrWhiteSpace(a.Text));
        if (bottomAd is { Type: "text" })
        {
            BottomAdButton.Content = bottomAd.Text;
            BottomAdButton.Visibility = Visibility.Visible;
            _bottomAdUrl = string.IsNullOrWhiteSpace(bottomAd.Link) ? null : bottomAd.Link;
        }
        else
        {
            BottomAdButton.Content = "广告位招租";
            BottomAdButton.Visibility = Visibility.Collapsed;
            _bottomAdUrl = null;
        }
    }

    private Border MakeAdPlaceholder() => new()
    {
        BorderBrush = (Brush)FindResource("CardBorder"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 2, 6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = "广告位招租", Foreground = (Brush)FindResource("TextSecondary"), FontSize = 12 },
    };

    private void BottomAdButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bottomAdUrl is not null) OpenBrowser(_bottomAdUrl);
    }

    // ── 链接 ──

    private void GuideButton_Click(object sender, RoutedEventArgs e) => OpenBrowser(GuideUrl);

    private void FeedbackButton_Click(object sender, RoutedEventArgs e) => OpenBrowser(FeedbackUrl);

    private void OpenBrowser(string url)
    {
        if (!BrowserLauncher.TryOpen(url, out var error))
            MessageBox.Show(this, error, "无法打开网页", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
