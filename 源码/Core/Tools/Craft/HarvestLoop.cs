namespace ShiKe.Tools.Craft;

/// <summary>
/// 庄园工艺的循环骨架：每轮「转换一次 → 复制 → 判定」，命中即停，连续同一结果达阈值即停。
/// 点击工艺按钮、等待、复制工艺区域这些 I/O 由调用方以委托注入，
/// 因此这段判定可以被确定性驱动，不必真的连游戏或读剪贴板。
/// 与 Mode2 的循环拆分保持同一形状：Core 负责判定，桌面层负责输入。
/// </summary>
internal static class HarvestLoop
{
    /// <summary>一轮结果。UseCount 从 1 开始；IdenticalResults 是「与上一轮完全相同的次数」，首轮为 0。</summary>
    public sealed record Step(int UseCount, CraftItemSnapshot Item, AffixCheckResult Result, int IdenticalResults);

    /// <param name="convertAndCopy">点一次工艺按钮并复制工艺区域；复制失败应直接抛异常，由调用方终止任务。</param>
    /// <param name="evaluate">按当前规则判定该物品；由调用方决定是否顺带记录日志与测量。</param>
    /// <param name="onStep">每轮判定后的上报口，用来更新计数、状态栏和日志。</param>
    public static async Task<Step> RunAsync(CraftRules rules, int identicalThreshold,
        Func<CancellationToken, Task<CraftItemSnapshot>> convertAndCopy,
        Func<CraftItemSnapshot, AffixCheckResult> evaluate,
        Action<Step> onStep,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(convertAndCopy);
        ArgumentNullException.ThrowIfNull(evaluate);
        ArgumentNullException.ThrowIfNull(onStep);

        string? previousText = null;
        var identicalResults = 0;
        var useCount = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            var item = await convertAndCopy(token);
            useCount++;

            // 随机转换本来就可能转出同一个道具，因此不用「等待文本变化」的闸门；
            // 但连续多次完全相同说明按钮没点中，按既有未变化阈值停止。
            var normalized = CraftStateSync.NormalizeItemText(item.Text);
            identicalResults = string.Equals(normalized, previousText, StringComparison.Ordinal)
                ? identicalResults + 1 : 0;
            previousText = normalized;

            var step = new Step(useCount, item, evaluate(item), identicalResults);
            onStep(step);

            // 通货只有名称一个候选：最多 1 次主命中、0 次次命中。要求更高时永远不可能满足，
            // 而随机转换结果各不相同，「连续相同」阈值兜不住，只会把堆叠烧光后报「坐标不准」——这里第一次就停。
            if (item.Parsed is { IsCurrencyItem: true } &&
                (rules.RequiredPrimaryHits > 1 || rules.RequiredSecondaryHits > 0))
                throw new InvalidOperationException(
                    $"工艺区域是通货「{(item.Parsed.ItemName.Length > 0 ? item.Parsed.ItemName : "未识别名称")}」，" +
                    $"它只有名称一个候选（最多主命中 1、次命中 0），无法满足 主命中 {rules.RequiredPrimaryHits}/次命中 {rules.RequiredSecondaryHits}；" +
                    "已安全停止，未继续消耗道具");

            if (step.Result.MeetsFinalRules(rules)) return step;

            if (identicalResults >= Math.Max(1, identicalThreshold))
                throw new InvalidOperationException(
                    $"连续 {identicalResults + 1} 次工艺转换得到的物品完全相同；" +
                    "可能工艺按钮坐标不准或工艺台未打开，已安全停止");
        }
    }
}
