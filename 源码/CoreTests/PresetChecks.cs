using System.Text.Json.Nodes;
using ShiKe.Tools.Craft;

internal static class PresetChecks
{
    public static void LegacyMixedAffixesRoundTripWithOriginalFields()
    {
        var legacy = JsonNode.Parse("""
            {"primary_affixes":["生命",{"text":"物理伤害"},null,42,"  "],"primary_hit_count":2,
             "secondary_affixes":[{"text":"攻击速度"}],"secondary_hit_count":1,
             "exclude_affixes":["敏捷",{"text":"智慧"}],"unknown_legacy_field":true}
            """)!.AsObject();
        var preset = CraftPreset.FromJson(legacy);
        Require(preset.PrimaryAffixes.SequenceEqual(new[] { "生命", "物理伤害" }) &&
                preset.SecondaryAffixes.SequenceEqual(new[] { "攻击速度" }) &&
                preset.ExcludeAffixes.SequenceEqual(new[] { "敏捷", "智慧" }),
            "旧字符串/字典混合词缀必须按原顺序读取，忽略非词缀条目");
        var serialized = preset.ToJson();
        Require(serialized.Select(pair => pair.Key).Order(StringComparer.Ordinal).SequenceEqual(new[]
            { "exclude_affixes", "primary_affixes", "primary_hit_count", "secondary_affixes", "secondary_hit_count" }),
            "输出必须保持原来的五字段格式");
        var restored = CraftPreset.FromJson(JsonNode.Parse(serialized.ToJsonString())!.AsObject());
        Require(restored.PrimaryHitCount == 2 && restored.SecondaryHitCount == 1 &&
                restored.PrimaryAffixes.SequenceEqual(preset.PrimaryAffixes) &&
                restored.SecondaryAffixes.SequenceEqual(preset.SecondaryAffixes) &&
                restored.ExcludeAffixes.SequenceEqual(preset.ExcludeAffixes),
            "保存后必须保留命中数量和三组词缀");
    }

    public static void PresetSnapshotKeepsSingleCurrencyCountsAndIndependentAffixes()
    {
        var rules = new CraftRules { Mode = CraftMode.Single, PrimaryHitCount = 3, SecondaryHitCount = 2 };
        rules.PrimaryAffixes.Add(new AffixRule { Text = "生命" });
        rules.SecondaryAffixes.Add(new AffixRule { Text = "攻击速度" });
        rules.ExcludeAffixes.Add(new AffixRule { Text = "敏捷" });
        var preset = CraftPreset.FromRules(rules);
        rules.PrimaryAffixes.Clear();
        rules.SecondaryAffixes.Clear();
        rules.ExcludeAffixes.Clear();
        rules.Mode = CraftMode.AltAug;
        Require(preset.PrimaryHitCount == 1 && preset.SecondaryHitCount == 0,
            "单通货保存仍为主1次0，不泄漏隐藏的多通货数量");
        Require(preset.PrimaryAffixes.Single() == "生命" && preset.SecondaryAffixes.Single() == "攻击速度" &&
                preset.ExcludeAffixes.Single() == "敏捷",
            "预设必须持有独立内容快照，不随之后的规则编辑改变");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
