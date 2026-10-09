using System.Text.RegularExpressions;

namespace ShiKe.Tools.Craft;

/// <summary>
/// 词缀解析与匹配引擎。
/// 行为按 DEV_GUIDE §60-98 + 方案 §6 铁律实现（用户 2026-08-04 拍板：装备名字参与匹配）：
/// - 按行处理，跳过空行与 -------- 分段符
/// - 按字段边界过滤简繁中文和英文元数据行（铁律 #3）
/// - ⚠️ 装备名字/基础类型必须保留参与匹配（铁律 #1：宽松匹配是设计意图，不过滤）
/// - 匹配语料与真实词缀数量分离：装备名可匹配，但不计入前/后缀数量
/// - 匹配：排除优先 → 主 → 次；同一实际候选在主/次词缀池之间也只能贡献一次；子串匹配，无数值范围（未实现功能）
/// </summary>
public static class AffixEngine
{
    /// <summary>
    /// 元数据字段（DEV_GUIDE §66-88 完整列表 + 品质）。只匹配字段名后的冒号，
    /// 不能因正文以“插槽”“物理伤害”等字样开头而吞掉整条词缀。
    /// </summary>
    private static readonly string[] SkipPatterns = new[]
    {
        // 简繁中文。只兼容元数据字段的字形，匹配正文保持原文，不自动转换用户规则。
        @"^稀\s*有\s*度",
        @"^物品(?:[类類][别別]|種類)",
        @"^物品等[级級]",
        @"^需求",
        @"^等[级級]",
        @"^敏捷", @"^力量", @"^智慧",
        @"^插槽",
        @"^物理[伤傷]害",
        @"^火焰，冰霜，[闪閃][电電][伤傷]害",
        @"^火焰[伤傷]害", @"^冰霜[伤傷]害", @"^[闪閃][电電][伤傷]害",
        @"^攻[击擊]暴[击擊]率",
        @"^每秒攻[击擊]次[数數]",
        @"^出售[获獲]得通[货貨]",
        @"^品[质質]",
        // 英文
        @"^Rarity", @"^Item Class", @"^Item Level", @"^Requirements", @"^Level",
        @"^Sockets", @"^Quality", @"^Physical Damage", @"^Elemental Damage",
        @"^Fire Damage", @"^Cold Damage", @"^Lightning Damage",
        @"^Critical Strike Chance", @"^Attacks per Second",
    }.Select(pattern => pattern + @"\s*[:：]")
     // 需求分节可以独占一行；英文旧格式的 Requires / Vendor 值可以直接跟在空格后。
     .Concat(new[] { @"^需求$", @"^Requires(?:\s|[:：]|$)", @"^Vendor(?:\s|[:：]|$)" })
     .ToArray();

