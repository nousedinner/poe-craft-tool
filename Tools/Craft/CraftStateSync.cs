namespace ShiKe.Tools.Craft;

/// <summary>一次通货点击后应观察到的物品状态转换。</summary>
public enum CraftCurrencyOperation
{
    AnyChange,
    Alteration,
    Augmentation,
    Scouring,
    Transmutation,
    Alchemy,
    Regal,
    Exalted,
}

/// <summary>用于通货转换校验的最小物品状态。</summary>
public readonly record struct CraftItemState(ItemRarity Rarity, int ExplicitAffixCount);

/// <summary>通货转换校验结果；Reason 用于日志和安全停止提示。</summary>
public readonly record struct CraftTransitionCheck(bool Accepted, string Reason);

/// <summary>
/// Craft 物品状态同步策略。仅包含纯函数和时序常量，便于在不发送真实输入的情况下回归验证。
/// </summary>
public static class CraftStateSync
{
    /// <summary>单次 Ctrl+Alt+C 后等待剪贴板写入的最长时间。</summary>
    public const int CopyAttemptTimeoutMs = 120;

    /// <summary>旧状态或空剪贴板后再次复制前的退避时间。</summary>
    public const int RetryBackoffMs = 20;

    /// <summary>
    /// 一次通货点击后等待服务器状态变化的总上限。
    /// 正常路径在首次变化时立即返回，此上限只影响异常/卡顿路径。
    /// </summary>
    public static int CalculateChangeTimeoutMs(int configuredDelayMs)
        => Math.Max(900, Math.Max(0, configuredDelayMs) + 500);

    /// <summary>
    /// 比较两次完整物品文本是否代表不同状态。
    /// 仅忽略剪贴板可能产生的换行和首尾空白差异，不忽略任何物品字段。
    /// </summary>
    public static bool HasItemStateChanged(string baseline, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        return !string.Equals(Normalize(baseline), Normalize(candidate), StringComparison.Ordinal);
    }

    /// <summary>
    /// 根据通货种类校验服务器返回的状态转换。
    /// 先要求完整文本发生变化，再检查稀有度和真实显式词缀数。
    /// </summary>
    public static CraftTransitionCheck CheckTransition(CraftCurrencyOperation operation,
        CraftItemState before, CraftItemState after, bool textChanged)
    {
        if (!textChanged)
            return new(false, "完整物品文本仍与点击前相同");

        var accepted = operation switch
        {
            CraftCurrencyOperation.AnyChange => true,
            CraftCurrencyOperation.Alteration =>
                before.Rarity == ItemRarity.Magic && after.Rarity == ItemRarity.Magic,
            CraftCurrencyOperation.Augmentation =>
                before.Rarity == ItemRarity.Magic && before.ExplicitAffixCount == 1 &&
                after.Rarity == ItemRarity.Magic && after.ExplicitAffixCount == 2,
            CraftCurrencyOperation.Scouring =>
                before.Rarity is ItemRarity.Magic or ItemRarity.Rare &&
                after.Rarity == ItemRarity.Normal && after.ExplicitAffixCount == 0,
            CraftCurrencyOperation.Transmutation =>
                before.Rarity == ItemRarity.Normal &&
                after.Rarity == ItemRarity.Magic && after.ExplicitAffixCount is 1 or 2,
            CraftCurrencyOperation.Alchemy =>
                before.Rarity == ItemRarity.Normal &&
                after.Rarity == ItemRarity.Rare && after.ExplicitAffixCount > 0,
            CraftCurrencyOperation.Regal =>
                before.Rarity == ItemRarity.Magic && after.Rarity == ItemRarity.Rare &&
                after.ExplicitAffixCount == before.ExplicitAffixCount + 1,
            CraftCurrencyOperation.Exalted =>
                before.Rarity == ItemRarity.Rare && after.Rarity == ItemRarity.Rare &&
                after.ExplicitAffixCount == before.ExplicitAffixCount + 1,
            _ => false,
        };

        if (accepted) return new(true, "");
        return new(false,
            $"状态转换不符合 {operation}：" +
            $"{before.Rarity}/{before.ExplicitAffixCount} → {after.Rarity}/{after.ExplicitAffixCount}");
    }

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal)
               .Replace('\r', '\n')
               .Trim();
}
