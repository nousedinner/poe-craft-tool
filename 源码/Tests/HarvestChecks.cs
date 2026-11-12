using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Services;
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

    /// <summary>庄园工艺不用通货；工艺区域与背包装备位置不是同一处，必须是独立槽位。</summary>
    public static void HarvestCoordinatesRequireBench()
    {
        var rules = new CraftRules { Mode = CraftMode.Harvest };

        // 只录了背包装备坐标不能启动：工艺台里的物品在另一处。
        var inventoryOnly = new Dictionary<string, Point> { [CraftSlot.Item] = new(100, 200) };
        Equal("工艺按钮", CraftEngine.CheckCoordinates(rules, inventoryOnly, false, false),
            "缺少工艺按钮坐标必须拒绝启动，且不能拿背包装备坐标顶替");

        var benchOnly = new Dictionary<string, Point> { [CraftSlot.HarvestBench] = new(300, 400) };
        Equal("工艺区域", CraftEngine.CheckCoordinates(rules, benchOnly, false, false),
            "缺少工艺区域坐标必须拒绝启动");

        var both = new Dictionary<string, Point>
        {
            [CraftSlot.HarvestArea] = new(100, 200),
            [CraftSlot.HarvestBench] = new(300, 400),
        };
        Require(CraftEngine.CheckCoordinates(rules, both, false, false) is null, "两个工艺坐标齐全时必须允许启动");

        // 庄园工艺不再要求背包装备坐标，反之通货模式也不该被工艺坐标满足。
        Require(!both.ContainsKey(CraftSlot.Item), "庄园工艺的坐标集合不应包含背包装备槽位");
        var single = new CraftRules { Mode = CraftMode.Single, SingleCurrency = Currency.Alteration };
        var harvestOnly = new Dictionary<string, Point>
        {
            [CraftSlot.HarvestArea] = new(100, 200),
            [CraftSlot.HarvestBench] = new(300, 400),
        };
        Equal("改造石坐标", CraftEngine.CheckCoordinates(single, harvestOnly, false, false),
            "通货模式不能借用庄园工艺的坐标");
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

    /// <summary>
    /// rules.json 的写入校验必须接受庄园工艺。否则切到模式 4 后任何一次保存都会失败，
    /// 启动前保存规则时直接报「保存文件失败：rules.json」。
    /// </summary>
    public static void HarvestModeRoundTripsThroughStorage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "shike-harvest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new StorageService(directory);
            storage.SaveRules(new JsonObject
            {
                ["mode"] = (int)CraftMode.Harvest,
                ["primary_affixes"] = new JsonArray("贪婪"),
                ["primary_hit_count"] = 1,
                ["secondary_hit_count"] = 0,
            });
            Equal((int)CraftMode.Harvest, storage.LoadRules()!["mode"]!.GetValue<int>(),
                "庄园工艺模式必须能保存并原样读回");

            storage.SaveRules(new JsonObject { ["mode"] = "harvest" });
            Equal("harvest", storage.LoadRules()!["mode"]!.GetValue<string>(), "字符串形式的庄园工艺也必须被接受");

            var rejected = false;
            try { storage.SaveRules(new JsonObject { ["mode"] = 9 }); }
            catch (StorageException) { rejected = true; }
            Require(rejected, "未知模式仍必须拒绝，放宽校验不能把非法值一起放过");
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private const string RareStaff = """
        物品类别: 法杖
        稀 有 度: 稀有
        活尸 战具
        魔性法杖
        --------
        法杖
        品质: +30% (augmented)
        物理伤害: 156-307 (augmented)
        攻击暴击率: 8.00%
        每秒攻击次数: 2.37 (augmented)
        --------
        需求:
        等级: 66
        智慧: 188 (unmet)
        --------
        插槽: R R
        --------
        物品等级: 84
        --------
        外延物理词缀增幅提高 8% (enchant)
        --------
        { 基底属性 — 伤害, 施法 }
        法术伤害提高 37(33-37)%
        --------
        { 破碎的 前缀属性 "独裁者的" (等阶：1) — 伤害, 物理, 攻击 ——提高 23 }
        物理伤害提高 79(75-79)%
        +189(175-200) 命中值
        { 前缀属性 "锻炼的" (等阶：2) — 伤害, 物理, 攻击 ——提高 23 }
        该装备附加 23(19-25) - 45(39-45) 基础物理伤害
        { 前缀属性 "钴蓝的" (等阶：11) — 魔力 }
        +47(40-49) 最大魔力
        { 后缀属性 "喝采之" (等阶：1) — 攻击, 速度 }
        攻击速度加快 18(17-19)%
        --------
        分裂
        --------
        { 分配熔炉天赋 (等阶：5) }
        该装备附加 6 - 15 基础物理伤害
        { 分配熔炉天赋 (等阶：3) }
        攻击速度加快 30%
        全域伤害总降 15%
        --------
        分裂之物
        --------
        出售获得通货:非绑定
        """;

    /// <summary>
    /// 用户实机样本：筛选「无情／独裁」后第一次转换就报命中，需要能说明命中的是哪一句。
    /// 结论是装备确实带「独裁者的」前缀，规则的「独裁」子串命中了高级描述头那一行。
    /// </summary>
    public static void StaffMatchesDictatorThroughAffixHeader()
    {
        var parsed = AffixEngine.ParseItem(RareStaff);
        Equal(ItemRarity.Rare, parsed.Rarity, "法杖必须仍按装备解析");
        Equal("活尸 战具", parsed.ItemName, "装备名解析错误");

        var dictator = AffixEngine.CheckAffixes(parsed, HarvestRules("独裁"));
        Equal(1, dictator.PrimaryHits, "「独裁」应命中装备上的独裁者的前缀");
        // 命中原行必须就是带词缀名的高级描述头，而不是别的正文。
        Require(dictator.MatchedPrimary.Any(line => line.Contains("独裁者的", StringComparison.Ordinal)),
            "「独裁」命中的行必须包含独裁者的，否则说明是别处误撞");
        Require(dictator.MeetsFinalRules(HarvestRules("独裁")), "主命中 1 应通过终检，这类样本会立即停止");

        // 反证：没有的前缀不能命中，避免"什么都能命中"的误解。
        Equal(0, AffixEngine.CheckAffixes(parsed, HarvestRules("无情")).PrimaryHits, "无情的装备不能命中无情");
        Equal(0, AffixEngine.CheckAffixes(parsed, HarvestRules("锯齿")).PrimaryHits, "无关名称不能命中");

        // 「破碎的 前缀属性」不满足显式词缀头格式，只作为普通语料参与匹配、不计入词缀数，
        // 所以这里只有 锻炼的／钴蓝的／喝采之 三条显式词缀。
        Equal(3, parsed.ExplicitAffixCount, "破碎头不能被当成普通前缀计入词缀数");
    }

    private static CraftRules HarvestRules(string primary, string? exclude = null)    {
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
