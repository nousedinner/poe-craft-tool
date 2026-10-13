using ShiKe.Tools.Craft;

internal static class CraftLoopChecks
{
    public static void Mode2DecisionMatrixKeepsContracts()
    {
        var rules = Rules();
        foreach (var count in new[] { 1, 2 })
        foreach (var primary in new[] { 0, 1 })
        foreach (var secondary in new[] { 0, 1 })
        foreach (var exclude in new[] { false, true })
        {
            var result = new AffixCheckResult { PrimaryHits = primary, SecondaryHits = secondary, HasExclude = exclude };
            var expected = exclude || primary + secondary == 0
                ? Mode2AlterationDecision.ContinueAlteration
                : count == 1 ? Mode2AlterationDecision.UseAugmentation
                : primary == 1 && secondary == 1 ? Mode2AlterationDecision.Complete
                : Mode2AlterationDecision.ContinueAlteration;
            Equal(expected, CraftDecisions.AfterMode2Alteration(count, result, rules), "Mode2阶段判定错误");
        }
        var oneRequired = Rules();
        oneRequired.SecondaryHitCount = 0;
        Equal(Mode2AlterationDecision.UseAugmentation,
            CraftDecisions.AfterMode2Alteration(1, new AffixCheckResult { PrimaryHits = 1 }, oneRequired),
            "Mode2一条命中仍按既有流程增幅，不新增提前成功规则");
        var zeroRequired = new CraftRules { Mode = CraftMode.AltAug, PrimaryHitCount = 0, SecondaryHitCount = 0 };
        Equal(Mode2AlterationDecision.ContinueAlteration,
            CraftDecisions.AfterMode2Alteration(2, new AffixCheckResult(), zeroRequired),
            "本次不改变无命中时继续改造的产品规则");
    }

    public static void Mode2PartialAndExcludedResultsStayInSamePhase()
    {
        var rules = Rules();
        rules.ExcludeAffixes.Add(new AffixRule { Text = "排除目标" });
        var queue = new Queue<Mode2AlterationStep>(new[]
        {
            Step(rules, "目标甲", "其他词缀"),
            Step(rules, "目标甲", "排除目标"),
            Step(rules, "其他甲", "其他乙"),
            Step(rules, "目标甲", "目标乙"),
        });
        var reads = 0;
        var continued = new List<Mode2AlterationStep>();
        var outcome = Mode2AlterationLoop.RunAsync(rules, _ =>
        {
            reads++;
            return Task.FromResult(queue.Dequeue());
        }, continued.Add, CancellationToken.None).GetAwaiter().GetResult();
        Equal(4, reads, "部分命中、排除和无命中应继续同一改造阶段");
        Equal(3, continued.Count, "每个未达标结果都应进入继续分支");
        Equal(Mode2AlterationDecision.Complete, outcome.Decision, "两个独立目标才完成");
        Require(queue.Count == 0, "命中后不应多取下一轮结果");
    }

    public static void Mode2OneModifierMovesToAugmentation()
    {
        var rules = Rules();
        var first = Step(rules, "目标甲");
        var calls = 0;
        var outcome = Mode2AlterationLoop.RunAsync(rules, _ =>
        {
            calls++;
            return Task.FromResult(first);
        }, _ => throw new InvalidOperationException("一条命中不得继续改造"), CancellationToken.None)
            .GetAwaiter().GetResult();
        Equal(1, calls, "一条命中后只离开本阶段一次");
        Equal(Mode2AlterationDecision.UseAugmentation, outcome.Decision, "应去增幅");
        Require(ReferenceEquals(first.Item, outcome.Step.Item), "增幅应使用最后已确认的物品快照");
    }

    public static void Mode2UnrecognizedCountStopsWithoutNextRoll()
    {
        var rules = Rules();
        var item = new CraftItemSnapshot("物品类别: 头盔\n稀有度: 魔法\n测试物品\n铁帽\n--------\n目标甲\n目标乙");
        var reads = 0;
        Throws<InvalidOperationException>(() => Mode2AlterationLoop.RunAsync(rules, _ =>
        {
            reads++;
            return Task.FromResult(new Mode2AlterationStep(item, AffixEngine.CheckAffixes(item.Parsed, rules)));
        }, _ => throw new InvalidOperationException("未知数量不能继续"), CancellationToken.None)
            .GetAwaiter().GetResult());
        Equal(1, reads, "不能把没有高级词缀头的文本当作一词缀去增幅或追加改造");
    }

