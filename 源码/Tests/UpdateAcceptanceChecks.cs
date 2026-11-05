using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShiKe.Services.Updates;

// 文件、Windows占用/只读属性和ZIP读写均是真实操作；进程与HTTP仅为受控替身，不执行样本内容。
internal static class UpdateAcceptanceChecks
{
    private static readonly List<object> Reports = [];
    private static string? _outputRoot;
    private static readonly byte[] OldMain = Encoding.UTF8.GetBytes("old executable fixture; never executed");
    private static readonly byte[] NewMain = Encoding.UTF8.GetBytes("new executable fixture; never executed");

    public static void UnchangedManifestFileIsNeverWritten() => WithFixture("unchanged", f =>
    {
        var same = Encoding.UTF8.GetBytes("unchanged official library");
        f.Existing("Stable.dll", same);
        f.Entry("拾刻.exe", NewMain);
        f.Entry("Stable.dll", same);
        var beforeHash = HashFile(f.Local("Stable.dll"));
        var beforeTime = File.GetLastWriteTimeUtc(f.Local("Stable.dll"));
        var session = f.Prepare();
        Require(session.Changes.Count == 1 && session.Changes[0].Path == "拾刻.exe", "未变化文件不得进入变更计划");
        var operations = new RealFileOperations(session);
        var result = Run(session, operations);
        Require(result.Succeeded, "仅主程序变化时整批应成功");
        Require(operations.Writes.SequenceEqual(new[] { "replace:拾刻.exe" }), "安装操作不得写入未变化文件");
        Require(HashFile(f.Local("Stable.dll")) == beforeHash && File.GetLastWriteTimeUtc(f.Local("Stable.dll")) == beforeTime,
            "未变化文件的内容与最后写入时间都必须保持");
        Require(!File.Exists(session.BackupFor("Stable.dll")), "未变化文件不应生成替换备份");
        f.AssertPersonalFiles();
        f.Report(new { declaredFiles = 2, changedFiles = session.Changes.Count, writes = operations.Writes, unchangedHashAndTimestamp = true });
    });

    public static void ExeOnlyTransactionKeepsConfiguration() => WithFixture("exe-only", f =>
    {
        f.Existing("Unlisted.dll", Encoding.UTF8.GetBytes("unlisted player-owned file"));
        var unlistedHash = HashFile(f.Local("Unlisted.dll"));
        var unlistedTime = File.GetLastWriteTimeUtc(f.Local("Unlisted.dll"));
        f.Entry("拾刻.exe", NewMain);
        var session = f.Prepare();
        Require(session.Manifest.Mode == "exe-only" && session.Manifest.Files.Count == 1, "本案必须使用EXE-only清单");
        var operations = new RealFileOperations(session);
        var result = Run(session, operations);
        Require(result.Succeeded && operations.Starts == 1 && session.HasCommitAcknowledgement,
            "EXE-only须完成原进程退出许可、真实替换及双向提交确认");
        Require(File.ReadAllBytes(f.Target).SequenceEqual(NewMain) && File.ReadAllBytes(session.BackupPath).SequenceEqual(OldMain),
            "主程序须更新且备份保留本轮旧文件");
        Require(operations.Writes.SequenceEqual(new[] { "replace:拾刻.exe" }), "EXE-only只能替换主程序一次");
        Require(HashFile(f.Local("Unlisted.dll")) == unlistedHash && File.GetLastWriteTimeUtc(f.Local("Unlisted.dll")) == unlistedTime,
            "未列入清单的DLL不得被写入");
        f.AssertPersonalFiles();
        f.Report(new { mode = session.Manifest.Mode, declaredFiles = 1, changedFiles = 1, operations.Starts, writes = operations.Writes,
            packageBytes = f.PackageBytes, protectedFiles = f.ProtectedCount });
    });

