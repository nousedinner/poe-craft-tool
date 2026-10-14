namespace ShiKe.Services;

/// <summary>PoE 3.29起Ctrl+C直接复制高级物品文本；此动作不按放Shift或Alt。</summary>
internal static class CraftCopyShortcut
{
    internal static void Send(Action<ushort> press, Action<ushort> release, Action<ushort> bestEffortRelease)
    {
        try
        {
            press(KeyCode.Control);
            press(KeyCode.C);
            release(KeyCode.C);
            release(KeyCode.Control);
        }
        catch
        {
            bestEffortRelease(KeyCode.C);
            bestEffortRelease(KeyCode.Control);
            throw;
        }
    }
}
