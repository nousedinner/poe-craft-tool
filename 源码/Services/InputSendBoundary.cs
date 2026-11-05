namespace ShiKe.Services;

/// <summary>所有准备等待结束后，再确认本次按下仍可发送；失焦不等待恢复后补发。</summary>
internal static class InputSendBoundary
{
    internal static async Task WaitThenSendAsync(Task preparation, Func<bool> isTargetForeground,
        Action checkEmergencyStop, Action send, CancellationToken token)
    {
        await preparation.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!isTargetForeground())
            throw new InputSimulationException("输入前游戏失去前台，已停止本次输入");
        checkEmergencyStop();
        token.ThrowIfCancellationRequested();
        send();
    }
}
