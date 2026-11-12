using System.Windows;
using ShiKe.Tools.Craft;

// 庄园工艺：通货道具只按名称匹配、装备路径不变、工艺按钮坐标必备。
// 样本全部取自用户实机复制的剪贴板原文，不合成字段。
internal static class HarvestChecks
{
    private const string Fossil = """
        物品类别: 可堆叠通货
        稀 有 度: 通货
        锯齿化石
        --------
        堆叠数量: 20 / 20
        --------
        更多物理属性
        无混沌属性
        --------
        放置在共振器中来影响打造物品。
        按住 Shift 点击以分开堆叠
        """;

    private const string Essence = """
        物品类别: 可堆叠通货
        稀 有 度: 通货
        贪婪之破空精华
        --------
        堆叠数量: 10 / 10
        --------
        确保一项特定属性, 升级一件普通物品至稀有物品或者重置一件稀有物品的属性.
        箭袋: 物理攻击伤害的 (1.1-1.3)% 会转化为生命偷取
        武器: 物理攻击伤害的 (4.1-4.4)% 会转化为生命偷取
        手套: (91-105) 最大生命
        鞋子: (91-105) 最大生命
        头部: (91-105) 最大生命
        盾: (130-144) 最大生命
        其他 护具: (160-174) 最大生命
        腰带: 药剂生命回复速度加快 (32-35)%
        其他 饰品: 每秒再生 (48.1-64) 生命
        --------
        右键点击此物品，再左键点击一件普通或稀有物品来使用。 三个同阶同类精华可以出售给NPC获得高一阶精华。
        按住 Shift 点击以分开堆叠
        """;

    private const string Catalyst = """
        物品类别: 可堆叠通货
        稀 有 度: 通货
        研磨催化剂
        --------
        堆叠数量: 20 / 20
        --------
        给戒指、项链、腰带添加品质，强化攻击类词缀。
        取代其它品质类型。
        --------
        右键点击此物品，随后左键点击一件戒指、项链或腰带来使用。对物品等级低的非传奇饰品效果更佳。最大品质为 20%。
        按住 Shift 点击以分开堆叠
        """;

    private const string Oil = """
        物品类别: 可堆叠通货
        稀 有 度: 通货
        清澈圣油
        --------
        堆叠数量: 10 / 10
        --------
        可在卡西娅修女处同其它圣油一起合用，给戒指或项链附魔，也可以用来修正菌潮地图。
        按住 Shift 点击以分开堆叠
        """;

    private const string RareHelmet = """
        物品类别: 头盔
        稀 有 度: 稀有
        末日之冠
        铁帽
        --------
        品质: +20% (augmented)
        --------
        需求:
        等级: 68
        力量: 100
        --------
        插槽: G-G-G
        物品等级: 86
        --------
        { 前缀属性 "生命之"(等阶: 1) — 生命 }
        +95 最大生命
        { 后缀属性 "防护之"(等阶: 1) — 防御 }
        +40% 火焰抗性
        --------
        """;

    /// <summary>四个道具都必须识别成通货，名称取名字行，其余正文一条都不进匹配语料。</summary>
    public static void CurrencyItemsMatchByNameOnly()
    {
        foreach (var (text, name) in new[]
                 {
                     (Fossil, "锯齿化石"), (Essence, "贪婪之破空精华"),
                     (Catalyst, "研磨催化剂"), (Oil, "清澈圣油"),
                 })
        {
            var parsed = AffixEngine.ParseItem(text);
            Equal(ItemRarity.Currency, parsed.Rarity, $"{name} 稀有度必须是通货");
            Equal(ItemKind.Currency, parsed.Kind, $"{name} 必须归为道具");
            Equal(name, parsed.ItemName, $"{name} 名称行解析错误");
            Equal(1, parsed.MatchLines.Count, $"{name} 只能有名称一条匹配语料");
            Equal(name, parsed.MatchLines[0], $"{name} 匹配语料必须就是名称");
            Equal(1, parsed.MatchCandidates.Count, $"{name} 只能有一个匹配候选");
            Equal(0, parsed.ExplicitAffixCount, $"{name} 不应产生显式词缀");
        }
    }

    /// <summary>道具按名称前缀识别：输入「贪婪」命中「贪婪之破空精华」。</summary>
    public static void CurrencyNamePrefixHits()
    {
        var parsed = AffixEngine.ParseItem(Essence);
        var rules = HarvestRules("贪婪");
        var result = AffixEngine.CheckAffixes(parsed, rules);
        Equal(1, result.PrimaryHits, "名称前缀必须命主池");
        Equal(0, result.SecondaryHits, "道具不应命上次池");
        Require(result.MeetsFinalRules(rules), "主命中 1 即应通过终检");

        // 另一枚精华名称不同，前缀不应互相命中。
        var oil = AffixEngine.CheckAffixes(AffixEngine.ParseItem(Oil), HarvestRules("贪婪"));
        Equal(0, oil.PrimaryHits, "不同道具的名称前缀不能互相命中");
        Require(!oil.MeetsFinalRules(HarvestRules("贪婪")), "未命中不能通过终检");
    }

