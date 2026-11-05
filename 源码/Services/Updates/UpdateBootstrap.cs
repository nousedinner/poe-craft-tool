using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace ShiKe.Services.Updates;

/// <summary>只处理独立更新模式与会话，不创建 WPF、宿主、热键、存储或网络服务。</summary>
internal static class UpdateBootstrap
{
    internal static bool TryRunHelper(string[] args, out int exitCode)
    {
        exitCode = 64;
        if (!args.Contains("--apply-update", StringComparer.Ordinal) &&
            !args.Contains("--recover-update", StringComparer.Ordinal)) return false;
        try
        {
            if (args.Length != 3 || args[0] is not ("--apply-update" or "--recover-update")) return true;
            var session = UpdateSession.Load(args[1], args[2], ReleaseTrust.PublicKeyPem, ReleaseTrust.KeyId);
            if (!UpdateSession.SamePath(Environment.ProcessPath!, session.HelperPath) ||
                !session.OriginalVersion.Equals(new Version(NetworkService.CurrentVersion.Major,
                    NetworkService.CurrentVersion.Minor, NetworkService.CurrentVersion.Build))) return true;
            UpdateSession.VerifyHash(session.HelperPath, session.OriginalSha256);
            using var bounded = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var runner = new UpdateRunner();
            var result = (args[0] == "--recover-update" ? runner.RecoverAsync(session, bounded.Token) : runner.RunAsync(session, bounded.Token))
                .GetAwaiter().GetResult();
            exitCode = result.Succeeded ? 0 : result.NeedsRecovery ? 4 : result.RolledBack ? 3 : 2;
        }
        catch { exitCode = 64; }
        return true;
    }

    internal static UpdateStartup? ReadStartup(string[] args)
    {
        if (args.Contains("--update-rollback", StringComparer.Ordinal))
            throw new InvalidDataException("不允许绕过强制更新启动旧版");
        if (!args.Contains("--update-start", StringComparer.Ordinal)) return null;
        if (args.Length != 3 || args[0] != "--update-start")
            throw new InvalidDataException("更新启动参数无效");
        var session = UpdateSession.Load(args[1], args[2], ReleaseTrust.PublicKeyPem, ReleaseTrust.KeyId);
        if (!UpdateSession.SamePath(Environment.ProcessPath!, session.TargetPath) ||
            !session.Manifest.Version.Equals(new Version(NetworkService.CurrentVersion.Major,
                NetworkService.CurrentVersion.Minor, NetworkService.CurrentVersion.Build)) ||
            session.ReadPhase() is not ("launching" or "awaiting-health") || !IsUpdateBusy(session.TargetPath))
            throw new InvalidDataException("更新启动身份或阶段不符");
        foreach (var entry in session.Manifest.Files)
            UpdateSession.VerifyFile(session.TargetFor(entry.Path), entry.Size, entry.Sha256);
        return new UpdateStartup(session);
    }

