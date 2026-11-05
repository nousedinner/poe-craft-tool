using System.Diagnostics;

namespace ShiKe.Services.Updates;

internal sealed record UpdateRunResult(bool Succeeded, bool RolledBack, bool NeedsRecovery, string Message);

internal sealed record UpdateRunnerTimeouts(TimeSpan ParentExit, TimeSpan Startup, TimeSpan StopChild)
{
    internal static UpdateRunnerTimeouts Default { get; } = new(TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10));
}

internal interface IUpdateProcess : IDisposable
{
    int Id { get; }
    long StartTimeUtcTicks { get; }
    string ExecutablePath { get; }
    bool HasExited { get; }
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token);
    Task<bool> StopOwnedAsync(TimeSpan timeout);
}

internal interface IUpdateOperations
{
    IUpdateProcess? OpenParent(int processId);
    IUpdateProcess StartUpdated(UpdateSession session);
    void Replace(string source, string target, string backup);
    void Add(string source, string target);
    void RemoveAdded(string target);
    bool IsTargetRunning(string executablePath);
}

/// <summary>父进程退出许可、整批文件事务和新版本健康确认。不初始化宿主或发送任何输入。</summary>
internal sealed class UpdateRunner
{
    private readonly IUpdateOperations _operations;
    private readonly UpdateRunnerTimeouts _timeouts;

    internal UpdateRunner(IUpdateOperations? operations = null, UpdateRunnerTimeouts? timeouts = null)
    {
        _operations = operations ?? new WindowsUpdateOperations();
        _timeouts = timeouts ?? UpdateRunnerTimeouts.Default;
    }

