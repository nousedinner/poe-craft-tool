using ShiKe.Tools.Craft;

/// <summary>
/// 庄园工艺循环骨架：命中即停、连续同结果达阈值即停、复制失败立即停止、取消立即收尾、不同结果重置计数。
/// 全部用脚本化委托驱动，不点鼠标、不读剪贴板、不连游戏。
/// </summary>
internal static class HarvestLoopChecks
{
    private const string HelmetA = "物品类别: 头盔\n稀 有 度: 稀有\n末日之冠\n铁帽";
    private const string HelmetB = "物品类别: 头盔\n稀 有 度: 稀有\n黎明之冠\n铁帽";

    public static void MatchedItemStopsLoop()
    {
        var rules = Rules();
        var script = new Scripted()
            .Add(HelmetA, Miss)
            .Add(HelmetB, Miss)
            .Add(HelmetA, () => Hit(rules));
        var steps = new List<HarvestLoop.Step>();

        var result = Run(rules, identicalThreshold: 10, script, steps);

        Equal(3, script.CopyCalls, "命中后不得再转换一次");
        Equal(3, steps.Count, "每轮判定都必须上报一次");
        Equal(3, result.UseCount, "返回的轮次必须与实际转换次数一致");
        True(result.Result.MeetsFinalRules(rules), "返回的那一步必须是命中步");
        Equal(1, steps.Count(step => step.Result.MeetsFinalRules(rules)), "只允许最后一次命中");
    }

    public static void IdenticalResultsStopAtThreshold()
    {
        var rules = Rules();
        var script = new Scripted()
            .Add(HelmetA, Miss).Add(HelmetA, Miss).Add(HelmetA, Miss)
            .Add(HelmetA, Miss).Add(HelmetA, Miss);
        var steps = new List<HarvestLoop.Step>();

        var failure = Catch(() => Run(rules, identicalThreshold: 2, script, steps));

        True(failure is InvalidOperationException, "连续同结果必须按阈值抛错停止");
        True(failure!.Message.Contains("连续 3 次", StringComparison.Ordinal),
            $"停止提示必须说明实际连续次数，实际：{failure.Message}");
        True(failure.Message.Contains("完全相同", StringComparison.Ordinal), "停止提示必须说明原因");
        Equal(3, script.CopyCalls, "达到阈值后不得再多点一次工艺按钮");
        Equal(3, steps.Count, "每一轮都要先上报再判定阈值");
    }

    public static void CopyFailureStopsImmediately()
    {
        var rules = Rules();
        var script = new Scripted()
            .Add(HelmetA, Miss)
            .Add(() => throw new InvalidOperationException("模拟复制工艺区域失败"), Miss);
        var steps = new List<HarvestLoop.Step>();

        var failure = Catch(() => Run(rules, identicalThreshold: 10, script, steps));

        True(failure is InvalidOperationException && failure.Message.Contains("模拟复制工艺区域失败", StringComparison.Ordinal),
            "复制失败必须原样抛出，由上层安全停止");
        Equal(2, script.CopyCalls, "复制失败后不得继续转换");
        Equal(1, steps.Count, "失败的那一轮不产生判定结果");
    }

    public static void CancellationStopsBeforeNextConversion()
    {
        var rules = Rules();
        var script = new Scripted()
            .Add(HelmetA, Miss)
            .Add(HelmetB, Miss);
        var steps = new List<HarvestLoop.Step>();
        using var cancel = new CancellationTokenSource();

        var failure = Catch(() => Run(rules, identicalThreshold: 10, script, steps, cancel.Token,
            afterStep: () => cancel.Cancel()));

        True(failure is OperationCanceledException, "取消必须立刻结束循环");
        Equal(1, script.CopyCalls, "取消后不得再转换一次");
        Equal(1, steps.Count, "取消发生在两轮之间，已完成的轮次仍要上报");
    }

