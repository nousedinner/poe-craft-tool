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

/// <summary>
/// 单条词缀规则——文本子串匹配（对齐 Python AffixRule.matches；DEV_GUIDE §45-52 的数值范围未实现）。
/// 国服界面常把“闪电”简称为“电”，因此候选文本额外提供“基础闪电”→“基础电”的匹配别名。
/// </summary>
public sealed class AffixRule
{
    public string Text { get; init; } = "";

    public bool Matches(string affixLine)
    {
        var text = Text.Trim();
        if (text.Length == 0) return false;
        if (affixLine.Contains(text, StringComparison.Ordinal)) return true;

        var normalized = affixLine.Replace("基础闪电", "基础电", StringComparison.Ordinal);
        return normalized.Contains(text, StringComparison.Ordinal);
    }
}

/// <summary>
/// 一条由 Ctrl+Alt+C 高级描述标记出的真实显式词缀。
/// Header/DescriptionLines 可用于诊断；词缀数量只按本集合元素数计算，
/// 不把装备名、基础类型、元数据或说明行误算为词缀。
/// </summary>
public sealed class ParsedItemAffix
{
    public required string Header { get; init; }
    public List<string> DescriptionLines { get; } = [];

    public string SearchText => DescriptionLines.Count == 0
        ? Header
        : $"{Header} {string.Join(' ', DescriptionLines)}";
}

/// <summary>一个不可重复消费的逻辑匹配单元；显式词缀的属性头和描述属于同一个单元。</summary>
public sealed class ItemMatchCandidate
{
    public required string DisplayText { get; init; }
    public required string SearchText { get; init; }
    /// <summary>
    /// 是否来自高级描述中的真实前/后缀块。
    /// 匹配时显式词缀优先；装备名等宽松语料只在规则没有显式对应时作为后备，
    /// 避免魔法物品名称与其显式词缀正文把同一实际词缀重复计数。
    /// </summary>
    public bool IsExplicitAffix { get; init; }
}

/// <summary>从物品剪贴板头解析出的稀有度；用于 Mode 3 启动前安全归一化。</summary>
public enum ItemRarity
{
    Unknown,
    Normal,
    Magic,
    Rare,
    Unique,
}

/// <summary>
/// 物品文本的双视图解析结果：
/// MatchLines 保留装备名/基础类型供宽松匹配；ExplicitAffixes 仅表示真实前后缀。
/// </summary>
public sealed class ItemParseResult
{
    public ItemRarity Rarity { get; set; }
    public List<string> MatchLines { get; } = [];
    public List<ItemMatchCandidate> MatchCandidates { get; } = [];
    public List<ParsedItemAffix> ExplicitAffixes { get; } = [];
    public int ExplicitAffixCount => ExplicitAffixes.Count;
}

/// <summary>Mode 3 魔法阶段下一步；纯判定便于在不发送键鼠输入时验证状态机。</summary>
public enum Mode3MagicDecision
{
    ContinueAlteration,
    UseAugmentation,
    ProceedToRegal,
}

public enum Mode3StartDecision
{
    ContinueFromNormal,
    ScourFirst,
    StopUnsupported,
}

public static class CraftDecisions
{
    /// <summary>
    /// Mode 3 改造得到两条显式词缀但未达到魔法阶段阈值时，记录完整样本用于排查漏识别。
    /// 该策略只影响诊断文件，不改变下一步通货判定。
    /// </summary>
    public static bool ShouldCaptureMode3Miss(int explicitAffixCount, int hits, int threshold, bool hasExclude)
        => !hasExclude && explicitAffixCount >= 2 && hits < threshold;

    /// <summary>命中数按钮的模式上限；Single 模式没有总数限制。</summary>
    public static bool IsHitCountSelectionValid(CraftMode mode, int primaryHitCount, int secondaryHitCount)
    {
        var total = primaryHitCount + secondaryHitCount;
        return mode switch
        {
            CraftMode.AltAug => total <= 2,
            CraftMode.AltAugRegal => total <= 3,
            _ => true,
        };
    }

