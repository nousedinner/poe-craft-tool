namespace ShiKe.Services;

/// <summary>一次聊天命令输入；失去目标前台后终止整次操作，不等待返回后续写半条命令。</summary>
internal static class ForegroundCommandInput
{
    internal static async Task SendAsync(string command, Func<bool> isTargetForeground,
        Action checkEmergencyStop, Action pressEnter, Action releaseEnter, Action<char, bool> sendCharacter,
        Func<int, CancellationToken, Task> delay, CancellationToken token)
    {
        async Task EnterAsync()
        {
            CheckForeground(isTargetForeground, checkEmergencyStop, token);
            try
            {
                pressEnter();
                await delay(10, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                // 输入开始后，取消或失焦都不能跳过按键释放。
                releaseEnter();
            }
            CheckForeground(isTargetForeground, checkEmergencyStop, token);
        }

        await EnterAsync().ConfigureAwait(false);
        await delay(100, token).ConfigureAwait(false);
        await TypeTextAsync(command, isTargetForeground, checkEmergencyStop, sendCharacter, delay, token)
            .ConfigureAwait(false);
        await EnterAsync().ConfigureAwait(false);
    }

    internal static async Task TypeTextAsync(string text, Func<bool> isTargetForeground,
        Action checkEmergencyStop, Action<char, bool> sendCharacter,
        Func<int, CancellationToken, Task> delay, CancellationToken token)
    {
        CheckForeground(isTargetForeground, checkEmergencyStop, token);
        foreach (var ch in text)
        {
            CheckForeground(isTargetForeground, checkEmergencyStop, token);
            try { sendCharacter(ch, true); }
            finally { sendCharacter(ch, false); }
            await delay(5, token).ConfigureAwait(false);
        }
        CheckForeground(isTargetForeground, checkEmergencyStop, token);
    }

    private static void CheckForeground(Func<bool> isTargetForeground, Action checkEmergencyStop,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!isTargetForeground())
            throw new OperationCanceledException("目标游戏失去前台，已取消本次命令输入");
        checkEmergencyStop();
        token.ThrowIfCancellationRequested();
    }
}
