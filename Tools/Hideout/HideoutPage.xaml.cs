using System.Windows.Controls;

namespace ShiKe.Tools.Hideout;

/// <summary>一键回城页：启用开关（立即保存 hideout 节）。</summary>
public partial class HideoutPage : UserControl
{
    private readonly HideoutTool _tool;

    public HideoutPage(HideoutTool tool)
    {
        _tool = tool;
        InitializeComponent();
        EnableCheck.IsChecked = tool.IsEnabled;
    }

    private void OnToggleChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        _tool.SetEnabled(EnableCheck.IsChecked == true);
    }
}