    /// <summary>
    /// 解析物品文本为双视图：MatchLines 用于宽松匹配；ExplicitAffixes 用于真实词缀计数。
    /// 高级描述中的显式词缀头形如 { 前缀属性 ... } / { 後綴 ... } / { Prefix Modifier ... }。
    /// </summary>
    public static ItemParseResult ParseItem(string clipboardText)
    {
        var result = new ItemParseResult();
        var rawLines = clipboardText.Split('\n');
        var matchLinesByIndex = new Dictionary<int, string>();

        foreach (var rawLine in rawLines)
        {
            var rarity = ParseRarity(rawLine.Trim());
            if (rarity == ItemRarity.Unknown) continue;
            result.Rarity = rarity;
            break;
        }

        // 匹配视图：保留装备名、基础类型、显式词缀头及描述；过滤元数据和说明注释。
        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("--------")) continue;   // 分段符
            if (IsSkipped(line)) continue;               // 元数据行
            if (IsParentheticalNote(line)) continue;      // 机制说明，不是物品词缀
            result.MatchLines.Add(line);
            matchLinesByIndex[i] = line;
        }

        // 计数视图：只按明确的前缀/后缀属性头计数，基础词缀/隐式词缀不计入通货规则的 1/2 条判断。
        var explicitLineIndices = new HashSet<int>();
        var explicitCandidatesByStartIndex = new Dictionary<int, ItemMatchCandidate>();
        for (var i = 0; i < rawLines.Length; i++)
        {
            var header = rawLines[i].Trim();
            if (!IsExplicitAffixHeader(header)) continue;

            var affix = new ParsedItemAffix { Header = header };
            explicitLineIndices.Add(i);
            for (var j = i + 1; j < rawLines.Length; j++)
            {
                var description = rawLines[j].Trim();
                if (description.Length == 0 || description.StartsWith("--------") || description.StartsWith('{'))
                    break;
                if (IsSkipped(description) || IsParentheticalNote(description))
                    continue;
                affix.DescriptionLines.Add(description);
                explicitLineIndices.Add(j);
            }
            result.ExplicitAffixes.Add(affix);
            explicitCandidatesByStartIndex[i] = new ItemMatchCandidate
            {
                DisplayText = affix.SearchText,
                SearchText = affix.SearchText,
                IsExplicitAffix = true,
            };
        }

        // 匹配单元保持原始顺序。一个显式词缀的属性头+描述合并为一个候选，
        // 因此同一词缀不能被同一规则池里的多条规则重复消费。
        for (var i = 0; i < rawLines.Length; i++)
        {
            if (explicitCandidatesByStartIndex.TryGetValue(i, out var affixCandidate))
            {
                result.MatchCandidates.Add(affixCandidate);
                continue;
            }
            if (explicitLineIndices.Contains(i)) continue;
            if (!matchLinesByIndex.TryGetValue(i, out var line)) continue;
            result.MatchCandidates.Add(new ItemMatchCandidate { DisplayText = line, SearchText = line });
        }

        return result;
    }

    /// <summary>兼容现有调用：返回用于宽松匹配的文本行。</summary>
    public static List<string> ParseItemText(string clipboardText) => ParseItem(clipboardText).MatchLines;

    /// <summary>真实显式前/后缀总数。</summary>
    public static int GetAffixCount(string itemText) => ParseItem(itemText).ExplicitAffixCount;

    private static ItemRarity ParseRarity(string line)
    {
        var chinese = Regex.Match(line, @"^稀\s*有\s*度\s*[:：]\s*(\S+)");
        if (chinese.Success)
        {
            return chinese.Groups[1].Value switch
            {
                "普通" => ItemRarity.Normal,
                "魔法" => ItemRarity.Magic,
                "稀有" => ItemRarity.Rare,
                "传奇" or "傳奇" => ItemRarity.Unique,
                _ => ItemRarity.Unknown,
            };
        }

        var english = Regex.Match(line, @"^Rarity\s*:\s*(\S+)", RegexOptions.IgnoreCase);
        if (!english.Success) return ItemRarity.Unknown;
        return english.Groups[1].Value.ToLowerInvariant() switch
        {
            "normal" => ItemRarity.Normal,
            "magic" => ItemRarity.Magic,
            "rare" => ItemRarity.Rare,
            "unique" => ItemRarity.Unique,
            _ => ItemRarity.Unknown,
        };
    }

    private static bool IsExplicitAffixHeader(string line)
    {
        if (!line.StartsWith('{')) return false;

        // 前/后缀类型必须紧跟左花括号；繁体描述可以没有“屬性”。
        // 階層只是头里的附加信息，不要求它出现，也不把基底/附魔头里的前后缀名字误计数。
        var chinese = Regex.IsMatch(line, @"^\{\s*(?:前[缀綴]|[后後][缀綴])(?:[属屬]性)?(?=\s|[""“])");
        var english = line.Contains("Modifier", StringComparison.OrdinalIgnoreCase) &&
                      (line.Contains("Prefix", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("Suffix", StringComparison.OrdinalIgnoreCase));
        return chinese || english;
    }

    private static bool IsParentheticalNote(string line)
        => (line.StartsWith('(') && line.EndsWith(')')) ||
           (line.StartsWith('（') && line.EndsWith('）'));

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
        => CheckAffixes(
            affixLines.Select(line => new ItemMatchCandidate { DisplayText = line, SearchText = line }).ToList(),
            rules);

    /// <summary>使用结构化解析结果检查规则；显式词缀头和描述作为一个不可重复消费的候选。</summary>
    public static AffixCheckResult CheckAffixes(ItemParseResult parsed, CraftRules rules)
        => CheckAffixes(parsed.MatchCandidates, rules);

    private static AffixCheckResult CheckAffixes(List<ItemMatchCandidate> candidates, CraftRules rules)
    {
        var result = new AffixCheckResult();

        // 1. 排除优先
        foreach (var rule in rules.ExcludeAffixes)
        {
            foreach (var candidate in candidates)
            {
                if (rule.Matches(candidate.SearchText))
                {
                    result.HasExclude = true;
                    result.MatchedExclude.Add(candidate.DisplayText);
                    result.MatchedExcludeRules.Add(rule.Text);
                    return result;
                }
            }
        }

        // 2. 主词缀优先。候选索引在主/次池之间共用，避免同一实际词缀被算两次。
        // 显式词缀块优先于装备名/基础类型等宽松语料：魔法物品名称通常由词缀名生成，
        // 若先消费名称行，再用另一条规则消费同一词缀正文，会把一个实际词缀算成两次。
        var usedCandidates = new HashSet<int>();
        foreach (var rule in rules.PrimaryAffixes)
        {
            var index = FindMatchCandidate(rule, candidates, usedCandidates);
            if (index < 0) continue;
            usedCandidates.Add(index);
            result.PrimaryHits++;
            result.MatchedPrimary.Add(candidates[index].DisplayText);
            result.MatchedPrimaryRules.Add(rule.Text);
        }

        // 3. 次级词缀只能使用尚未被主池消费的实际候选。
        foreach (var rule in rules.SecondaryAffixes)
        {
            var index = FindMatchCandidate(rule, candidates, usedCandidates);
            if (index < 0) continue;
            usedCandidates.Add(index);
            result.SecondaryHits++;
            result.MatchedSecondary.Add(candidates[index].DisplayText);
            result.MatchedSecondaryRules.Add(rule.Text);
        }

        return result;
    }

    private static int FindMatchCandidate(
        AffixRule rule,
        List<ItemMatchCandidate> candidates,
        HashSet<int> usedCandidates)
    {
        // 先把规则绑定到尚未消费的真实词缀块。
        for (var i = 0; i < candidates.Count; i++)
        {
            if (usedCandidates.Contains(i) || !candidates[i].IsExplicitAffix) continue;
            if (rule.Matches(candidates[i].SearchText)) return i;
        }

        // 若规则能命中某个已经消费的真实词缀，它只是该词缀的另一种写法；
        // 不得再退回魔法物品名称行制造第二次命中。
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates[i].IsExplicitAffix) continue;
            if (rule.Matches(candidates[i].SearchText)) return -1;
        }

        // 装备名和基础类型仍然参与匹配（铁律 #1），但只作为没有显式对应时的宽松后备。
        for (var i = 0; i < candidates.Count; i++)
        {
            if (usedCandidates.Contains(i) || candidates[i].IsExplicitAffix) continue;
            if (rule.Matches(candidates[i].SearchText)) return i;
        }

        return -1;
    }
}
