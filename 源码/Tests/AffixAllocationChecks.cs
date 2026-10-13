using ShiKe.Tools.Craft;

internal static class AffixAllocationChecks
{
    public static void OverlappingRulesAreIndependentOfOrder()
    {
        foreach (var words in new[] { new[] { "生命", "最大生命" }, new[] { "最大生命", "生命" } })
        foreach (var descriptions in new[]
                 {
                     new[] { "+80 最大生命", "每秒回复 10 生命" },
                     new[] { "每秒回复 10 生命", "+80 最大生命" },
                 })
        {
            var rules = Rules(words);
            rules.PrimaryHitCount = 2;
            var result = AffixEngine.CheckAffixes(Explicit(descriptions), rules);
            Equal(2, result.PrimaryHits, "两个条件应能分别分配到两个真实词缀，不受排列影响");
            Require(result.MeetsFinalRules(rules), "合理配对后应满足主2次0");
        }
    }

    public static void ReassignmentSupportsOverlappingChains()
    {
        var parsed = Explicit("keyA keyB keyC", "keyA", "keyB");
        foreach (var words in Permutations(new[] { "keyA", "keyB", "keyC" }))
        {
            var rules = Rules(words);
            rules.Mode = CraftMode.AltAugRegal;
            rules.PrimaryHitCount = 3;
            Equal(3, AffixEngine.CheckAffixes(parsed, rules).PrimaryHits,
                "必须能把已有宽条件重新分配，给仅有一个候选的条件留出位置");
        }
    }

    public static void SecondaryCanMovePrimaryWithoutDroppingIt()
    {
        var rules = Rules("物理伤害");
        rules.SecondaryAffixes.Add(new AffixRule { Text = "命中值" });
        rules.SecondaryHitCount = 1;
        var result = AffixEngine.CheckAffixes(
            Explicit("物理伤害提高 35%，+100 命中值", "附加 6 - 12 物理伤害"), rules);
        Equal(1, result.PrimaryHits, "给次池让出候选不能丢掉主池命中");
        Equal(1, result.SecondaryHits, "主池有其他独立候选时应允许合理重新分配");
        Require(result.MeetsFinalRules(rules), "应满足两个不同真实词缀的主1次1");
    }

    public static void PrimaryPriorityIsNotCappedByRequiredCount()
    {
        var rules = Rules("物理伤害", "攻击速度");
        rules.SecondaryAffixes.Add(new AffixRule { Text = "命中值" });
        rules.SecondaryHitCount = 1;
        var result = AffixEngine.CheckAffixes(
            Explicit("物理伤害提高 35%，+100 命中值", "增加 12% 攻击速度"), rules);
        Equal(2, result.PrimaryHits, "保持主池优先，不因需求为1而主动丢弃另一主池命中");
        Equal(0, result.SecondaryHits, "主池没有替代位置时不能重复消费已占用词缀");
        Require(!result.MeetsFinalRules(rules), "两次主命中不能冒充主1次1");
    }

    public static void NamesRemainFallbackWithoutDuplicatingModifiers()
    {
        const string text = """
            物品类别: 头盔
            稀有度: 魔法
            测试甲 铁帽
            --------
            { 前缀属性 "测试甲" (等阶: 1) }
            +80 最大生命
            --------
            物品等级: 80
            """;
        var parsed = AffixEngine.ParseItem(text);
        var duplicates = Rules("测试甲", "最大生命");
        duplicates.PrimaryHitCount = 2;
        Equal(1, AffixEngine.CheckAffixes(parsed, duplicates).PrimaryHits,
            "不能把已消费词缀的名字当作第二条真实词缀");
        var fallback = Rules("最大生命", "铁帽");
        fallback.PrimaryHitCount = 2;
        Equal(2, AffixEngine.CheckAffixes(parsed, fallback).PrimaryHits,
            "没有显式对应的装备名/基础类型条件仍能独立参与宽松匹配");
        Equal(1, parsed.ExplicitAffixCount, "宽松名字命中不增加真实词缀数量");
    }

    public static void AllSmallGraphsAgreeWithIndependentOracle()
    {
        // 穷举4条规则（主池2条、次池2条）与3个词缀的4096种关系；参考答案穷举所有独立分配，
        // 先取最大主命中，再取最大次命中，不使用生产匹配算法。
        for (var mask = 0; mask < 1 << 12; mask++)
        {
            var expected = ExhaustiveBest(mask);
            foreach (var primaryOrder in new[] { new[] { 0, 1 }, new[] { 1, 0 } })
            foreach (var secondaryOrder in new[] { new[] { 2, 3 }, new[] { 3, 2 } })
            {
                var rules = new CraftRules { Mode = CraftMode.AltAugRegal, PrimaryHitCount = 2, SecondaryHitCount = 1 };
                foreach (var index in primaryOrder) rules.PrimaryAffixes.Add(new AffixRule { Text = $"key{index}" });
                foreach (var index in secondaryOrder) rules.SecondaryAffixes.Add(new AffixRule { Text = $"key{index}" });
                var parsed = Explicit(Enumerable.Range(0, 3).Select(candidate =>
                    string.Join(' ', Enumerable.Range(0, 4)
                        .Where(rule => HasEdge(mask, rule, candidate)).Select(rule => $"key{rule}"))).ToArray());
                var result = AffixEngine.CheckAffixes(parsed, rules);
                Equal(expected.Primary, result.PrimaryHits, $"关系{mask}主池应达到最大独立匹配");
                Equal(expected.Secondary, result.SecondaryHits, $"关系{mask}次池不得受规则顺序干扰");
                Equal(result.TotalHits, result.MatchedPrimary.Concat(result.MatchedSecondary).Count(),
                    "命中明细和数量必须一致");
                Require(result.TotalHits <= 3, "三个真实候选不能被重复计数");
            }
        }
    }

    private static (int Primary, int Secondary) ExhaustiveBest(int mask)
    {
        var best = (Primary: 0, Secondary: 0);
        Visit(0, 0, 0, 0);
        return best;

        void Visit(int rule, int used, int primary, int secondary)
        {
            if (rule == 4)
            {
                if (primary > best.Primary || primary == best.Primary && secondary > best.Secondary)
                    best = (primary, secondary);
                return;
            }
            Visit(rule + 1, used, primary, secondary);
            for (var candidate = 0; candidate < 3; candidate++)
            {
                if ((used & (1 << candidate)) != 0 || !HasEdge(mask, rule, candidate)) continue;
                Visit(rule + 1, used | (1 << candidate), primary + (rule < 2 ? 1 : 0), secondary + (rule >= 2 ? 1 : 0));
            }
        }
    }

    private static bool HasEdge(int mask, int rule, int candidate) => (mask & (1 << (rule * 3 + candidate))) != 0;

    private static CraftRules Rules(params string[] words)
    {
        var rules = new CraftRules { Mode = CraftMode.AltAug };
        foreach (var word in words) rules.PrimaryAffixes.Add(new AffixRule { Text = word });
        return rules;
    }

    private static ItemParseResult Explicit(params string[] descriptions)
    {
        var parsed = new ItemParseResult();
        foreach (var description in descriptions)
            parsed.MatchCandidates.Add(new ItemMatchCandidate { SearchText = description, DisplayText = description, IsExplicitAffix = true });
        return parsed;
    }

    private static IEnumerable<string[]> Permutations(string[] words)
    {
        foreach (var a in words)
        foreach (var b in words.Where(value => value != a))
            yield return new[] { a, b, words.Single(value => value != a && value != b) };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}；预期{expected}，实际{actual}");
    }
}
