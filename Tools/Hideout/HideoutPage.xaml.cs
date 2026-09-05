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
        HotkeyLabel.Text = $"（热键 {tool.Hotkey}）";
        InstructionHotkeyRun.Text = $"游戏内按 {tool.Hotkey}：自动打开聊天框输入 ";
        CommandText.Text = tool.Command;
    }

    private void OnToggleChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        _tool.SetEnabled(EnableCheck.IsChecked == true);
    }

    private void CommandText_Commit(object sender, System.Windows.RoutedEventArgs e)
    {
        _tool.SetCommand(CommandText.Text);
        CommandText.Text = _tool.Command;
    }

    private void CommandText_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        CommandText_Commit(sender, e);
        System.Windows.Input.Keyboard.ClearFocus();
        e.Handled = true;
    }
}
