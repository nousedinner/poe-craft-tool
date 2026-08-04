using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ShiKe.Host;

namespace ShiKe.Tools.Hideout;

/// <summary>
/// 一键回城抽屉。
/// 阶段1：空骨架（验证导航架构）；阶段4实现完整功能（F2 热键 + 聊天命令）。
/// </summary>
public sealed class HideoutTool : ITool
{
    public string Id => "hideout";
    public string Name => "一键回城";
    public string IconKey => "hideout";

    public void Initialize(ToolHost host)
    {
        // 阶段4：保存 host 引用、读设置
    }

    public FrameworkElement CreatePage() => new TextBlock
    {
        Text = "一键回城（阶段 4 实现）",
        FontSize = 16,
        Foreground = System.Windows.Media.Brushes.Gray,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public void OnActivate() { }

    public void OnDeactivate() { }

    public void OnShutdown() { }

    // 阶段4 声明：F2 一键回城（Toggle, CheckForeground=true）
    public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() => Array.Empty<HotkeyRequest>();

    public void LoadSettings(JsonElement section) { }

    public void SaveSettings(Utf8JsonWriter writer) { }
}
