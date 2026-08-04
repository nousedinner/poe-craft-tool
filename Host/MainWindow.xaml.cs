using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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

    public MainWindow(ToolRegistry registry, ToolHost host)
    {
        InitializeComponent();
        _registry = registry;
        _host = host;

        // 导航绑定注册表，注册顺序 = 导航顺序
        ToolList.ItemsSource = registry.Tools;
        ToolList.DisplayMemberPath = nameof(ITool.Name);
        ToolList.SelectedIndex = 0; // 默认选中第一个抽屉
    }

    // ── 导航 ──

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is not ITool tool)
            return;

        // 切走通知
        _currentTool?.OnDeactivate();
        _currentTool = tool;

        // 懒加载：首次选中创建页面，后续复用缓存
        if (!_pages.TryGetValue(tool.Id, out var page))
        {
            page = tool.CreatePage();
            _pages[tool.Id] = page;
        }

        ToolContent.Content = page;
        tool.OnActivate();
    }

    // ── 状态栏 ──

    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>已使用次数（洗装模式下显示）。</summary>
    public void SetUseCount(int count)
    {
        UseCountLabel.Text = $"已使用 {count} 次";
        UseCountLabel.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 广告 ──

    /// <summary>
    /// 应用广告数据（对齐 Python _update_ads）：
    /// 顶部：全部 top 广告（多条），无则"广告位招租"占位；点击带 link 的用浏览器打开。
    /// 底部：第一条 bottom 广告，无则占位。
    /// 注：服务器 style 是 Qt CSS，WPF 不兼容，统一用默认链接样式。
    /// </summary>
    public void ApplyAds(List<AdItem> ads)
    {
        TopAdPanel.Children.Clear();

        var topAds = ads.Where(a => a.Location == "top").ToList();
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

        var bottomAd = ads.FirstOrDefault(a => a.Location == "bottom");
        if (bottomAd is { Type: "text" })
        {
            BottomAdButton.Content = bottomAd.Text;
            BottomAdButton.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(bottomAd.Link))
            {
                var url = bottomAd.Link;
                BottomAdButton.Click -= OpenBrowserFromAd; // 防重复绑定
                BottomAdButton.Click += (_, _) => OpenBrowser(url);
            }
        }
        else
        {
            BottomAdButton.Content = "广告位招租";
            BottomAdButton.Visibility = Visibility.Collapsed;
        }
    }

    private static Border MakeAdPlaceholder() => new()
    {
        BorderBrush = System.Windows.Media.Brushes.LightGray,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 2, 6, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = "广告位招租", Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11 },
    };

    private void OpenBrowserFromAd(object sender, RoutedEventArgs e) { }

    // ── 链接 ──

    private void GuideButton_Click(object sender, RoutedEventArgs e) => OpenBrowser(GuideUrl);

    private void FeedbackButton_Click(object sender, RoutedEventArgs e) => OpenBrowser(FeedbackUrl);

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 打开失败静默
        }
    }
}
