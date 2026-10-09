using ShiKe.Services;
using ShiKe.Tools.Craft;

// 合成完整物品文本包住用户提供的原始词缀；验证读取门禁、解析、匹配和通货判定，不发送输入。
internal static class ItemParsingChecks
{
    private const string SocketDescription = "插槽中寶石增加 30% 保留效用 — 無法使用的值";
    private const string SocketAffix = """
        { 後綴 "塑者之"(階層: 1)— 寶石 }
        插槽中寶石增加 30% 保留效用 — 無法使用的值
        """;
    private const string LifeAffix = """
        { 前綴 "生命之"(階層: 1) — 生命 }
        +80 最大生命
        """;
    private const string ArmourAffix = """
        { 前綴屬性 "堅固的"(階層: 2) — 防禦 }
        +100 點護甲
        """;

    public static void SocketAffixIsMatched()
    {
        var parsed = AffixEngine.ParseItem(TraditionalItem("魔法", SocketAffix));
        Require(parsed.MatchLines.Contains(SocketDescription), "截图原文必须进入匹配语料");
        Equal(1, parsed.ExplicitAffixCount, "截图中的後綴头必须识别为一条显式词缀");
        Require(parsed.ExplicitAffixes.Single().DescriptionLines.Contains(SocketDescription),
            "原文必须保留在显式词缀正文中");
        Equal(1, AffixEngine.GetAffixCount(TraditionalItem("魔法", SocketAffix)), "兼容计数入口必须一致");

        var rules = Rules("保留效用");
        var result = AffixEngine.CheckAffixes(parsed, rules);
        Equal(1, result.PrimaryHits, "保留效用必须命中");
        Require(result.MeetsFinalRules(rules), "单通货要求一命中时应通过终检");
    }

    public static void MetadataRequiresFieldBoundary()
    {
        string[] descriptions =
        [
            SocketDescription, "插槽中宝石增加 30% 保留效用", "物理伤害提高 55%", "物理傷害提高 55%",
            "品质提高 10%", "品質提高 10%", "火焰伤害提高 10%", "閃電傷害提高 10%",
            "Physical Damage increased by 55%", "Sockets grant additional bonuses",
            "插槽工匠之盔", "物品等级之剑",
        ];
        string[] metadata =
        [
            "插槽: G-G", "插槽 ： G-G", "插槽\t: G-G", "物理伤害: 78-188", "物理傷害：78-188",
            "品质: +20%", "品質：+20%", "火焰伤害: 10-20", "閃電傷害：10-20",
            "Physical Damage: 78-188", "Sockets: G-G", "Sockets : G-G", "物品等级: 85", "需求",
            "稀 有 度: 魔法", "物品类别: 头盔", "等级: 70", "敏捷: 40", "力量: 40", "智慧: 40",
            "火焰，冰霜，闪电伤害: 1-2", "冰霜伤害: 1-2", "闪电伤害: 1-2",
            "攻击暴击率: 6.50%", "每秒攻击次数: 1.50", "出售获得通货: 非绑定",
            "Rarity: Magic", "Item Class: Helmets", "Item Level: 85", "Requirements:", "Requires Level 70",
            "Level: 70", "Quality: +20%", "Elemental Damage: 1-2", "Fire Damage: 1-2", "Cold Damage: 1-2",
            "Lightning Damage: 1-2", "Critical Strike Chance: 6.50%", "Attacks per Second: 1.50",
            "Vendor: Test", "Vendor Price: Test",
        ];
        var parsed = AffixEngine.ParseItem(string.Join('\n', descriptions.Concat(metadata)));
        foreach (var line in descriptions)
            Require(parsed.MatchLines.Contains(line), $"真实正文或装备名被误过滤：{line}");
        foreach (var line in metadata)
            Require(!parsed.MatchLines.Contains(line), $"面板元数据未过滤：{line}");
    }

    public static void TraditionalMetadataIsFiltered()
    {
        string[] metadata =
        [
            "物品類別: 頭盔", "物品種類：頭盔", "稀 有 度: 魔法", "物品等級: 85", "需求:",
            "等級: 70", "敏捷: 40", "力量：40", "智慧 : 40", "插槽: G-G",
            "物理傷害: 78-188", "火焰，冰霜，閃電傷害: 1-2", "火焰傷害: 1-2",
            "冰霜傷害: 1-2", "閃電傷害: 1-2", "攻擊暴擊率: 6.50%", "每秒攻擊次數: 1.50",
            "出售獲得通貨: 非綁定", "品質: +20%",
        ];
        var parsed = AffixEngine.ParseItem(string.Join('\n', metadata));
        Equal(0, parsed.MatchLines.Count, "所有繁体元数据必须从语料排除");
        Equal(0, parsed.MatchCandidates.Count, "元数据不得形成匹配候选");
        Equal(0, parsed.ExplicitAffixCount, "元数据不得计为显式词缀");

        var rules = Rules("等級");
        rules.SecondaryAffixes.Add(new AffixRule { Text = "傷害" });
        rules.ExcludeAffixes.Add(new AffixRule { Text = "通貨" });
        var result = AffixEngine.CheckAffixes(parsed, rules);
        Equal(0, result.PrimaryHits, "物品等級不应误命中主池");
        Equal(0, result.SecondaryHits, "面板傷害不应误命中次池");
        Require(!result.HasExclude, "出售通貨不应误命中排除池");
    }

