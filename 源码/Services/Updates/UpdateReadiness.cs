namespace ShiKe.Services.Updates;

internal static class UpdateReadiness
{
    internal static void EnsureOrdinaryStartupAllowed(string executablePath)
    {
        if (UpdateBootstrap.IsUpdateBusy(executablePath))
            throw new IOException("拾刻正在更新，请等待更新完成后再启动。");
        UpdateBootstrap.EnsureNoIncompleteTransaction(executablePath);
    }

    internal static bool CanApproveExit(bool prepared, bool settingsLoaded, bool workersStopped,
        IReadOnlyCollection<string> errors)
        => prepared && settingsLoaded && workersStopped && errors.Count == 0;
}
