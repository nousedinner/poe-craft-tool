namespace ShiKe.Tools.Craft;

/// <summary>正式洗装时序。首次等待跟随原延迟设置，其余固定采用已实测的稳健组合。</summary>
internal sealed record CraftTimingProfile(int ClipboardPollMs, int ItemMovementMs, int CopyBeforeMs, int CopyAfterMs)
{
    public static CraftTimingProfile Default { get; } = new(5, 10, 20, 10);

    public int FirstWaitMs(int configuredDelayMs) => configuredDelayMs;
}
