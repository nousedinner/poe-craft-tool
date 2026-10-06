using System.Text.Json;

namespace ShiKe.Services;

/// <summary>热键缺少字段时使用默认值；显式空字符串表示未绑定。</summary>
public static class HotkeySetting
{
    public static string Read(JsonElement section, string propertyName, string fallback)
        => section.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : fallback;

    public static string Display(string key)
        => string.IsNullOrWhiteSpace(key) ? "未绑定" : key;
}
