using System.Text.Json;

namespace ShiKe.Services;

/// <summary>热键缺少字段时使用默认值；显式空字符串表示未绑定。</summary>
public static class HotkeySetting
{
    public static string Read(JsonElement section, string propertyName, string fallback)
        => section.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : fallback;

    /// <summary>界面显示名：鼠标侧键显示为“鼠标4/5”，其余保持配置原文。</summary>
    public static string Display(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "未绑定";
        // 只替换主键段，修饰键前缀保持原样（Ctrl+Mouse4 → Ctrl+鼠标4）。
        var parts = key.Split('+');
        if (MouseButtonKey.TryParse(parts[^1], out var vk))
            parts[^1] = MouseButtonKey.DisplayName(vk);
        return string.Join('+', parts);
    }
}
