using System.Text.RegularExpressions;

namespace ShiKe.Tools.Craft;

/// <summary>
/// 词缀解析与匹配引擎。
/// 行为按 DEV_GUIDE §60-98 + 方案 §6 铁律实现（用户 2026-08-04 拍板：装备名字参与匹配）：
/// - 按行处理，跳过空行与 -------- 分段符
/// - 过滤元数据行（skip patterns 中英文完整，铁律 #3）
/// - ⚠️ 装备名字/基础类型必须保留参与匹配（铁律 #1：宽松匹配是设计意图，不过滤）
/// - 匹配：排除优先 → 主 → 次；每条规则最多匹配一行（break）；子串匹配，无数值范围（未实现功能）
/// </summary>
public static class AffixEngine
{
    /// <summary>元数据 skip patterns（DEV_GUIDE §66-88 完整列表 + 品质；^ 前缀匹配）。</summary>
    private static readonly string[] SkipPatterns =
    [
        // 中文（腾讯客户端）
        @"^稀\s*有\s*度",           // 稀有度
        @"^物品类别",               // 物品类别
        @"^物品等级",               // 物品等级
        @"^需求",                   // 需求
        @"^等级:",                  // 等级需求
        @"^敏捷:", @"^力量:", @"^智慧:",  // 属性需求
        @"^插槽",                   // 插槽
        @"^物理伤害",               // 物理伤害
        @"^火焰，冰霜，闪电伤害",    // 元素伤害（国服顿号分隔）
        @"^火焰伤害", @"^冰霜伤害", @"^闪电伤害",
        @"^攻击暴击率",             // 攻击暴击率
        @"^每秒攻击次数",           // 每秒攻击次数
        @"^出售获得通货",           // 出售获得通货
        @"^品质",                   // 品质（补充）
        // 英文
        @"^Rarity", @"^Item Class", @"^Item Level", @"^Requires", @"^Level:",
        @"^Sockets", @"^Quality", @"^Physical Damage", @"^Elemental Damage",
        @"^Fire Damage", @"^Cold Damage", @"^Lightning Damage",
        @"^Critical Strike Chance", @"^Attacks per Second", @"^Vendor",
    ];

    /// <summary>
    /// 解析物品文本 → 词缀行列表（装备名/基础类型/属性块行保留，元数据行过滤）。
    /// </summary>
    public static List<string> ParseItemText(string clipboardText)
    {
        var lines = new List<string>();
        foreach (var raw in clipboardText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("--------")) continue;   // 分段符
            if (IsSkipped(line)) continue;               // 元数据行
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>词缀总数（对齐 Python get_item_affix_count）。</summary>
    public static int GetAffixCount(string itemText) => ParseItemText(itemText).Count;

    private static bool IsSkipped(string line)
    {
        foreach (var pattern in SkipPatterns)
        {
            if (Regex.IsMatch(line, pattern)) return true;
        }
        return false;
    }

    /// <summary>
    /// 检查词缀行是否满足规则（对齐 DEV_GUIDE §92-97 + Python check_affixes）：
    /// 排除优先 → 命中即 early return（终检/循环中均判失败，铁律 #7）；
    /// 主词缀 → 每条规则最多匹配一行（break）；次级同理。
    /// </summary>
    public static AffixCheckResult CheckAffixes(List<string> affixLines, CraftRules rules)
    {
        var result = new AffixCheckResult();

        // 1. 排除优先
        foreach (var rule in rules.ExcludeAffixes)
        {
            foreach (var line in affixLines)
            {
                if (rule.Matches(line))
                {
                    result.HasExclude = true;
                    result.MatchedExclude.Add(line);
                    return result;
                }
            }
        }

        // 2. 主词缀（每规则最多 +1，break）
        foreach (var rule in rules.PrimaryAffixes)
        {
            foreach (var line in affixLines)
            {
                if (rule.Matches(line))
                {
                    result.PrimaryHits++;
                    result.MatchedPrimary.Add(line);
                    break;
                }
            }
        }

        // 3. 次级词缀
        foreach (var rule in rules.SecondaryAffixes)
        {
            foreach (var line in affixLines)
            {
                if (rule.Matches(line))
                {
                    result.SecondaryHits++;
                    result.MatchedSecondary.Add(line);
                    break;
                }
            }
        }

        return result;
    }
}
