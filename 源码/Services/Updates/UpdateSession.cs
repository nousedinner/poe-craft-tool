using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShiKe.Services.Updates;

/// <summary>一次更新的固定文件边界与握手。更新文件全部位于主程序旁的专用会话目录。</summary>
internal sealed class UpdateSession
{
    internal const string RootDirectoryName = ".shike-updates";
    private const string Owner = "ShiKe.UpdateSession.v1";
    private const int MaximumSessionBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SessionDocument _document;

    private UpdateSession(string path, SessionDocument document, UpdateManifest manifest)
    {
        SessionPath = path;
        DirectoryPath = Path.GetDirectoryName(path)!;
        _document = document;
        Manifest = manifest;
    }

    internal string SessionPath { get; }
    internal string DirectoryPath { get; }
    internal string DownloadDirectory => Path.Combine(DirectoryPath, "download");
    internal string CandidatePath => CandidateFor("拾刻.exe");
    internal string HelperPath => Path.Combine(DirectoryPath, "runner.exe");
    internal string BackupPath => BackupFor("拾刻.exe");
    internal string TargetPath => _document.TargetPath;
    internal string InstallationDirectory => Path.GetDirectoryName(TargetPath)!;
    internal string Token => _document.Token;
    internal string OriginalSha256 => _document.OriginalSha256;
    internal Version OriginalVersion => Version.Parse(_document.OriginalVersion);
    internal int ParentId => _document.ParentId;
    internal long ParentStartTimeUtcTicks => _document.ParentStartTimeUtcTicks;
    internal UpdateManifest Manifest { get; }
    internal IReadOnlyList<UpdateChange> Changes => _document.Changes;

