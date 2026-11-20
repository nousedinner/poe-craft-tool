namespace ShiKe.Services;

/// <summary>保持调用方的 Shift 状态，发送选定的物品复制组合键。</summary>
internal static class CraftCopyShortcut
{
    internal static void Send(Action<ushort> press, Action<ushort> release, Action<ushort> bestEffortRelease,
        bool withAlt = false)
    {
        var releaseAlt = false;
        try
        {
            press(KeyCode.Control);
            if (withAlt)
            {
                releaseAlt = true;
                press(KeyCode.Alt);
            }
            press(KeyCode.C);
            release(KeyCode.C);
            if (releaseAlt)
            {
                release(KeyCode.Alt);
                releaseAlt = false;
            }
            release(KeyCode.Control);
        }
        catch
        {
            bestEffortRelease(KeyCode.C);
            if (releaseAlt) bestEffortRelease(KeyCode.Alt);
            bestEffortRelease(KeyCode.Control);
            throw;
        }
    }
}

/// <summary>保持 Shift 时优先使用本轮已验证的复制组合，每次读取最多试一次备用组合。</summary>
internal sealed class CraftCopyPreference
{
    private enum NextAttempt { Preferred, Alternate, PreferredOnly }

    private bool _preferAlt;
    private NextAttempt _next;

    internal bool IsTryingAlternate(bool shiftHeld) => shiftHeld && _next == NextAttempt.Alternate;
    internal bool UseAlt(bool shiftHeld) => shiftHeld && (_next == NextAttempt.Alternate ? !_preferAlt : _preferAlt);

    internal void BeginItemRead() => _next = NextAttempt.Preferred;

    // 返回是否因一次成功的备用复制而更换了本轮首选。
    internal bool RecordAttempt(bool receivedFreshItem, bool shiftHeld)
    {
        if (!shiftHeld) return false;
        if (_next == NextAttempt.Alternate)
        {
            _next = NextAttempt.PreferredOnly;
            if (!receivedFreshItem) return false;
            _preferAlt = !_preferAlt;
            return true;
        }
        if (!receivedFreshItem && _next == NextAttempt.Preferred)
            _next = NextAttempt.Alternate;
        return false;
    }
}