    public static void LockedMainFileRollsBackEarlierChanges() => WithFixture("locked-main", f =>
    {
        var oldLibrary = Encoding.UTF8.GetBytes("old official library");
        f.Existing("Library.dll", oldLibrary);
        // 主EXE故意最后替换，验证真实占用失败还会撤销之前已完成的替换和新增。
        f.Entry("Added.dll", Encoding.UTF8.GetBytes("new managed library"), allowCreate: true);
        f.Entry("Library.dll", Encoding.UTF8.GetBytes("new official library"));
        f.Entry("拾刻.exe", NewMain);
        var session = f.Prepare();
        var operations = new RealFileOperations(session);
        UpdateRunResult result;
        using (var occupied = new FileStream(f.Target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = Run(session, operations);
            Require(!result.Succeeded && result.RolledBack && !result.NeedsRecovery && operations.Starts == 0,
                "真实文件占用必须阻止主EXE替换，并恢复已变更文件");
            Require(operations.LastWriteFailure is IOException or UnauthorizedAccessException, "本案须观察到真实Windows文件替换错误");
            Require(File.ReadAllBytes(f.Target).SequenceEqual(OldMain), "被占用的旧主程序应保持");
            Require(File.ReadAllBytes(f.Local("Library.dll")).SequenceEqual(oldLibrary) && !File.Exists(f.Local("Added.dll")),
                "主程序失败前的DLL替换与新增必须回滚");
            Require(operations.Writes.Contains("replace:Library.dll") && operations.Writes.Contains("remove:Added.dll"),
                "必须实际覆盖先改后回滚路径");
            f.AssertPersonalFiles();
        }
        f.Report(new { failure = operations.LastWriteFailure!.GetType().Name, result.RolledBack, writes = operations.Writes,
            realExclusiveDeleteDenial = true, protectedFiles = f.ProtectedCount });
    });

    public static void ReadOnlyResourceRollsBackChangedExecutable() => WithFixture("readonly-resource", f =>
    {
        var oldSound = Encoding.UTF8.GetBytes("old official sound bytes");
        f.Existing("sounds/official.mp3", oldSound);
        f.Entry("拾刻.exe", NewMain);
        f.Entry("sounds/official.mp3", Encoding.UTF8.GetBytes("new official sound bytes"));
        var session = f.Prepare();
        var path = f.Local("sounds/official.mp3");
        var originalAttributes = File.GetAttributes(path);
        var operations = new RealFileOperations(session);
        try
        {
            File.SetAttributes(path, originalAttributes | FileAttributes.ReadOnly);
            var result = Run(session, operations);
            Require(!result.Succeeded && result.RolledBack && !result.NeedsRecovery && operations.Starts == 0,
                "只读资源替换失败后应恢复先替换的主EXE");
            Require(operations.LastWriteFailure is IOException or UnauthorizedAccessException, "必须观察到真实只读文件错误");
            Require(File.ReadAllBytes(f.Target).SequenceEqual(OldMain) && File.ReadAllBytes(path).SequenceEqual(oldSound),
                "只读资源原件和主EXE必须保持或恢复");
            Require((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, "更新器不能为了覆盖而移除用户文件只读属性");
            Require(operations.Writes.Count(item => item == "replace:拾刻.exe") == 2, "主EXE须实际经历前进替换与恢复");
            f.AssertPersonalFiles();
            f.Report(new { failure = operations.LastWriteFailure!.GetType().Name, result.RolledBack,
                writes = operations.Writes, readonlyPreserved = true, protectedFiles = f.ProtectedCount });
        }
        finally { File.SetAttributes(path, originalAttributes); }
    });

    public static void ReadOnlyMainFileIsPreserved() => WithFixture("readonly-main", f =>
    {
        var oldLibrary = Encoding.UTF8.GetBytes("old library before readonly main");
        f.Existing("Library.dll", oldLibrary);
        f.Entry("Library.dll", Encoding.UTF8.GetBytes("new library before readonly main"));
        f.Entry("拾刻.exe", NewMain);
        var session = f.Prepare();
        var originalAttributes = File.GetAttributes(f.Target);
        var operations = new RealFileOperations(session);
        try
        {
            File.SetAttributes(f.Target, originalAttributes | FileAttributes.ReadOnly);
            var result = Run(session, operations);
            Require(!result.Succeeded && result.RolledBack && !result.NeedsRecovery && operations.Starts == 0,
                "真实只读主EXE必须阻止替换并恢复之前的DLL");
            Require(operations.LastWriteFailure is IOException or UnauthorizedAccessException, "须观察到Windows只读主EXE写入错误");
            Require(File.ReadAllBytes(f.Target).SequenceEqual(OldMain) && File.ReadAllBytes(f.Local("Library.dll")).SequenceEqual(oldLibrary),
                "只读主程序应保持，已替换的DLL应恢复");
            Require((File.GetAttributes(f.Target) & FileAttributes.ReadOnly) != 0, "更新器不能移除主EXE只读属性强行覆盖");
            Require(operations.Writes.Count(item => item == "replace:Library.dll") == 2, "DLL须实际经历前进替换与恢复");
            f.AssertPersonalFiles();
            f.Report(new { failure = operations.LastWriteFailure!.GetType().Name, result.RolledBack,
                writes = operations.Writes, mainReadonlyPreserved = true, protectedFiles = f.ProtectedCount });
        }
        finally { File.SetAttributes(f.Target, originalAttributes); }
    });

    public static void LargePackageAndSixtyFourFilesCompleteTransaction() => WithFixture("large-64-files", f =>
    {
        var main = RandomNumberGenerator.GetBytes(16 * 1024 * 1024);
        f.Entry("拾刻.exe", main);
        for (var index = 0; index < 60; index++)
            f.Entry($"Library{index:D2}.dll", RandomNumberGenerator.GetBytes(64 * 1024), allowCreate: true);
        for (var index = 0; index < 3; index++)
            f.Entry($"sounds/extra{index:D2}.mp3", RandomNumberGenerator.GetBytes(64 * 1024), allowCreate: true);
        var session = f.Prepare();
        Require(session.Manifest.Files.Count == 64 && f.PackageBytes >= 16 * 1024 * 1024,
            "本案必须实际处理64个条目以及至少16MiB压缩包");
        Require(f.PackageBytes > f.UnpackedBytes * 0.95, "随机样本不能被高度压缩来虚报大包测试");
        var operations = new RealFileOperations(session);
        var result = Run(session, operations, TimeSpan.FromSeconds(60));
        Require(result.Succeeded && operations.Starts == 1 && operations.Writes.Count == 64,
            "64个真实文件应完整应用且只启动一次受控新进程");
        Require(session.Manifest.Files.All(file => HashFile(session.TargetFor(file.Path)) == file.Sha256),
            "大包事务的全部文件须达到目标哈希");
        f.AssertPersonalFiles();
        f.Report(new { mainBytes = main.LongLength, packageBytes = f.PackageBytes, unpackedBytes = f.UnpackedBytes,
            declaredFiles = session.Manifest.Files.Count, changedFiles = session.Changes.Count,
            installedWrites = operations.Writes.Count, nonCompressibleRandomData = true, protectedFiles = f.ProtectedCount });
    });

    public static void SixtyFifthEntryIsRejectedWithoutInstallationWrites() => WithFixture("65-files-rejected", f =>
    {
        f.Entry("拾刻.exe", NewMain);
        for (var index = 0; index < 64; index++) f.Entry($"Extra{index:D2}.dll", [(byte)index], allowCreate: true);
        var rejected = false;
        try { f.Prepare(); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected && File.ReadAllBytes(f.Target).SequenceEqual(OldMain), "65项清单必须在安装写入前拒绝");
        Require(!Directory.Exists(UpdateSession.UpdateRoot(f.Target)), "超出条目数量时不能创建安装事务");
        f.AssertPersonalFiles();
        f.Report(new { declaredFiles = 65, rejectedBeforeInstallation = true, protectedFiles = f.ProtectedCount });
    });

    private static UpdateRunResult Run(UpdateSession session, RealFileOperations operations, TimeSpan? budget = null)
        => new UpdateRunner(operations, new UpdateRunnerTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2)))
            .RunAsync(session).WaitAsync(budget ?? TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();

    private sealed class ProcessStub : IUpdateProcess
    {
        internal ProcessStub(int id, long ticks, string path) { Id = id; StartTimeUtcTicks = ticks; ExecutablePath = path; }
        public int Id { get; }
        public long StartTimeUtcTicks { get; }
        public string ExecutablePath { get; }
        public bool HasExited { get; set; }
        internal Action? OnWait { get; set; }
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OnWait?.Invoke();
            return Task.FromResult(HasExited);
        }
        public Task<bool> StopOwnedAsync(TimeSpan timeout) { HasExited = true; return Task.FromResult(true); }
        public void Dispose() { }
    }