    internal static UpdateSession Create(UpdateManifest manifest, string currentExecutablePath)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法确认当前程序路径");
        if (!SamePath(processPath, currentExecutablePath))
            throw new InvalidDataException("更新目标必须是当前运行的主程序");
        using var process = Process.GetCurrentProcess();
        return CreateCore(manifest, currentExecutablePath, NetworkService.CurrentVersion,
            process.Id, process.StartTime.ToUniversalTime().Ticks);
    }

    internal static UpdateSession CreateCore(UpdateManifest manifest, string targetPath, Version currentVersion,
        int parentId, long parentStartTimeUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.CanApply(currentVersion)) throw new InvalidDataException("本版本不允许兼容文件自动更新");
        targetPath = ValidateTargetPath(targetPath);
        UpdateBootstrap.EnsureReleaseAllowed(targetPath, manifest);
        if (!File.Exists(targetPath)) throw new FileNotFoundException("原主程序不存在", targetPath);
        var changes = PlanChanges(manifest, targetPath);
        var root = UpdateRoot(targetPath);
        Directory.CreateDirectory(root);
        EnsureNoReparsePoints(root);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        var document = new SessionDocument
        {
            Owner = Owner, SessionId = id, Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            TargetPath = targetPath, OriginalSha256 = HashFile(targetPath),
            OriginalVersion = currentVersion.ToString(3), ParentId = parentId,
            ParentStartTimeUtcTicks = parentStartTimeUtcTicks, ManifestJson = manifest.SourceJson,
            Changes = changes,
        };
        var path = Path.Combine(directory, "session.json");
        WriteJson(path, document);
        var session = new UpdateSession(path, document, manifest);
        session.WritePhase("prepared");
        return session;
    }

    internal async Task PrepareCandidateAsync(string downloadedCandidatePath, CancellationToken token)
    {
        ValidatePaths();
        if (!SamePath(downloadedCandidatePath, Path.Combine(DownloadDirectory, "candidates", "拾刻.exe")))
            throw new InvalidDataException("候选程序不在本次专用下载目录");
        foreach (var entry in Manifest.Files)
        {
            var downloaded = Path.Combine(DownloadDirectory, "candidates", entry.Path.Replace('/', Path.DirectorySeparatorChar));
            EnsureNoReparsePoints(downloaded);
            VerifyFile(downloaded, entry.Size, entry.Sha256);
            var candidate = CandidateFor(entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
            await using var source = new FileStream(downloaded, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            await source.CopyToAsync(destination, token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        VerifyCandidate();
    }

    internal static UpdateSession Load(string path, string token, string publicKeyPem, string expectedKeyId)
    {
        path = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) != "session.json")
            throw new InvalidDataException("更新会话路径无效");
        EnsureNoReparsePoints(path);
        var document = ReadJson<SessionDocument>(path, MaximumSessionBytes);
        if (document.Owner != Owner || !Guid.TryParseExact(document.SessionId, "N", out _) ||
            string.IsNullOrEmpty(document.Token) || document.Token.Length != 64 ||
            !document.Token.All(Uri.IsHexDigit) || !SameToken(document.Token, token))
            throw new InvalidDataException("更新会话身份无效");
        var target = ValidateTargetPath(document.TargetPath);
        var expectedDirectory = Path.Combine(UpdateRoot(target), document.SessionId);
        if (!SamePath(Path.GetDirectoryName(path)!, expectedDirectory) ||
            document.ParentId <= 0 || document.ParentStartTimeUtcTicks <= 0 ||
            !IsHash(document.OriginalSha256) || !Version.TryParse(document.OriginalVersion, out var originalVersion))
            throw new InvalidDataException("更新会话超出主程序目录或缺少进程身份");
        var manifest = UpdateManifest.Verify(document.ManifestJson, publicKeyPem, expectedKeyId);
        if (!manifest.CanApply(originalVersion)) throw new InvalidDataException("更新会话不允许本次升级");
        var session = new UpdateSession(path, document, manifest);
        session.ValidatePaths();
        session.ValidateChangePlan();
        return session;
    }

    internal void ValidatePaths()
    {
        var target = ValidateTargetPath(TargetPath);
        if (!SamePath(DirectoryPath, Path.Combine(UpdateRoot(target), _document.SessionId)))
            throw new InvalidDataException("更新会话目录不符");
        foreach (var path in new[] { SessionPath, CandidatePath, HelperPath, BackupPath,
                     Path.Combine(DirectoryPath, "state.json"), Path.Combine(DirectoryPath, "permit.json"),
                     Path.Combine(DirectoryPath, "abort.json"), Path.Combine(DirectoryPath, "healthy.json"),
                     Path.Combine(DirectoryPath, "committed.json"),
                     Path.Combine(DirectoryPath, "helper-ready.json") }) EnsureNoReparsePoints(path);
        foreach (var entry in Manifest.Files)
        {
            EnsureNoReparsePoints(TargetFor(entry.Path));
            EnsureNoReparsePoints(CandidateFor(entry.Path));
            EnsureNoReparsePoints(BackupFor(entry.Path));
            EnsureNoReparsePoints(RejectedFor(entry.Path));
        }
    }

    internal void VerifyCandidate()
    {
        foreach (var entry in Manifest.Files) VerifyFile(CandidateFor(entry.Path), entry.Size, entry.Sha256);
    }
    internal void VerifyOriginal() => VerifyHash(TargetPath, OriginalSha256);

    internal void VerifyCurrentFiles()
    {
        ValidatePaths();
        ValidateChangePlan();
        foreach (var entry in Manifest.Files)
        {
            var change = Changes.SingleOrDefault(item => item.Path == entry.Path);
            var target = TargetFor(entry.Path);
            if (change is null) VerifyFile(target, entry.Size, entry.Sha256);
            else if (change.Existed) VerifyHash(target, change.OriginalSha256);
            else if (File.Exists(target) || Directory.Exists(target))
                throw new IOException($"更新准备后文件已出现，已保留：{entry.Path}");
        }
    }

    internal string TargetFor(string relativePath)
        => relativePath == "拾刻.exe" ? TargetPath : BoundedFile(InstallationDirectory, relativePath);
    internal string CandidateFor(string relativePath) => BoundedFile(Path.Combine(DirectoryPath, "candidates"), relativePath);
    internal string BackupFor(string relativePath) => BoundedFile(Path.Combine(DirectoryPath, "backups"), relativePath);
    internal string RejectedFor(string relativePath) => BoundedFile(Path.Combine(DirectoryPath, "rejected"), relativePath);

    private static List<UpdateChange> PlanChanges(UpdateManifest manifest, string targetPath)
    {
        var result = new List<UpdateChange>();
        var installation = Path.GetDirectoryName(targetPath)!;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            var target = entry.Path == "拾刻.exe" ? targetPath : BoundedFile(installation, entry.Path);
            EnsureNoReparsePoints(target);
            if (!targets.Add(target)) throw new InvalidDataException("主程序实际名称与清单文件发生冲突");
            if (Directory.Exists(target)) throw new IOException($"目标文件位置是目录：{entry.Path}");
            if (File.Exists(target))
            {
                var hash = HashFile(target);
                if (hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Path != "拾刻.exe" && !entry.BaseSha256.Contains(hash, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException($"文件与可信旧版不符，可能已被用户修改，已停止整批更新：{entry.Path}");
                result.Add(new UpdateChange { Path = entry.Path, Existed = true, OriginalSha256 = hash });
            }
            else
            {
                if (!entry.AllowCreate || entry.Path == "拾刻.exe")
                    throw new InvalidDataException($"清单未允许新增此文件：{entry.Path}");
                result.Add(new UpdateChange { Path = entry.Path, Existed = false });
            }
        }
        return result;
    }

    private void ValidateChangePlan()
    {
        if (_document.Changes is null || _document.Changes.Count > Manifest.Files.Count)
            throw new InvalidDataException("更新变更计划无效");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in Changes)
        {
            var entry = Manifest.Files.SingleOrDefault(file => file.Path == change.Path);
            if (entry is null || !seen.Add(change.Path)) throw new InvalidDataException("更新计划包含未签名或重复文件");
            if (change.Existed)
            {
                if (!IsHash(change.OriginalSha256) ||
                    (change.Path == "拾刻.exe" ? !change.OriginalSha256.Equals(OriginalSha256, StringComparison.OrdinalIgnoreCase)
                        : !entry.BaseSha256.Contains(change.OriginalSha256, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidDataException("旧文件基线不可信");
            }
            else if (!entry.AllowCreate || change.Path == "拾刻.exe" || change.OriginalSha256.Length != 0)
                throw new InvalidDataException("更新计划包含未授权新增文件");
        }
    }

    private static string BoundedFile(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\') ||
            relativePath.Split('/').Any(segment => segment is "" or "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("更新文件相对路径无效");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新文件超出允许目录");
        return path;
    }

    internal void ApproveExit()
    {
        ValidatePaths();
        if (!HasHelperReady) throw new InvalidOperationException("更新助手尚未就绪，不能许可替换");
        WriteJson(Path.Combine(DirectoryPath, "permit.json"), new SignalDocument { Token = Token });
    }

    internal void Abort(string reason)
    {
        ValidatePaths();
        WriteJson(Path.Combine(DirectoryPath, "abort.json"), new SignalDocument { Token = Token, Reason = reason });
        WritePhase("aborted", reason);
    }

    internal bool IsApproved => HasSignal("permit.json");
    internal bool IsAborted => HasSignal("abort.json");
    internal bool HasHelperReady => HasSignal("helper-ready.json");

    internal void MarkHelperReady(int helperId, long helperStartTicks)
    {
        ValidatePaths();
        WriteJson(Path.Combine(DirectoryPath, "helper-ready.json"),
            new SignalDocument { Token = Token, ProcessId = helperId, ProcessStartTimeUtcTicks = helperStartTicks });
    }

    internal void MarkHealthy(int processId, long processStartTicks)
    {
        ValidatePaths();
        foreach (var entry in Manifest.Files) VerifyFile(TargetFor(entry.Path), entry.Size, entry.Sha256);
        WriteJson(Path.Combine(DirectoryPath, "healthy.json"), new SignalDocument
        {
            Token = Token, ProcessId = processId, ProcessStartTimeUtcTicks = processStartTicks,
            ExecutableSha256 = Manifest.ExeSha256,
        });
    }

    internal bool HasHealthyProcess(int processId, long processStartTicks)
        => HasProcessSignal("healthy.json", processId, processStartTicks);

    internal void MarkCommitted(int processId, long processStartTicks)
        => WriteJson(Path.Combine(DirectoryPath, "committed.json"), new SignalDocument
        {
            Token = Token, ProcessId = processId, ProcessStartTimeUtcTicks = processStartTicks,
            ExecutableSha256 = Manifest.ExeSha256,
        });

    internal bool HasCommittedProcess(int processId, long processStartTicks)
        => HasProcessSignal("committed.json", processId, processStartTicks);

    internal bool HasCommitAcknowledgement
    {
        get
        {
            var path = Path.Combine(DirectoryPath, "committed.json");
            if (!File.Exists(path)) return false;
            var signal = ReadJson<SignalDocument>(path, 8192);
            return SameToken(Token, signal.Token) && signal.ProcessId > 0 && signal.ProcessStartTimeUtcTicks > 0 &&
                   signal.ExecutableSha256.Equals(Manifest.ExeSha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool HasProcessSignal(string name, int processId, long processStartTicks)
    {
        var path = Path.Combine(DirectoryPath, name);
        if (!File.Exists(path)) return false;
        var signal = ReadJson<SignalDocument>(path, 8192);
        return SameToken(Token, signal.Token) && signal.ProcessId == processId &&
               signal.ProcessStartTimeUtcTicks == processStartTicks &&
               string.Equals(signal.ExecutableSha256, Manifest.ExeSha256, StringComparison.OrdinalIgnoreCase);
    }

    internal void WritePhase(string phase, string? error = null)
    {
        ValidatePaths();
        WriteJson(Path.Combine(DirectoryPath, "state.json"), new StateDocument
            { Token = Token, Phase = phase, Error = error, UpdatedUtc = DateTimeOffset.UtcNow });
    }

    internal string ReadPhase()
    {
        var state = ReadJson<StateDocument>(Path.Combine(DirectoryPath, "state.json"), 16384);
        if (!SameToken(Token, state.Token)) throw new InvalidDataException("更新状态身份无效");
        return state.Phase;
    }

    internal static UpdateSession LoadOwned(string path, string publicKeyPem, string keyId)
    {
        var document = ReadJson<SessionDocument>(path, MaximumSessionBytes);
        return Load(path, document.Token, publicKeyPem, keyId);
    }

    internal bool IsHelperAlive()
    {
        var path = Path.Combine(DirectoryPath, "helper-ready.json");
        if (!File.Exists(path)) return false;
        var signal = ReadJson<SignalDocument>(path, 8192);
        if (!SameToken(Token, signal.Token)) return true;
        try
        {
            using var process = Process.GetProcessById(signal.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == signal.ProcessStartTimeUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    private bool HasSignal(string name)
    {
        var path = Path.Combine(DirectoryPath, name);
        if (!File.Exists(path)) return false;
        var signal = ReadJson<SignalDocument>(path, 8192);
        return SameToken(Token, signal.Token);
    }

    internal static string UpdateRoot(string executablePath)
        => Path.Combine(Path.GetDirectoryName(ValidateTargetPath(executablePath))!, RootDirectoryName);

    internal static string TargetIdentity(string executablePath)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executablePath).ToUpperInvariant())));

    internal static string ValidateTargetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("主程序路径必须为绝对路径");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(full).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("自动更新只支持本地磁盘中的主 EXE");
        EnsureNoReparsePoints(full);
        return full;
    }

    internal static void EnsureNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"更新路径包含链接，已拒绝操作：{current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (SamePath(current, Path.GetPathRoot(current)!)) break;
        }
    }

    internal static bool SamePath(string first, string second)
        => string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    internal static string HashFile(string path)
    {
        EnsureNoReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void VerifyHash(string path, string expected)
    {
        if (!string.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新文件哈希不符，已拒绝替换");
    }

    internal static void VerifyFile(string path, long expectedSize, string expectedHash)
    {
        EnsureNoReparsePoints(path);
        if (new FileInfo(path).Length != expectedSize) throw new InvalidDataException("更新文件大小不符");
        VerifyHash(path, expectedHash);
    }

    private static bool IsHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool SameToken(string first, string second)
        => first.Length == second.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));

    internal static T ReadJson<T>(string path, int limit)
    {
        EnsureNoReparsePoints(path);
        // 读取期间允许状态文件原子换名，读者仍得到已打开句柄的完整旧快照。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > limit) throw new InvalidDataException("更新状态文件超过大小限制");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException("更新状态文件为空");
    }

    internal static void WriteJson<T>(string path, T value)
    {
        EnsureNoReparsePoints(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class SessionDocument
    {
        public string Owner { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string Token { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public string OriginalSha256 { get; set; } = "";
        public string OriginalVersion { get; set; } = "";
        public int ParentId { get; set; }
        public long ParentStartTimeUtcTicks { get; set; }
        public string ManifestJson { get; set; } = "";
        public List<UpdateChange> Changes { get; set; } = [];
    }

    internal sealed class SignalDocument
    {
        public string Token { get; set; } = "";
        public string Reason { get; set; } = "";
        public int ProcessId { get; set; }
        public long ProcessStartTimeUtcTicks { get; set; }
        public string ExecutableSha256 { get; set; } = "";
    }

    private sealed class StateDocument
    {
        public string Token { get; set; } = "";
        public string Phase { get; set; } = "";
        public string? Error { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }
}

internal sealed class UpdateChange
{
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public string OriginalSha256 { get; set; } = "";
}
