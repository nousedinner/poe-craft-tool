using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;

internal sealed class StubTool(string id, JsonNode? settings) : ITool
{
    public string Id => id;
    public string Name => id;
    public string IconKey => "";
    public void Initialize(ToolHost host) { }
    public FrameworkElement CreatePage() => throw new NotSupportedException("此虚拟工具仅验证存储，不创建窗口");
    public void OnActivate() { }
    public void OnDeactivate() { }
    public void OnShutdown() { }
    public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() => [];
    public void LoadSettings(JsonElement section) { }
    public void SaveSettings(Utf8JsonWriter writer)
    {
        if (settings is null) writer.WriteNullValue();
        else settings.WriteTo(writer);
    }
}