    private sealed class RealFileOperations : IUpdateOperations
    {
        private readonly UpdateSession _session;
        private readonly ProcessStub _parent;
        private readonly ProcessStub _child;
        internal List<string> Writes { get; } = [];
        internal int Starts { get; private set; }
        internal Exception? LastWriteFailure { get; private set; }
        internal RealFileOperations(UpdateSession session)
        {
            _session = session;
            _parent = new(session.ParentId, session.ParentStartTimeUtcTicks, session.TargetPath);
            _child = new(9567, 200, session.TargetPath);
            _parent.OnWait = () => { session.ApproveExit(); _parent.HasExited = true; };
            _child.OnWait = () => session.MarkHealthy(_child.Id, _child.StartTimeUtcTicks);
        }
        public IUpdateProcess? OpenParent(int processId) => processId == _parent.Id ? _parent : null;
        public IUpdateProcess StartUpdated(UpdateSession session) { Starts++; return _child; }
        public void Replace(string source, string target, string backup)
        {
            Writes.Add("replace:" + Relative(target));
            try { File.Replace(source, target, backup, false); }
            catch (Exception error) { LastWriteFailure = error; throw; }
        }
        public void Add(string source, string target)
        {
            Writes.Add("add:" + Relative(target));
            try { File.Move(source, target, false); }
            catch (Exception error) { LastWriteFailure = error; throw; }
        }
        public void RemoveAdded(string target) { Writes.Add("remove:" + Relative(target)); File.Delete(target); }
        public bool IsTargetRunning(string executablePath) => false;
        private string Relative(string target) => Path.GetRelativePath(_session.InstallationDirectory, target).Replace('\\', '/');
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);
        private readonly Dictionary<string, byte[]> _old = new(StringComparer.Ordinal);
        private readonly List<(string Path, byte[] Bytes, bool AllowCreate)> _entries = [];
        private readonly Dictionary<string, (string Hash, DateTime WriteTime)> _personal = new(StringComparer.Ordinal);
        private const string KeyId = "update-acceptance-test-only";
        internal string DirectoryPath { get; }
        internal string Target => Local("拾刻.exe");
        internal long PackageBytes { get; private set; }
        internal long UnpackedBytes => _entries.Sum(entry => entry.Bytes.LongLength);
        internal int ProtectedCount => _personal.Count;
        internal object? Metrics { get; private set; }
        internal Fixture(string directory)
        {
            DirectoryPath = directory;
            Directory.CreateDirectory(directory);
            Existing("拾刻.exe", OldMain);
            foreach (var path in new[] { "data/settings.json", "data/rules.json", "data/coordinates.json", "data/presets/player.json", "sounds/player-custom.mp3" })
            {
                Write(Local(path), Encoding.UTF8.GetBytes("player-owned sentinel: " + path));
                File.SetLastWriteTimeUtc(Local(path), new DateTime(2003, 2, 1, 4, 5, 6, DateTimeKind.Utc));
                _personal[path] = (HashFile(Local(path)), File.GetLastWriteTimeUtc(Local(path)));
            }
        }
        internal string Local(string path) => Path.Combine(DirectoryPath, path.Replace('/', Path.DirectorySeparatorChar));
        internal void Existing(string path, byte[] bytes)
        {
            _old.Add(path, bytes);
            Write(Local(path), bytes);
            File.SetLastWriteTimeUtc(Local(path), new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        }
        internal void Entry(string path, byte[] bytes, bool allowCreate = false) => _entries.Add((path, bytes, allowCreate));
        internal UpdateSession Prepare()
        {
            byte[] package;
            using (var buffer = new MemoryStream())
            {
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                {
                    foreach (var entry in _entries)
                    {
                        using var output = archive.CreateEntry(entry.Path, CompressionLevel.Fastest).Open();
                        output.Write(entry.Bytes);
                    }
                    using var ignored = archive.CreateEntry("data/settings.json").Open();
                    ignored.Write(Encoding.UTF8.GetBytes("packaged defaults must be ignored"));
                }
                package = buffer.ToArray();
            }
            PackageBytes = package.LongLength;
            var main = _entries.Single(entry => entry.Path == "拾刻.exe");
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1, version = "1.0.53", manualUrl = UpdateManifest.OfficialManualUrl,
                mode = _entries.Count == 1 ? "exe-only" : "files", minUpdaterVersion = 1, minAppVersion = "1.0.52",
                runtimeMajor = 10, platform = "win-x64", configSchema = 1,
                packageUrl = "https://open.cancanneed.top/download/releases/1.0.53/acceptance-fixture.zip",
                packageSize = PackageBytes, packageSha256 = Hash(package), exeSize = main.Bytes.LongLength, exeSha256 = Hash(main.Bytes),
                files = _entries.Select(entry => new
                {
                    path = entry.Path, size = entry.Bytes.LongLength, sha256 = Hash(entry.Bytes),
                    baseSha256 = entry.Path != "拾刻.exe" && _old.TryGetValue(entry.Path, out var original) ? new[] { Hash(original) } : Array.Empty<string>(),
                    allowCreate = entry.AllowCreate,
                }),
            });
            var envelope = JsonSerializer.Serialize(new
            {
                latest = "1.0.53", url = UpdateManifest.OfficialManualUrl,
                autoUpdate = new { keyId = KeyId, payload = Convert.ToBase64String(payload),
                    signature = Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) },
            });
            var manifest = UpdateManifest.Verify(envelope, _key.ExportSubjectPublicKeyInfoPem(), KeyId);
            var session = UpdateSession.CreateCore(manifest, Target, new Version(1, 0, 52), 8765, 100);
            using var downloader = new UpdatePackageDownloader(new LocalPackageHandler(package));
            var candidate = downloader.PrepareAsync(manifest, session.DownloadDirectory, CancellationToken.None).GetAwaiter().GetResult();
            session.PrepareCandidateAsync(candidate, CancellationToken.None).GetAwaiter().GetResult();
            return session;
        }
        internal void AssertPersonalFiles()
        {
            foreach (var (path, before) in _personal)
                Require(HashFile(Local(path)) == before.Hash && File.GetLastWriteTimeUtc(Local(path)) == before.WriteTime,
                    "配置/自定义资源内容及写入时间必须保持：" + path);
        }
        internal void Report(object metrics) => Metrics = metrics;
        public void Dispose() => _key.Dispose();
    }

    private sealed class LocalPackageHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Require(request.RequestUri?.Host == "open.cancanneed.top", "必须仍走已验签的官方地址语义");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private static void WithFixture(string name, Action<Fixture> action)
    {
        var output = OutputRoot();
        var directory = Path.Combine(output, name + "-" + Guid.NewGuid().ToString("N"));
        using var fixture = new Fixture(directory);
        var started = DateTimeOffset.UtcNow;
        var passed = false;
        try
        {
            action(fixture);
            passed = true;
            Reports.Add(new { name, status = "passed", startedUtc = started, finishedUtc = DateTimeOffset.UtcNow, metrics = fixture.Metrics });
        }
        catch (Exception error)
        {
            Reports.Add(new { name, status = "failed", startedUtc = started, error = error.ToString(), retainedDirectory = directory });
            throw;
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                scope = "real temporary Windows files/locks/attributes and real ZIP bytes; simulated processes/HTTP; no UI, network, loaded DLL or audio playback",
                reports = Reports,
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (passed)
            {
                var full = Path.GetFullPath(directory);
                if (!full.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("测试清理越界");
                Directory.Delete(full, recursive: true);
            }
            Console.WriteLine($"INFO 更新补充验收 {name}: {(passed ? "passed" : "failed")}; report={Path.Combine(output, "result.json")}");
        }
    }

    private static string OutputRoot()
    {
        if (_outputRoot is not null) return _outputRoot;
        var source = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "拾刻.csproj")))
        {
            if (File.Exists(Path.Combine(source.FullName, "源码", "拾刻.csproj"))) { source = new DirectoryInfo(Path.Combine(source.FullName, "源码")); break; }
            source = source.Parent;
        }
        if (source?.Parent is null) throw new IOException("未找到受控源码目录");
        var root = Path.Combine(source.Parent.FullName, "bin", "Release", "net10.0-windows");
        _outputRoot = Path.Combine(root, "验证记录", "更新补充验收-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_outputRoot);
        File.WriteAllText(Path.Combine(_outputRoot, ".shike-output.json"), "{\"schemaVersion\":1,\"purpose\":\"更新补充验收\"}");
        return _outputRoot;
    }
    private static void Write(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