    internal async Task<UpdateRunResult> RunAsync(UpdateSession session, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        IUpdateProcess? child = null;
        IDisposable? lease = null;
        var attempted = new List<UpdateChange>();
        var confirmed = false;
        try
        {
            session.ValidatePaths();
            lease = UpdateBootstrap.AcquireLease(session.TargetPath);
            UpdateBootstrap.EnsureReleaseAllowed(session.TargetPath, session.Manifest);
            session.VerifyOriginal();
            session.VerifyCandidate();
            session.VerifyCurrentFiles();
            using var parent = _operations.OpenParent(session.ParentId)
                ?? throw new InvalidDataException("更新主进程已不存在，未获得可验证的退出握手");
            if (parent.StartTimeUtcTicks != session.ParentStartTimeUtcTicks ||
                !UpdateSession.SamePath(parent.ExecutablePath, session.TargetPath))
                throw new InvalidDataException("更新主进程身份不符");
            using (var helper = Process.GetCurrentProcess())
                session.MarkHelperReady(helper.Id, helper.StartTime.ToUniversalTime().Ticks);
            session.WritePhase("waiting-exit");

            if (!await WaitForParentAsync(parent, session, token).ConfigureAwait(false))
            {
                session.WritePhase("aborted", "主程序未结束或取消了更新，未替换任何文件");
                return new(false, false, false, "主程序未完成退出，更新已取消");
            }
            if (session.IsAborted || !session.IsApproved)
            {
                session.WritePhase("aborted", "没有完整停止与保存许可，未替换任何文件");
                return new(false, false, false, "主程序未确认停止和保存成功，更新未执行");
            }

            token.ThrowIfCancellationRequested();
            UpdateBootstrap.EnsureReleaseAllowed(session.TargetPath, session.Manifest);
            session.VerifyOriginal();
            session.VerifyCandidate();
            session.VerifyCurrentFiles();
            session.WritePhase("applying");
            foreach (var change in session.Changes)
            {
                token.ThrowIfCancellationRequested();
                session.ValidatePaths();
                var target = session.TargetFor(change.Path);
                var candidate = session.CandidateFor(change.Path);
                var backup = session.BackupFor(change.Path);
                // 整批准备后用户仍可能编辑后续资源；每个实际写入前再次核对旧状态。
                if (change.Existed) UpdateSession.VerifyHash(target, change.OriginalSha256);
                else if (File.Exists(target) || Directory.Exists(target))
                    throw new IOException($"更新中目标已出现，保留原文件：{change.Path}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                // 先记录尝试：ReplaceFile 返回失败时也可能已把旧文件移动到备份位置。
                attempted.Add(change);
                if (change.Existed) _operations.Replace(candidate, target, backup);
                else _operations.Add(candidate, target);
            }
            foreach (var entry in session.Manifest.Files)
                UpdateSession.VerifyFile(session.TargetFor(entry.Path), entry.Size, entry.Sha256);
            session.WritePhase("launching");
            child = _operations.StartUpdated(session);
            session.WritePhase("awaiting-health");
            if (!await WaitForHealthAsync(child, session, token).ConfigureAwait(false))
                throw new InvalidOperationException("新版本未在期限内完成启动确认");

            UpdateBootstrap.RecordAccepted(session);
            session.WritePhase("completed");
            // committed 是新程序解除输入/只读门禁的唯一许可。开始发布后不能再回退，
            // 即使记录/清理抛异常也不能结束可能已经收到许可的新程序。
            try { session.MarkCommitted(child.Id, child.StartTimeUtcTicks); }
            catch
            {
                // 极少数 I/O 错误可能出现在最终重命名之后；不能结束已经收到许可的进程。
                confirmed = session.HasCommittedProcess(child.Id, child.StartTimeUtcTicks);
                throw;
            }
            confirmed = true;
            return new(true, false, false, "更新完成，新版启动已确认");
        }
        catch (Exception error)
        {
            if (confirmed) return new(true, false, false, $"新版已启动；清理延后：{error.Message}");
            if (lease is not null)
                try { UpdateBootstrap.RecordFailure(session, error.Message); } catch { }
            if (child is not null)
            {
                bool stopped;
                try { stopped = child.HasExited || await child.StopOwnedAsync(_timeouts.StopChild).ConfigureAwait(false); }
                catch { stopped = false; }
                if (!stopped)
                {
                    TryWritePhase(session, "recovery-required", "未就绪新进程未退出，保留整批备份；" + error.Message);
                    return new(false, false, true, "更新未完成，新进程未退出，备份已保留；请关闭后再恢复");
                }
            }
            try
            {
                RollBack(session, attempted);
                var phase = attempted.Count > 0 ? "rolled-back" : "aborted";
                TryWritePhase(session, phase, error.Message);
                // 强制更新策略：恢复只保证文件可用，绝不自行重启旧版进入正常主界面。
                return new(false, attempted.Count > 0, false, $"更新失败，原文件已保留或恢复：{error.Message}");
            }
            catch (Exception rollbackError)
            {
                TryWritePhase(session, "recovery-required", $"{error.Message}；恢复失败：{rollbackError.Message}");
                return new(false, false, true, $"更新失败且自动恢复未完成，整批备份保留：{rollbackError.Message}");
            }
        }
        finally { child?.Dispose(); lease?.Dispose(); }
    }

    internal Task<UpdateRunResult> RecoverAsync(UpdateSession session, CancellationToken token = default)
    {
        var recoveryStarted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            session.ValidatePaths();
            using var lease = UpdateBootstrap.AcquireLease(session.TargetPath);
            if (session.HasCommitAcknowledgement ||
                session.ReadPhase() is not ("applying" or "launching" or "awaiting-health" or "recovery-required" or "completed"))
                throw new InvalidOperationException("该会话不允许中断恢复");
            if (_operations.IsTargetRunning(session.TargetPath))
                throw new InvalidOperationException("主程序仍在运行，已拒绝恢复；请先关闭它");
            recoveryStarted = true;
            RollBack(session, session.Changes);
            UpdateBootstrap.RecordFailure(session, "上次更新中断，已明确恢复原文件；仍需完成强制更新");
            session.WritePhase("rolled-back", "中断更新已恢复原文件，未自动启动旧版");
            return Task.FromResult(new UpdateRunResult(false, true, false, "原文件已恢复；未启动旧版主界面"));
        }
        catch (Exception error)
        {
            if (recoveryStarted) TryWritePhase(session, "recovery-required", error.Message);
            return Task.FromResult(new UpdateRunResult(false, false, true, $"恢复未完成，备份保持：{error.Message}"));
        }
    }