    public static void Mode2CancelledBeforeReadDoesNotRoll()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;
        Throws<OperationCanceledException>(() => Mode2AlterationLoop.RunAsync(Rules(), _ =>
        {
            calls++;
            return Task.FromResult(Step(Rules(), "目标甲", "目标乙"));
        }, _ => { }, cts.Token).GetAwaiter().GetResult());
        Equal(0, calls, "停止后不能发送下一次改造");
    }

    public static void Mode2CancelledAfterReadDoesNotReportSuccess()
    {
        using var cts = new CancellationTokenSource();
        var rules = Rules();
        var calls = 0;
        Throws<OperationCanceledException>(() => Mode2AlterationLoop.RunAsync(rules, _ =>
        {
            calls++;
            cts.Cancel();
            return Task.FromResult(Step(rules, "目标甲", "目标乙"));
        }, _ => throw new InvalidOperationException("取消后不能继续"), cts.Token).GetAwaiter().GetResult());
        Equal(1, calls, "取消后不应追加动作或返回完成");
    }

    public static void Mode2CopyFailureDoesNotRetryCurrency()
    {
        var failure = new IOException("模拟复制失败");
        var calls = 0;
        try
        {
            Mode2AlterationLoop.RunAsync(Rules(), _ =>
            {
                calls++;
                return Task.FromException<Mode2AlterationStep>(failure);
            }, _ => throw new InvalidOperationException("复制失败不能继续"), CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("复制失败没有传播");
        }
        catch (IOException actual) { Require(ReferenceEquals(failure, actual), "原异常应保留"); }
        Equal(1, calls, "复制异常不得触发额外通货操作");
    }

    public static void SnapshotKeepsOriginalTextAndReusesParsedObject()
    {
        var original = "\r\n " + Text("目标甲", "目标乙").Replace("\n", "\r\n") + " \r\n";
        var snapshot = new CraftItemSnapshot(original);
        Equal(original, snapshot.Text, "快照必须保留原文，不将同步规范化应用于关键词");
        Equal(new CraftItemState(ItemRarity.Magic, 2), snapshot.State, "真实状态应来自同一解析");
        Require(ReferenceEquals(snapshot.Parsed, snapshot.Parsed), "后续检查不能重新解析同一快照");
        var result = AffixEngine.CheckAffixes(snapshot.Parsed, Rules());
        Equal(2, result.TotalHits, "快照应能直接供匹配使用");
    }

    public static void SnapshotChangeComparisonPreservesCompleteFields()
    {
        var text = Text("目标甲", "目标乙");
        var snapshot = new CraftItemSnapshot(text);
        foreach (var candidate in new[] { text, "\r\n " + text + " \r\n", text.Replace("\n", "\r"), "", " ", text.Replace("80", "81"), text.Replace("目标甲", "目标丙") })
            Equal(CraftStateSync.HasItemStateChanged(text, candidate), snapshot.HasChangedTo(candidate),
                "快照只忽略既有换行/首尾空白差异，不能忽略任何物品字段变化");
    }

    public static void SnapshotsAreIndependentAcrossCopies()
    {
        var first = new CraftItemSnapshot(Text("目标甲"));
        var next = new CraftItemSnapshot(Text("目标甲", "目标乙"));
        var anotherRun = new CraftItemSnapshot(first.Text);
        Require(!ReferenceEquals(first.Parsed, anotherRun.Parsed), "不能跨运行共享可变解析集合");
        Equal(1, first.State.ExplicitAffixCount, "新复制不能污染原快照状态");
        Equal(2, next.State.ExplicitAffixCount, "新复制必须取得新状态");
        Require(CraftStateSync.CheckTransition(CraftCurrencyOperation.Augmentation,
            first.State, next.State, first.HasChangedTo(next.Text)).Accepted, "快照仍须通过真实增幅转换核对");
        Require(!CraftStateSync.CheckTransition(CraftCurrencyOperation.Regal,
            first.State, next.State, first.HasChangedTo(next.Text)).Accepted, "数量增加不等于富豪成功");
    }

    private static CraftRules Rules()
    {
        var rules = new CraftRules { Mode = CraftMode.AltAug, PrimaryHitCount = 1, SecondaryHitCount = 1 };
        rules.PrimaryAffixes.Add(new AffixRule { Text = "目标甲" });
        rules.SecondaryAffixes.Add(new AffixRule { Text = "目标乙" });
        return rules;
    }

    private static Mode2AlterationStep Step(CraftRules rules, params string[] descriptions)
    {
        var item = new CraftItemSnapshot(Text(descriptions));
        return new(item, AffixEngine.CheckAffixes(item.Parsed, rules));
    }

    private static string Text(params string[] descriptions)
        => "物品类别: 头盔\n稀有度: 魔法\n测试物品\n铁帽\n--------\n物品等级: 80\n--------\n" +
           string.Join("\n", descriptions.Select((description, index) =>
               $"{{ {(index == 0 ? "前缀" : "后缀")}属性 \"测试{index}\" (等阶: 1) }}\n{description}"));

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"预期{typeof(T).Name}异常");
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
