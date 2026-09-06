using System.Windows.Controls;

namespace ShiKe.Tools.Hideout;

/// <summary>一键回城页：启用开关（立即保存 hideout 节）。</summary>
public partial class HideoutPage : UserControl
{
    private readonly HideoutTool _tool;
    private bool _refreshing;

    public HideoutPage(HideoutTool tool)
    {
        _tool = tool;
        InitializeComponent();
        RefreshFromTool();
    }

    internal void RefreshFromTool()
    {
        _refreshing = true;
        try
        {
            EnableCheck.IsChecked = _tool.IsEnabled;
            HotkeyLabel.Text = $"（热键 {_tool.Hotkey}）";
            InstructionHotkeyRun.Text = $"游戏内按 {_tool.Hotkey}：自动打开聊天框输入 ";
            CommandText.Text = _tool.Command;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnToggleChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_refreshing) return;
        var result = _tool.SetEnabled(EnableCheck.IsChecked == true);
        if (result.Success) return;
        RefreshFromTool();
        var owner = System.Windows.Window.GetWindow(this);
        if (owner is null)
            System.Windows.MessageBox.Show(result.Message, "无法修改启用状态", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        else
            System.Windows.MessageBox.Show(owner, result.Message, "无法修改启用状态", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    private void CommandText_Commit(object sender, System.Windows.RoutedEventArgs e)
    {
        _tool.SetCommand(CommandText.Text);
        CommandText.Text = _tool.Command;
    }

    private void CommandText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_refreshing || string.IsNullOrWhiteSpace(CommandText.Text)) return;
        _tool.SetCommand(CommandText.Text);
    }

    private void CommandText_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        CommandText_Commit(sender, e);
        System.Windows.Input.Keyboard.ClearFocus();
        e.Handled = true;
    }
}