    private async Task<bool> WaitForParentAsync(IUpdateProcess parent, UpdateSession session, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (!parent.HasExited)
        {
            token.ThrowIfCancellationRequested();
            if (session.IsAborted || timer.Elapsed >= _timeouts.ParentExit) return false;
            await parent.WaitForExitAsync(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
        return true;
    }

    private async Task<bool> WaitForHealthAsync(IUpdateProcess child, UpdateSession session, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (!child.HasExited)
        {
            token.ThrowIfCancellationRequested();
            if (session.HasHealthyProcess(child.Id, child.StartTimeUtcTicks)) return true;
            if (timer.Elapsed >= _timeouts.Startup) return false;
            await child.WaitForExitAsync(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
        return false;
    }

    private void RollBack(UpdateSession session, IReadOnlyList<UpdateChange> attempted)
    {
        // 先确认整批可恢复，避免面对另一份新程序或用户改动时先回滚一部分资源。
        foreach (var change in attempted)
        {
            session.ValidatePaths();
            var entry = session.Manifest.Files.Single(file => file.Path == change.Path);
            var target = session.TargetFor(change.Path);
            if (change.Existed)
            {
                if (File.Exists(target) && UpdateSession.HashFile(target).Equals(change.OriginalSha256,
                        StringComparison.OrdinalIgnoreCase)) continue;
                UpdateSession.VerifyHash(session.BackupFor(change.Path), change.OriginalSha256);
                if (File.Exists(target)) UpdateSession.VerifyFile(target, entry.Size, entry.Sha256);
            }
            else if (File.Exists(target)) UpdateSession.VerifyFile(target, entry.Size, entry.Sha256);
        }
        var errors = new List<Exception>();
        foreach (var change in attempted.Reverse())
        {
            try
            {
                session.ValidatePaths();
                var entry = session.Manifest.Files.Single(file => file.Path == change.Path);
                var target = session.TargetFor(change.Path);
                var backup = session.BackupFor(change.Path);
                if (change.Existed)
                {
                    if (File.Exists(target) && UpdateSession.HashFile(target).Equals(change.OriginalSha256,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    UpdateSession.VerifyHash(backup, change.OriginalSha256);
                    if (File.Exists(target))
                    {
                        // 不覆盖事务之外出现的文件。
                        UpdateSession.VerifyFile(target, entry.Size, entry.Sha256);
                        var rejected = session.RejectedFor(change.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(rejected)!);
                        _operations.Replace(backup, target, rejected);
                    }
                    else _operations.Add(backup, target);
                    UpdateSession.VerifyHash(target, change.OriginalSha256);
                }
                else if (File.Exists(target))
                {
                    UpdateSession.VerifyFile(target, entry.Size, entry.Sha256);
                    _operations.RemoveAdded(target);
                }
            }
            catch (Exception error) { errors.Add(new IOException($"{change.Path}：{error.Message}", error)); }
        }
        if (errors.Count > 0) throw new AggregateException("部分文件尚需恢复", errors);
    }

    private static void TryWritePhase(UpdateSession session, string phase, string error)
    {
        try { session.WritePhase(phase, error); } catch { }
    }

    private sealed class WindowsUpdateOperations : IUpdateOperations
    {
        public IUpdateProcess? OpenParent(int processId)
        {
            Process? process = null;
            try { process = Process.GetProcessById(processId); return new WindowsProcess(process, owned: false); }
            catch (ArgumentException) { process?.Dispose(); return null; }
            catch { process?.Dispose(); throw; }
        }

        public IUpdateProcess StartUpdated(UpdateSession session)
        {
            var start = new ProcessStartInfo(session.TargetPath)
            {
                WorkingDirectory = session.InstallationDirectory,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("--update-start");
            start.ArgumentList.Add(session.SessionPath);
            start.ArgumentList.Add(session.Token);
            var process = Process.Start(start) ?? throw new IOException("无法启动新版程序");
            try { return new WindowsProcess(process, owned: true, session.TargetPath); }
            catch
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: false); process.WaitForExit(5000); } }
                finally { process.Dispose(); }
                throw;
            }
        }

        public void Replace(string source, string target, string backup) => File.Replace(source, target, backup, false);
        public void Add(string source, string target) => File.Move(source, target, overwrite: false);
        public void RemoveAdded(string target) => File.Delete(target);

        public bool IsTargetRunning(string executablePath)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath)))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && UpdateSession.SamePath(process.MainModule?.FileName ?? "", executablePath)) return true;
                    }
                    catch { return true; }
                }
            }
            return false;
        }
    }

    private sealed class WindowsProcess : IUpdateProcess
    {
        private readonly Process _process;
        private readonly bool _owned;
        internal WindowsProcess(Process process, bool owned, string? knownPath = null)
        {
            _process = process;
            _owned = owned;
            Id = process.Id;
            StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            ExecutablePath = knownPath ?? process.MainModule?.FileName ?? throw new IOException("无法确认进程映像路径");
        }
        public int Id { get; }
        public long StartTimeUtcTicks { get; }
        public string ExecutablePath { get; }
        public bool HasExited => _process.HasExited;

        public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            try
            {
                await _process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return _process.HasExited; }
        }

        public async Task<bool> StopOwnedAsync(TimeSpan timeout)
        {
            if (!_owned) throw new InvalidOperationException("不能结束非本更新器创建的进程");
            if (HasExited) return true;
            _process.CloseMainWindow();
            if (await WaitForExitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false)) return true;
            _process.Kill(entireProcessTree: false);
            return await WaitForExitAsync(timeout, CancellationToken.None).ConfigureAwait(false);
        }

        public void Dispose() => _process.Dispose();
    }
}
