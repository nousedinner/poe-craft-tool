using System.Text.Json;
using System.Text.Json.Nodes;
using ShiKe.Services;

namespace ShiKe.Host;

/// <summary>所有工具共用序列化与分节写入；全部候选有效后才接触存储。</summary>
internal static class ToolSettingsPersistence
{
    public static void SaveSections(StorageService storage, IEnumerable<ITool> tools)
    {
        var sections = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) tool.SaveSettings(writer);
            var section = JsonNode.Parse(stream.ToArray()) as JsonObject
                ?? throw new InvalidDataException($"工具“{tool.Name}”保存的数据必须是对象，已取消整组保存");
            if (!sections.TryAdd(tool.Id, section))
                throw new InvalidDataException($"工具 ID“{tool.Id}”重复，已取消整组保存");
        }
        storage.UpdateSettings(root =>
        {
            foreach (var (id, section) in sections) root[id] = section;
        });
    }
}
