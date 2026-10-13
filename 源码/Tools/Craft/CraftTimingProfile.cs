namespace ShiKe.Tools.Craft;

/// <summary>仅本实验分支使用；不写入用户设置，每次启动程序恢复基线。</summary>
public enum CraftSpeedExperiment
{
    Baseline,
    FirstWait10,
    ClipboardPoll5,
    Movement10,
    CopyPadding10,
    Combined,
}

internal sealed record CraftTimingProfile(
    CraftSpeedExperiment Experiment, string Name, int? FirstWaitOverrideMs,
    int ClipboardPollMs, int ItemMovementMs, int CopyBeforeMs, int CopyAfterMs)
{
    public static CraftTimingProfile For(CraftSpeedExperiment experiment) => experiment switch
    {
        CraftSpeedExperiment.Baseline => new(experiment, "原等待", null, 20, 30, 20, 20),
        CraftSpeedExperiment.FirstWait10 => new(experiment, "仅首次等待10ms", 10, 20, 30, 20, 20),
        CraftSpeedExperiment.ClipboardPoll5 => new(experiment, "仅剪贴板读取5ms", null, 5, 30, 20, 20),
        CraftSpeedExperiment.Movement10 => new(experiment, "仅装备移动等待10ms", null, 20, 10, 20, 20),
        CraftSpeedExperiment.CopyPadding10 => new(experiment, "仅复制前后各10ms", null, 20, 30, 10, 10),
        CraftSpeedExperiment.Combined => new(experiment, "组合提速", 10, 5, 10, 10, 10),
        _ => throw new ArgumentOutOfRangeException(nameof(experiment)),
    };

    public int FirstWaitMs(int configuredDelayMs) => FirstWaitOverrideMs ?? configuredDelayMs;

    public string Describe(int configuredDelayMs) =>
        $"首次等待{FirstWaitMs(configuredDelayMs)}ms；装备移动{ItemMovementMs}ms；" +
        $"复制前后{CopyBeforeMs}+{CopyAfterMs}ms；读取间隔{ClipboardPollMs}ms。";
}
