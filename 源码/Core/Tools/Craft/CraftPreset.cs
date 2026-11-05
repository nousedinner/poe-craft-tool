using System.Text.Json.Nodes;

namespace ShiKe.Tools.Craft;

/// <summary>预设的无界面数据边界；保留既有 JSON 字段及字符串/字典词缀兼容性。</summary>
public sealed record CraftPreset(
    IReadOnlyList<string> PrimaryAffixes,
    int PrimaryHitCount,
    IReadOnlyList<string> SecondaryAffixes,
    int SecondaryHitCount,
    IReadOnlyList<string> ExcludeAffixes)
{
    public static CraftPreset FromRules(CraftRules rules) => new(
        rules.PrimaryAffixes.Select(rule => rule.Text).ToArray(), rules.RequiredPrimaryHits,
        rules.SecondaryAffixes.Select(rule => rule.Text).ToArray(), rules.RequiredSecondaryHits,
        rules.ExcludeAffixes.Select(rule => rule.Text).ToArray());

    public static CraftPreset FromJson(JsonObject data) => new(
        ReadAffixes(data, "primary_affixes"), ReadInt(data, "primary_hit_count"),
        ReadAffixes(data, "secondary_affixes"), ReadInt(data, "secondary_hit_count"),
        ReadAffixes(data, "exclude_affixes"));

    public JsonObject ToJson() => new()
    {
        ["primary_affixes"] = new JsonArray(PrimaryAffixes.Select(text => (JsonNode)text).ToArray()),
        ["primary_hit_count"] = PrimaryHitCount,
        ["secondary_affixes"] = new JsonArray(SecondaryAffixes.Select(text => (JsonNode)text).ToArray()),
        ["secondary_hit_count"] = SecondaryHitCount,
        ["exclude_affixes"] = new JsonArray(ExcludeAffixes.Select(text => (JsonNode)text).ToArray()),
    };

    private static int ReadInt(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private static IReadOnlyList<string> ReadAffixes(JsonObject data, string key)
    {
        if (data[key] is not JsonArray values) return [];
        var result = new List<string>();
        foreach (var item in values)
        {
            var text = item switch
            {
                JsonValue value when value.TryGetValue<string>(out var candidate) => candidate,
                JsonObject obj when obj["text"] is JsonValue value && value.TryGetValue<string>(out var candidate) => candidate,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
        }
        return result;
    }
}