    /// <summary>非法按钮选择恢复到上一次合法值，而不是意外归零。</summary>
    public static int ResolveHitCountSelection(CraftMode mode, int previousValue, int proposedValue, int otherValue)
        => IsHitCountSelectionValid(
            mode,
            primaryHitCount: proposedValue,
            secondaryHitCount: otherValue)
            ? proposedValue
            : previousValue;

    /// <summary>Mode 3 每次启动都先确认底材状态；蓝/黄装先重铸，其他未知状态拒绝盲点。</summary>
    public static Mode3StartDecision BeforeMode3(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Normal => Mode3StartDecision.ContinueFromNormal,
        ItemRarity.Magic or ItemRarity.Rare => Mode3StartDecision.ScourFirst,
        _ => Mode3StartDecision.StopUnsupported,
    };

    /// <summary>
    /// 蜕变后必须先检查新生成的魔法词缀，再决定直接富豪、增幅或进入改造循环。
    /// 排除词缀在魔法阶段仍使用“跳过并继续改造”的既定语义。
    /// </summary>
    public static Mode3MagicDecision AfterTransmutation(
        int explicitAffixCount, int hits, int threshold, bool hasExclude)
        => hasExclude
            ? Mode3MagicDecision.ContinueAlteration
            : AfterAlteration(explicitAffixCount, hits, threshold);

    /// <summary>改造后：两词缀达到阈值去富豪；一词缀至少一命中才允许增幅。</summary>
    public static Mode3MagicDecision AfterAlteration(int explicitAffixCount, int hits, int threshold)
    {
        if (explicitAffixCount >= 2)
            return hits >= threshold ? Mode3MagicDecision.ProceedToRegal : Mode3MagicDecision.ContinueAlteration;
        if (explicitAffixCount == 1 && hits >= 1)
            return Mode3MagicDecision.UseAugmentation;
        return Mode3MagicDecision.ContinueAlteration;
    }

    /// <summary>增幅后只判断魔法阶段阈值；即使已满足最终规则也必须先经过富豪。</summary>
    public static Mode3MagicDecision AfterAugmentation(int hits, int threshold)
        => hits >= threshold ? Mode3MagicDecision.ProceedToRegal : Mode3MagicDecision.ContinueAlteration;
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

    /// <summary>创建一次洗装运行使用的深拷贝，避免 UI 修改正在执行的规则。</summary>
    public CraftRules CreateSnapshot()
    {
        var snapshot = new CraftRules
        {
            Mode = Mode,
            SingleCurrency = SingleCurrency,
            PrimaryHitCount = PrimaryHitCount,
            SecondaryHitCount = SecondaryHitCount,
        };
        snapshot.PrimaryAffixes.AddRange(PrimaryAffixes.Select(rule => new AffixRule { Text = rule.Text }));
        snapshot.SecondaryAffixes.AddRange(SecondaryAffixes.Select(rule => new AffixRule { Text = rule.Text }));
        snapshot.ExcludeAffixes.AddRange(ExcludeAffixes.Select(rule => new AffixRule { Text = rule.Text }));
        return snapshot;
    }

    /// <summary>验证规则约束（对齐 Python validate；primary_hit_count=0 合法，方案 D5）。</summary>
    public (bool Ok, string Message) Validate()
    {
        if (!Enum.IsDefined(Mode))
            return (false, "洗词缀模式无法识别，请重新选择模式");
        if (PrimaryHitCount < 0 || SecondaryHitCount < 0)
            return (false, "主/次词缀命中数不能为负数");
        var seenRules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (poolName, rules) in new[]
                 {
                     ("主词缀池", PrimaryAffixes),
                     ("次级词缀池", SecondaryAffixes),
                     ("排除词缀池", ExcludeAffixes),
                 })
        {
            foreach (var rule in rules)
            {
                var text = rule.Text.Trim();
                if (text.Length == 0) continue;
                if (seenRules.TryGetValue(text, out var existingPool))
                    return (false, $"词缀「{text}」同时存在于{existingPool}和{poolName}，请只保留一处");
                seenRules[text] = poolName;
            }
        }

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
    public List<string> MatchedPrimaryRules { get; } = [];
    public List<string> MatchedSecondaryRules { get; } = [];
    public List<string> MatchedExcludeRules { get; } = [];

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