    public static void DifferentResultsResetIdenticalCounter()
    {
        var rules = Rules();
        var script = new Scripted()
            .Add(HelmetA, Miss)
            .Add(HelmetB, Miss)
            .Add(HelmetA, Miss)
            .Add(HelmetB, Miss)
            .Add(HelmetA, () => Hit(rules));
        var steps = new List<HarvestLoop.Step>();

        var result = Run(rules, identicalThreshold: 2, script, steps);

        Equal(5, result.UseCount, "交替结果不得被误判为连续相同");
        Equal(5, script.CopyCalls, "交替结果一轮都不该被阈值提前打断");
        Equal(5, steps.Count, "命中步也要上报");
        True(steps.All(step => step.IdenticalResults == 0), "交替物品的任何一轮都不该累计连续次数");
    }

    /// <summary>通货只有名称一个候选：主命中要求超过 1 时，第一次转换就停，不允许把堆叠烧光。</summary>
    public static void CurrencyWithImpossibleHitCountStopsAfterFirstConversion()
    {
        var rules = new CraftRules { Mode = CraftMode.Harvest, PrimaryHitCount = 2 };
        rules.PrimaryAffixes.Add(new AffixRule { Text = "贪婪" });
        var script = new Scripted().Add(HarvestChecks.Essence, Miss);
        var steps = new List<HarvestLoop.Step>();

        var failure = Catch(() => Run(rules, identicalThreshold: 10, script, steps));

        True(failure is InvalidOperationException, "通货无法满足主命中 2 时必须立刻停止");
        True(failure!.Message.Contains("通货", StringComparison.Ordinal), "停止提示必须点明是通货");
        Equal(1, script.CopyCalls, "只允许消耗 1 次转换就停");
        Equal(1, steps.Count, "那一次转换仍要上报");
    }

    // ── 夹具 ──

    private static CraftRules Rules()
    {
        var rules = new CraftRules { Mode = CraftMode.Harvest, PrimaryHitCount = 1 };
        rules.PrimaryAffixes.Add(new AffixRule { Text = "最大生命" });
        return rules;
    }

    private static AffixCheckResult Hit(CraftRules rules) => new() { PrimaryHits = rules.RequiredPrimaryHits };

    private static AffixCheckResult Miss() => new();

    /// <summary>“这一轮转换后复制到什么”与“这一轮判定结果”成对排队；多转一次或多判一次都会立刻失败。</summary>
    private sealed class Scripted
    {
        private readonly Queue<Func<CraftItemSnapshot>> _copies = new();
        private readonly Queue<Func<AffixCheckResult>> _results = new();

        public int CopyCalls { get; private set; }

        public Scripted Add(string text, Func<AffixCheckResult> result)
            => Add(() => new CraftItemSnapshot(text), result);

        public Scripted Add(Func<CraftItemSnapshot> copy, Func<AffixCheckResult> result)
        {
            _copies.Enqueue(copy);
            _results.Enqueue(result);
            return this;
        }

        public Task<CraftItemSnapshot> CopyAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_copies.Count == 0) throw new InvalidOperationException("脚本已用尽：循环比预期多转了一次");
            CopyCalls++;
            return Task.FromResult(_copies.Dequeue()());
        }

        public AffixCheckResult Evaluate(CraftItemSnapshot item)
        {
            if (_results.Count == 0) throw new InvalidOperationException("判定脚本已用尽：循环比预期多判定了一次");
            return _results.Dequeue()();
        }
    }

    private static HarvestLoop.Step Run(CraftRules rules, int identicalThreshold, Scripted script,
        List<HarvestLoop.Step> steps, CancellationToken token = default, Action? afterStep = null)
        => HarvestLoop.RunAsync(rules, identicalThreshold, script.CopyAsync, script.Evaluate,
            step =>
            {
                steps.Add(step);
                afterStep?.Invoke();
            }, token).GetAwaiter().GetResult();

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
