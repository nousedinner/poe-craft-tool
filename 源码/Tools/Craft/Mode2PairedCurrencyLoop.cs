namespace ShiKe.Tools.Craft;

/// <summary>
/// Mode2 的持续 Shift 会话：只在开始时选定常驻通货，配对操作由 Alt 临时切换。
/// 每个使用回调必须先确认本次物品转换；复制失败直接停止，不额外补用通货。
/// </summary>
internal static class Mode2PairedCurrencyLoop
{
    public static Task<Mode2AlterationStep> RunAlterationAugmentationAsync(CraftRules rules,
        Func<CancellationToken, Task> beginSession, Func<CancellationToken, Task> releaseShift,
        Func<CancellationToken, Task<Mode2AlterationStep>> alteration,
        Func<CancellationToken, Task<Mode2AlterationStep>> augmentation,
        Action<Mode2AlterationStep> continueAlteration, Action continueAfterAugmentation,
        CancellationToken token)
        => RunSessionAsync(beginSession, releaseShift, async workToken =>
        {
            while (true)
            {
                var outcome = await Mode2AlterationLoop.RunAsync(rules, alteration, continueAlteration, workToken);
                if (outcome.Decision == Mode2AlterationDecision.Complete) return outcome.Step;

                workToken.ThrowIfCancellationRequested();
                var augmented = await augmentation(workToken);
                workToken.ThrowIfCancellationRequested();
                if (augmented.Result.MeetsFinalRules(rules)) return augmented;
                continueAfterAugmentation();
            }
        }, token);

    public static Task<Mode2AlterationStep> RunScouringAlchemyAsync(CraftRules rules, ItemRarity initialRarity,
        Func<CancellationToken, Task> beginSession, Func<CancellationToken, Task> releaseShift,
        Func<CancellationToken, Task> scouring,
        Func<CancellationToken, Task<Mode2AlterationStep>> alchemy,
        Action continueAfterAlchemy, CancellationToken token)
    {
        if (initialRarity is not (ItemRarity.Normal or ItemRarity.Magic or ItemRarity.Rare))
            throw new InvalidOperationException("重铸+点金无法处理当前物品稀有度，未执行通货操作");

        return RunSessionAsync(beginSession, releaseShift, async workToken =>
        {
            var needsScouring = initialRarity != ItemRarity.Normal;
            while (true)
            {
                workToken.ThrowIfCancellationRequested();
                if (needsScouring)
                {
                    await scouring(workToken);
                    workToken.ThrowIfCancellationRequested();
                }

                var rolled = await alchemy(workToken);
                workToken.ThrowIfCancellationRequested();
                if (rolled.Result.MeetsFinalRules(rules)) return rolled;
                needsScouring = true;
                continueAfterAlchemy();
            }
        }, token);
    }

    private static async Task<Mode2AlterationStep> RunSessionAsync(
        Func<CancellationToken, Task> beginSession, Func<CancellationToken, Task> releaseShift,
        Func<CancellationToken, Task<Mode2AlterationStep>> body, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await beginSession(token);
            token.ThrowIfCancellationRequested();
            return await body(token);
        }
        finally
        {
            await releaseShift(CancellationToken.None);
        }
    }
}