    /// <summary>道具正文的属性清单最容易误命中，现在完全不参与匹配。</summary>
    public static void CurrencyDescriptionsDoNotPolluteMatching()
    {
        // 精华正文里成段出现「最大生命」；它描述的是精华能做什么，不是这件物品的词缀。
        Equal(0, AffixEngine.CheckAffixes(AffixEngine.ParseItem(Essence), HarvestRules("最大生命")).PrimaryHits,
            "精华的属性清单不得命中主池");

        // 之前这两个排除词会把精华/催化剂直接判死。
        Require(!AffixEngine.CheckAffixes(AffixEngine.ParseItem(Essence), HarvestRules("贪婪", exclude: "生命")).HasExclude,
            "精华正文的「生命偷取」不得命中排除池");
        Require(!AffixEngine.CheckAffixes(AffixEngine.ParseItem(Catalyst), HarvestRules("研磨", exclude: "品质")).HasExclude,
            "催化剂正文的「添加品质」不得命中排除池");

        // 排除仍然作用于名称本身。
        Require(AffixEngine.CheckAffixes(AffixEngine.ParseItem(Oil), HarvestRules("清澈", exclude: "圣油")).HasExclude,
            "名称命中排除词时仍须判失败");
    }

    /// <summary>庄园工艺也用来洗装备，装备的解析与匹配必须与原来完全一致。</summary>
    public static void EquipmentParsingStaysUnchanged()
    {
        var parsed = AffixEngine.ParseItem(RareHelmet);
        Equal(ItemRarity.Rare, parsed.Rarity, "装备稀有度保持不变");
        Equal(ItemKind.Equipment, parsed.Kind, "装备必须归为装备");
        Equal("末日之冠", parsed.ItemName, "装备名称行解析错误");
        Equal(2, parsed.ExplicitAffixCount, "两条显式词缀必须照常计数");
        Require(parsed.MatchLines.Contains("铁帽"), "基础类型仍须参与宽松匹配");

        var rules = HarvestRules("最大生命");
        rules.PrimaryAffixes.Add(new AffixRule { Text = "火焰抗性" });
        rules.PrimaryHitCount = 2;
        var result = AffixEngine.CheckAffixes(parsed, rules);
        Equal(2, result.PrimaryHits, "装备的主词缀必须照常命中");
        Require(result.MeetsFinalRules(rules), "装备主命中 2 应通过终检");
    }

    /// <summary>庄园工艺不用通货，只需要工艺按钮与物品区域两个坐标。</summary>
    public static void HarvestCoordinatesRequireBench()
    {
        var rules = new CraftRules { Mode = CraftMode.Harvest };
        var onlyItem = new Dictionary<string, Point> { [CraftSlot.Item] = new(100, 200) };
        Equal("工艺按钮坐标", CraftEngine.CheckCoordinates(rules, onlyItem, false, false),
            "缺少工艺按钮坐标必须拒绝启动");

        var both = new Dictionary<string, Point>
        {
            [CraftSlot.Item] = new(100, 200),
            [CraftSlot.HarvestBench] = new(300, 400),
        };
        Require(CraftEngine.CheckCoordinates(rules, both, false, false) is null, "两个坐标齐全时必须允许启动");

        var noItem = new Dictionary<string, Point> { [CraftSlot.HarvestBench] = new(300, 400) };
        Equal("装备坐标", CraftEngine.CheckCoordinates(rules, noItem, false, false),
            "缺少物品区域坐标必须拒绝启动");
    }

    /// <summary>庄园工艺沿用可配置命中数，且不显示任何通货。</summary>
    public static void HarvestRulesUseConfiguredHits()
    {
        var rules = HarvestRules("锯齿化石");
        rules.PrimaryHitCount = 1;
        Require(rules.Validate().Ok, "主命中 1 的庄园工艺规则必须通过校验");
        Equal(1, rules.RequiredPrimaryHits, "庄园工艺不能像单通货那样强制覆盖命中数");
        Equal(0, rules.RequiredSecondaryHits, "庄园工艺次池默认 0");

        rules.PrimaryHitCount = 2;
        rules.PrimaryAffixes.Add(new AffixRule { Text = "催化剂" });
        Require(rules.Validate().Ok, "庄园工艺保留多词缀装备用法");

        Equal(0, Currency.ModeCurrencies[CraftMode.Harvest].Length, "庄园工艺不显示通货卡片");
    }

    private static CraftRules HarvestRules(string primary, string? exclude = null)
    {
        var rules = new CraftRules { Mode = CraftMode.Harvest, PrimaryHitCount = 1, SecondaryHitCount = 0 };
        rules.PrimaryAffixes.Add(new AffixRule { Text = primary });
        if (exclude is not null) rules.ExcludeAffixes.Add(new AffixRule { Text = exclude });
        return rules;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}；预期 {expected}，实际 {actual}");
    }
}