    public static void ExplicitHeadersAreCounted()
    {
        string[] headers =
        [
            "{ 前缀属性 \"测试\" (等阶：1) — 伤害 }", "{ 后缀属性 \"测试\" (等阶：1) — 伤害 }",
            "{ 前綴 \"測試\"(階層: 1) — 寶石 }", "{ 後綴 \"測試\"(階層: 1) — 寶石 }",
            "{ 前綴屬性 \"測試\" (階層：1) — 傷害 }", "{ 後綴屬性 \"測試\" (階層：1) — 傷害 }",
            "{前綴屬性\"測試\" (階層：1) — 傷害 }",
            "{ Prefix Modifier \"Test\" (Tier: 1) — Damage }", "{ Suffix Modifier \"Test\" (Tier: 1) — Damage }",
        ];
        foreach (var header in headers)
        {
            var parsed = AffixEngine.ParseItem($"{header}\n+80 最大生命");
            Equal(1, parsed.ExplicitAffixCount, $"词缀头未识别：{header}");
            Equal(1, parsed.MatchCandidates.Count, "头和正文应合为一个候选");
        }
    }

    public static void OtherHeadersAreNotCounted()
    {
        const string text = """
            { 基底屬性 "前綴" (階層: 1) — 傷害 }
            +10 最大生命
            { 基底属性 "前缀" (等阶: 1) — 伤害 }
            +10 最大生命
            { 附魔屬性 "後綴" (階層: 1) — 寶石 }
            +10 最大生命
            { 固定屬性 "後綴" (階層: 1) — 寶石 }
            +10 最大生命
            { 前綴說明 "測試" — 寶石 }
            +10 最大生命
            """;
        Equal(0, AffixEngine.ParseItem(text).ExplicitAffixCount,
            "基底、附魔、固定和说明头都不能算成显式前后缀");
    }

    public static void TraditionalAffixIsConsumedOnce()
    {
        var parsed = AffixEngine.ParseItem(TraditionalItem("魔法", SocketAffix));
        var rules = Rules("塑者之");
        rules.Mode = CraftMode.AltAug;
        rules.PrimaryAffixes.Add(new AffixRule { Text = "保留效用" });
        rules.SecondaryHitCount = 1;
        rules.SecondaryAffixes.Add(new AffixRule { Text = "寶石" });
        var result = AffixEngine.CheckAffixes(parsed, rules);
        Equal(1, result.PrimaryHits, "装备名、头和正文描述同一词缀，只能贡献一命中");
        Equal(0, result.SecondaryHits, "次池不得再次消费相同词缀");
        Require(!result.MeetsFinalRules(rules), "一条词缀不能满足主一次一");
        Equal(1, AffixEngine.CheckAffixes(parsed, Rules("獅皮帽")).PrimaryHits,
            "独立装备名和基础类型必须继续参与宽松匹配");
    }

    public static void TraditionalClipboardHeadersAreAccepted()
    {
        foreach (var header in new[] { "物品类别:", "物品類別:", "物品種類：", "物品類別 ：", "Item Class:" })
            Require(ClipboardHelper.IsItemText($"\r\n {header} 頭盔\n稀有度: 魔法"), $"物品头被拒绝：{header}");
        foreach (var text in new[] { "物品類別說明: 頭盔", "普通文本\n物品類別: 頭盔", "物品類別", "稀有度: 魔法" })
            Require(!ClipboardHelper.IsItemText(text), $"非物品头被误接受：{text}");
    }

    public static void RarityFormatsAreParsed()
    {
        foreach (var (text, expected) in new[]
        {
            ("稀 有 度: 普通", ItemRarity.Normal), ("稀有度：魔法", ItemRarity.Magic),
            ("稀有度: 稀有", ItemRarity.Rare), ("稀有度: 传奇", ItemRarity.Unique),
            ("稀有度 ： 傳奇", ItemRarity.Unique), ("Rarity: Unique", ItemRarity.Unique),
            ("稀有度: 未知", ItemRarity.Unknown),
        })
            Equal(expected, AffixEngine.ParseItem(text).Rarity, $"稀有度解析错误：{text}");
        Equal(Mode3StartDecision.StopUnsupported,
            CraftDecisions.BeforeMode3(AffixEngine.ParseItem("稀有度: 傳奇").Rarity),
            "繁体传奇仍须拒绝自动洗装");
    }

