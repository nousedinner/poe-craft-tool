namespace ShiKe.Tools.Craft;

/// <summary>
/// 工作任务内一次已复制物品的快照。原文、规范换行文本和解析结果一起传递，
/// 不跨运行缓存；解析集合由该任务只读使用，不向UI发布。
/// </summary>
internal sealed class CraftItemSnapshot
{
    public string Text { get; }
    public ItemParseResult Parsed { get; }
    public CraftItemState State => new(Parsed.Rarity, Parsed.ExplicitAffixCount);
    private string NormalizedText { get; }

    public CraftItemSnapshot(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        NormalizedText = CraftStateSync.NormalizeItemText(text);
        Parsed = AffixEngine.ParseItem(text);
    }

    public bool HasChangedTo(string candidate)
        => !string.IsNullOrWhiteSpace(candidate) &&
           !string.Equals(NormalizedText, CraftStateSync.NormalizeItemText(candidate), StringComparison.Ordinal);
}
