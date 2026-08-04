namespace ShiKe.Tools.Craft;

/// <summary>洗装模式（对齐 Python models.py Mode，IntEnum 值保留兼容旧版 rules.json）。</summary>
public enum CraftMode
{
    Single = 1,        // 单通货
    AltAug = 2,        // 改造+增幅
    AltAugRegal = 3,   // 改造+增幅+富豪
}

/// <summary>通货类型与布局（对齐 Python models.py CurrencyType + config_tab.py all_currencies）。</summary>
public static class Currency
{
    public const string Alteration = "alteration";
    public const string Augmentation = "augmentation";
    public const string Alchemy = "alchemy";
    public const string Chaos = "chaos";
    public const string Exalted = "exalted";
    public const string Divine = "divine";
    public const string Regal = "regal";
    public const string Scouring = "scouring";
    public const string Transmutation = "transmutation";
    public const string Custom = "custom";

    public static readonly Dictionary<string, string> Labels = new()
    {
        [Alteration] = "改造石",
        [Augmentation] = "增幅石",
        [Alchemy] = "点金石",
        [Chaos] = "混沌石",
        [Exalted] = "崇高石",
        [Divine] = "神圣石",
        [Regal] = "富豪石",
        [Scouring] = "重铸石",
        [Transmutation] = "蜕变石",
        [Custom] = "自定义通货",
    };

    /// <summary>网格顺序（4 列，对齐 config_tab.py all_currencies；神圣石占位永不显示）。</summary>
    public static readonly string[] All =
    [
        Alteration, Augmentation, Alchemy, Scouring,
        Chaos, Custom,
        Regal, Transmutation, Exalted,
        Divine,
    ];

    /// <summary>每模式显示子集（对齐 models.py MODE_CURRENCIES；点金不在 Mode2 子集但属全局网格，方案 §6）。</summary>
    public static readonly Dictionary<CraftMode, string[]> ModeCurrencies = new()
    {
        [CraftMode.Single] = [Alteration, Chaos, Custom],
        [CraftMode.AltAug] = [Alteration, Augmentation, Alchemy, Scouring],
        [CraftMode.AltAugRegal] = [Alteration, Augmentation, Regal, Scouring, Transmutation, Exalted],
    };

    public static string Label(string key) => Labels.TryGetValue(key, out var l) ? l : key;
}

/// <summary>单条词缀规则——文本子串匹配（对齐 Python AffixRule.matches；DEV_GUIDE §45-52 的数值范围未实现）。</summary>
public sealed class AffixRule
{
    public string Text { get; init; } = "";

    public bool Matches(string affixLine)
        => Text.Length > 0 && affixLine.Contains(Text, StringComparison.Ordinal);
}

/// <summary>完整洗装规则（对齐 Python CraftRules + validate）。</summary>
public sealed class CraftRules
{
    public CraftMode Mode { get; set; } = CraftMode.Single;
    public string SingleCurrency { get; set; } = Currency.Alteration;

    public List<AffixRule> PrimaryAffixes { get; } = [];
    public int PrimaryHitCount { get; set; } = 1;

    public List<AffixRule> SecondaryAffixes { get; } = [];
    public int SecondaryHitCount { get; set; } = 0;

    public List<AffixRule> ExcludeAffixes { get; } = [];

    /// <summary>验证规则约束（对齐 Python validate；primary_hit_count=0 合法，方案 D5）。</summary>
    public (bool Ok, string Message) Validate()
    {
        if (PrimaryHitCount > 0 && PrimaryAffixes.Count == 0)
            return (false, $"主词缀命中数要求 {PrimaryHitCount}，但主词缀池为空");
        if (PrimaryHitCount > PrimaryAffixes.Count)
            return (false, $"主词缀命中数 {PrimaryHitCount} 超过主词缀池数量 {PrimaryAffixes.Count}");
        if (SecondaryHitCount > 0 && SecondaryAffixes.Count == 0)
            return (false, $"次级词缀命中数要求 {SecondaryHitCount}，但次级词缀池为空");
        if (SecondaryHitCount > SecondaryAffixes.Count)
            return (false, $"次级词缀命中数 {SecondaryHitCount} 超过次级词缀池数量 {SecondaryAffixes.Count}");

        var total = PrimaryHitCount + SecondaryHitCount;
        if (Mode == CraftMode.AltAug && total > 2)
            return (false, "Mode 2 总词缀命中数不能超过 2");
        if (Mode == CraftMode.AltAugRegal && total > 3)
            return (false, "Mode 3 总词缀命中数不能超过 3");
        return (true, "");
    }
}

/// <summary>词缀检查结果（对齐 Python AffixCheckResult）。</summary>
public sealed class AffixCheckResult
{
    public int PrimaryHits { get; set; }
    public int SecondaryHits { get; set; }
    public bool HasExclude { get; set; }
    public List<string> MatchedPrimary { get; } = [];
    public List<string> MatchedSecondary { get; } = [];
    public List<string> MatchedExclude { get; } = [];

    public int TotalHits => PrimaryHits + SecondaryHits;

    /// <summary>终检（对齐 Python meets_final_rules；排除=失败，铁律 #7 终检语义）。</summary>
    public bool MeetsFinalRules(CraftRules rules)
    {
        if (HasExclude) return false;
        if (PrimaryHits < rules.PrimaryHitCount) return false;
        if (SecondaryHits < rules.SecondaryHitCount) return false;
        return true;
    }

    public bool HasAnyHit => PrimaryHits > 0 || SecondaryHits > 0;
}
