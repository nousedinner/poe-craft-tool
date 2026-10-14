namespace ShiKe.Services;

/// <summary>一次快捷配对通货点击。Alt 仅包住这一发左键，复制和后续点击不得借用该按住状态。</summary>
internal static class CraftAltClick
{
    public const int ModifierSettleMs = 20;

    internal static async Task RunAsync(Action pressAlt, Action releaseAlt,
        Func<CancellationToken, Task> click, Func<int, CancellationToken, Task> delay,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            pressAlt();
            await delay(ModifierSettleMs, token);
            token.ThrowIfCancellationRequested();
            await click(token);
        }
        finally
        {
            // 按下、等待、鼠标输入中的任何失败均释放；不得由取消令牌跳过。
            releaseAlt();
        }
    }
}