    internal static async Task StartHelperAsync(UpdateSession session, CancellationToken token)
    {
        session.ValidatePaths();
        session.VerifyOriginal();
        session.VerifyCandidate();
        session.VerifyCurrentFiles();
        // 复制当前可运行版本，避免让尚未启动验证的新版本负责恢复旧文件。
        File.Copy(session.TargetPath, session.HelperPath, overwrite: false);
        UpdateSession.VerifyHash(session.HelperPath, session.OriginalSha256);
        var start = new ProcessStartInfo(session.HelperPath)
        {
            WorkingDirectory = session.DirectoryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(session.SessionPath);
        start.ArgumentList.Add(session.Token);
        using var helper = Process.Start(start) ?? throw new IOException("无法启动更新助手");
        var timer = Stopwatch.StartNew();
        try
        {
            while (!session.HasHelperReady)
            {
                token.ThrowIfCancellationRequested();
                if (helper.HasExited) throw new IOException($"更新助手未就绪（退出码 {helper.ExitCode}）");
                if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("更新助手准备超时");
                await Task.Delay(50, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (helper.HasExited) throw new IOException("更新助手在交接前退出");
        }
        catch
        {
            try { session.Abort("主程序取消或无法完成更新交接"); } catch { }
            // 本方法只结束自己刚刚创建、尚未获得退出许可的助手，不碰现有程序实例。
            if (!helper.HasExited)
            {
                try
                {
                    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    helper.Kill(entireProcessTree: false);
                    await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            throw;
        }
    }

    internal static bool IsUpdateBusy(string executablePath)
    {
        var path = LeasePath(executablePath);
        if (!File.Exists(path)) return false;
        try
        {
            UpdateSession.EnsureNoReparsePoints(path);
            using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    internal static IDisposable AcquireLease(string executablePath)
    {
        var path = LeasePath(executablePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        UpdateSession.EnsureNoReparsePoints(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static string LeasePath(string executablePath)
    {
        var directory = Path.GetDirectoryName(UpdateSession.ValidateTargetPath(executablePath))!.ToUpperInvariant();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)));
        return Path.Combine(UpdateSession.UpdateRoot(executablePath), "lease-" + identity + ".lock");
    }

    private static string LedgerPath(string executablePath)
        // 与安装目录一起保留，不能因用户重命名主 EXE 而丢失失败记录和已接受版本。
        => Path.Combine(UpdateSession.UpdateRoot(executablePath), "release.json");

    /// <summary>只决定是否暂停重复自动尝试；调用方仍须保持强制更新阻断，不能因此放行旧主界面。</summary>
    internal static bool IsFailedRelease(string executablePath, UpdateManifest manifest)
    {
        var path = LedgerPath(executablePath);
        if (!File.Exists(path)) return false;
        try
        {
            var state = ReadLedger(path);
            return state.FailedVersion == manifest.VersionText &&
                   state.FailedPackageSha256.Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>此门禁不能由“重试失败版本”覆盖；受控事务回滚不会调用它。</summary>
    internal static void EnsureReleaseAllowed(string executablePath, UpdateManifest manifest)
    {
        var path = LedgerPath(executablePath);
        if (!File.Exists(path)) return;
        var state = ReadLedger(path);
        if (state.HighestAcceptedVersion.Length == 0) return;
        if (!Version.TryParse(state.HighestAcceptedVersion, out var accepted))
            throw new InvalidDataException("已接受版本记录损坏，已停止自动更新");
        if (manifest.Version < accepted)
            throw new InvalidDataException("发行清单低于本机已接受版本，已拒绝降级");
        if (manifest.Version == accepted && !state.AcceptedPackageSha256.Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("相同已接受版本的更新包内容发生变化，已拒绝覆盖");
    }

    internal static void RecordFailure(UpdateSession session, string reason)
    {
        var path = LedgerPath(session.TargetPath);
        var state = File.Exists(path) ? ReadLedger(path) : new ReleaseLedger();
        state.FailedVersion = session.Manifest.VersionText;
        state.FailedPackageSha256 = session.Manifest.PackageSha256;
        state.FailureReason = reason;
        UpdateSession.WriteJson(path, state);
    }

    internal static void RecordAccepted(UpdateSession session)
    {
        var path = LedgerPath(session.TargetPath);
        var state = File.Exists(path) ? ReadLedger(path) : new ReleaseLedger();
        if (!Version.TryParse(state.HighestAcceptedVersion, out var accepted) || accepted < session.Manifest.Version)
        {
            state.HighestAcceptedVersion = session.Manifest.VersionText;
            state.AcceptedPackageSha256 = session.Manifest.PackageSha256;
        }
        state.FailedVersion = "";
        state.FailedPackageSha256 = "";
        state.FailureReason = "";
        UpdateSession.WriteJson(path, state);
    }

    private static ReleaseLedger ReadLedger(string path)
    {
        var state = UpdateSession.ReadJson<ReleaseLedger>(path, 16384);
        if (state.Schema != 1) throw new InvalidDataException("更新版本记录格式无效");
        return state;
    }

    /// <summary>上次事务在中途失去助手时不能启动混合版本；保留所有备份供明确恢复。</summary>
    internal static void EnsureNoIncompleteTransaction(string executablePath)
        => EnsureNoIncompleteTransaction(executablePath, ReleaseTrust.PublicKeyPem, ReleaseTrust.KeyId);

    internal static void EnsureNoIncompleteTransaction(string executablePath, string publicKeyPem, string keyId)
    {
        var root = UpdateSession.UpdateRoot(executablePath);
        if (!Directory.Exists(root)) return;
        UpdateSession.EnsureNoReparsePoints(root);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            var statePath = Path.Combine(directory, "state.json");
            if (!File.Exists(statePath)) continue;
            var state = UpdateSession.ReadJson<InspectionState>(statePath, 16384);
            if (state.Phase is not ("applying" or "launching" or "awaiting-health" or "recovery-required" or "completed")) continue;
            var session = UpdateSession.LoadOwned(Path.Combine(directory, "session.json"), publicKeyPem, keyId);
            if (state.Phase == "completed" && session.HasCommitAcknowledgement) continue;
            throw new InvalidDataException($"检测到未完成的更新，已阻止启动混合版本。请先关闭拾刻后恢复；备份和更新助手保留在：{directory}");
        }
    }

    internal static void CleanupCompleted(string executablePath)
    {
        var root = UpdateSession.UpdateRoot(executablePath);
        if (!Directory.Exists(root) || IsUpdateBusy(executablePath)) return;
        try
        {
            using var lease = AcquireLease(executablePath);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                try
                {
                    var session = UpdateSession.LoadOwned(Path.Combine(directory, "session.json"),
                        ReleaseTrust.PublicKeyPem, ReleaseTrust.KeyId);
                    var phase = session.ReadPhase();
                    if (!UpdateSession.SamePath(session.TargetPath, executablePath) || session.IsHelperAlive() ||
                        phase is not ("completed" or "aborted" or "rolled-back") ||
                        (phase == "completed" && !session.HasCommitAcknowledgement)) continue;
                    ValidateCleanupContents(session);
                    Directory.Delete(session.DirectoryPath, recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                {
                    // 不识别、含用户文件、尚在运行或需恢复的目录都保留，不扩大删除范围。
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void ValidateCleanupContents(UpdateSession session)
    {
        session.ValidatePaths();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "session.json", "state.json", "permit.json", "abort.json", "helper-ready.json", "healthy.json",
            "committed.json", "runner.exe", "download/package.zip", "download/package.zip.part",
        };
        foreach (var entry in session.Manifest.Files)
            foreach (var prefix in new[] { "candidates/", "backups/", "rejected/", "download/candidates/" })
            {
                allowed.Add(prefix + entry.Path);
                if (prefix == "download/candidates/") allowed.Add(prefix + entry.Path + ".part");
            }
        foreach (var path in Directory.EnumerateFileSystemEntries(session.DirectoryPath, "*", SearchOption.AllDirectories))
        {
            UpdateSession.EnsureNoReparsePoints(path);
            if (Directory.Exists(path)) continue;
            var relative = Path.GetRelativePath(session.DirectoryPath, path).Replace('\\', '/');
            if (!allowed.Contains(relative)) throw new InvalidDataException("更新目录含未知文件，保留供核对");
        }
    }

    private sealed class ReleaseLedger
    {
        public int Schema { get; set; } = 1;
        public string HighestAcceptedVersion { get; set; } = "";
        public string AcceptedPackageSha256 { get; set; } = "";
        public string FailedVersion { get; set; } = "";
        public string FailedPackageSha256 { get; set; } = "";
        public string FailureReason { get; set; } = "";
    }

    private sealed class InspectionState
    {
        public string Phase { get; set; } = "";
    }
}

internal sealed class UpdateStartup
{
    private readonly UpdateSession _session;
    internal UpdateStartup(UpdateSession session) => _session = session;
    internal bool IsRollback => false;
    internal UpdateSession Session => _session;
    internal void MarkHealthy() => MarkHealthyAsync().GetAwaiter().GetResult();

    internal async Task MarkHealthyAsync(CancellationToken token = default)
    {
        using var process = Process.GetCurrentProcess();
        if (!UpdateSession.SamePath(process.MainModule?.FileName ?? "", _session.TargetPath))
            throw new InvalidDataException("健康确认不是目标主进程发出");
        var id = process.Id;
        var started = process.StartTime.ToUniversalTime().Ticks;
        _session.MarkHealthy(id, started);
        await WaitForCommitAsync(_session, id, started, TimeSpan.FromSeconds(70), token).ConfigureAwait(false);
    }

    internal static async Task WaitForCommitAsync(UpdateSession session, int id, long started,
        TimeSpan timeout, CancellationToken token = default)
    {
        var timer = Stopwatch.StartNew();
        while (!session.HasCommittedProcess(id, started))
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed > timeout ||
                session.ReadPhase() is "aborted" or "rolled-back" or "recovery-required" ||
                !UpdateBootstrap.IsUpdateBusy(session.TargetPath))
                throw new InvalidOperationException("更新事务未确认，新版仍保持未启用状态");
            await Task.Delay(50, token).ConfigureAwait(false);
        }
    }
}
