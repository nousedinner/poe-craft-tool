namespace ShiKe.Tools.Craft;

internal sealed record Mode2AlterationStep(CraftItemSnapshot Item, AffixCheckResult Result);
internal sealed record Mode2AlterationOutcome(Mode2AlterationDecision Decision, Mode2AlterationStep Step);

/// <summary>
/// Mode2已选中改造石后的循环。一次roll只返回已通过同步门禁的物品和匹配结果，
/// 失败留在本循环；调用方只在进入/离开阶段时选通货和管理Shift。
/// </summary>
internal static class Mode2AlterationLoop
{
    public static async Task<Mode2AlterationOutcome> RunAsync(
        CraftRules rules,
        Func<CancellationToken, Task<Mode2AlterationStep>> roll,
        Action<Mode2AlterationStep> continuing,
        CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var step = await roll(token);
            token.ThrowIfCancellationRequested();
            var decision = CraftDecisions.AfterMode2Alteration(step.Item.Parsed.ExplicitAffixCount, step.Result, rules);
            if (decision == Mode2AlterationDecision.StopUnsupported)
                throw new InvalidOperationException(
                    $"改造后未能确认1～2条真实词缀（识别到{step.Item.Parsed.ExplicitAffixCount}条）；" +
                    "已停止，请检查Ctrl+Alt+C高级物品文本，未继续使用增幅石");
            if (decision != Mode2AlterationDecision.ContinueAlteration)
                return new(decision, step);
            continuing(step);
        }
    }
}