    public static void TraditionalCurrencyFlow()
    {
        var normal = TraditionalItem("普通");
        var magic1 = TraditionalItem("魔法", SocketAffix);
        var magic2 = TraditionalItem("魔法", SocketAffix, LifeAffix);
        var rare3 = TraditionalItem("稀有", SocketAffix, LifeAffix, ArmourAffix);
        var rules = Rules("保留效用");
        rules.Mode = CraftMode.AltAugRegal;
        rules.PrimaryHitCount = 2;
        rules.PrimaryAffixes.Add(new AffixRule { Text = "最大生命" });
        rules.SecondaryHitCount = 1;
        rules.SecondaryAffixes.Add(new AffixRule { Text = "護甲" });
        var threshold = rules.PrimaryHitCount + rules.SecondaryHitCount - 1;

        var parsed1 = AffixEngine.ParseItem(magic1);
        var result1 = AffixEngine.CheckAffixes(parsed1, rules);
        Equal(Mode3MagicDecision.UseAugmentation,
            CraftDecisions.AfterAlteration(parsed1.ExplicitAffixCount, result1.PrimaryHits + result1.SecondaryHits, threshold),
            "一条繁体词缀命中应选择增幅");
        var parsed2 = AffixEngine.ParseItem(magic2);
        var result2 = AffixEngine.CheckAffixes(parsed2, rules);
        Equal(Mode3MagicDecision.ProceedToRegal,
            CraftDecisions.AfterAlteration(parsed2.ExplicitAffixCount, result2.PrimaryHits + result2.SecondaryHits, threshold),
            "两条繁体词缀达阈值应选择富豪");
        Require(!result2.MeetsFinalRules(rules), "魔法阶段主2次0还未满足主2次1终检");
        Require(AffixEngine.CheckAffixes(AffixEngine.ParseItem(rare3), rules).MeetsFinalRules(rules),
            "富豪后主2次1应通过终检");

        CheckTransition(CraftCurrencyOperation.Transmutation, normal, magic1, true);
        CheckTransition(CraftCurrencyOperation.Augmentation, magic1, magic2, true);
        CheckTransition(CraftCurrencyOperation.Regal, magic2, rare3, true);
        CheckTransition(CraftCurrencyOperation.Scouring, rare3, normal, true);
        CheckTransition(CraftCurrencyOperation.Augmentation, magic2, magic2, false);
        CheckTransition(CraftCurrencyOperation.Augmentation, magic2, rare3, false);
        CheckTransition(CraftCurrencyOperation.Regal, magic1, rare3, false);
    }

    public static void TraditionalExclusionTakesPriority()
    {
        var rules = Rules("保留效用");
        rules.ExcludeAffixes.Add(new AffixRule { Text = "無法使用的值" });
        var result = AffixEngine.CheckAffixes(AffixEngine.ParseItem(TraditionalItem("魔法", SocketAffix)), rules);
        Require(result.HasExclude, "插槽词缀的排除关键词必须命中");
        Equal(0, result.PrimaryHits, "排除命中后不应再累计主命中");
        Require(!result.MeetsFinalRules(rules), "排除命中不能通过终检");
    }

    private static string TraditionalItem(string rarity, params string[] affixes) => $"""
        物品類別: 頭盔
        稀有度: {rarity}
        塑者之 獅皮帽
        獅皮帽
        --------
        需求:
        等級: 70
        智慧: 40
        --------
        插槽: G-G-G-R
        物品等級: 85
        --------
        {string.Join('\n', affixes)}
        --------
        """;

    private static CraftRules Rules(string primary)
    {
        var rules = new CraftRules { PrimaryHitCount = 1 };
        rules.PrimaryAffixes.Add(new AffixRule { Text = primary });
        return rules;
    }

    private static void CheckTransition(CraftCurrencyOperation operation, string before, string after, bool expected)
    {
        Require(ClipboardHelper.IsItemText(before) && ClipboardHelper.IsItemText(after), "繁体样本须先通过读取门禁");
        var parsedBefore = AffixEngine.ParseItem(before);
        var parsedAfter = AffixEngine.ParseItem(after);
        var check = CraftStateSync.CheckTransition(operation,
            new CraftItemState(parsedBefore.Rarity, parsedBefore.ExplicitAffixCount),
            new CraftItemState(parsedAfter.Rarity, parsedAfter.ExplicitAffixCount),
            CraftStateSync.HasItemStateChanged(before, after));
        Equal(expected, check.Accepted, $"繁体转换错误 {operation}: {check.Reason}");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}；预期 {expected}，实际 {actual}");
    }
}
