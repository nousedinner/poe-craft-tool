using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ShiKe.Services.Updates;

internal static class UpdateRunnerChecks
{
    public static void BatchSuccessChangesOnlyDeclaredFiles() => WithFixture(f =>
    {
        var session = f.Prepare();
        var operations = new FakeOperations(session);
        var result = Run(session, operations);
        Require(result.Succeeded && !result.RolledBack && operations.Starts == 1, "整批更新应成功且仅启动一次新版");
        f.AssertNewFiles();
        f.AssertPersonalFiles();
        Require(session.HasCommittedProcess(operations.Child.Id, operations.Child.StartTimeUtcTicks),
            "成功必须发布与本次新进程绑定的提交确认");
        Require(!UpdateBootstrap.IsUpdateBusy(f.Target), "更新器结束必须释放跨进程租约");
    });

    public static void ParentExitWithoutApprovalNeverWrites() => WithFixture(f =>
    {
        var session = f.Prepare();
        var operations = new FakeOperations(session) { ApproveParentExit = false };
        var result = Run(session, operations);
        Require(!result.Succeeded && operations.Replacements == 0 && operations.Starts == 0,
            "进程退出不等于保存成功，未许可时不能替换或启动");
        f.AssertOriginalFiles();
        f.AssertPersonalFiles();
    });

    public static void ParentIdentityMustMatchBeforeReady() => WithFixture(f =>
    {
        var session = f.Prepare();
        var operations = new FakeOperations(session);
        operations.Parent.StartTimeUtcTicks++;
        var result = Run(session, operations);
        Require(!result.Succeeded && !session.HasHelperReady && operations.Replacements == 0,
            "PID相同但启动时间不同不能获得更新交接许可");
        f.AssertOriginalFiles();
    });

    public static void PartialBatchFailureRestoresEarlierAndAddedFiles() => WithFixture(f =>
    {
        var session = f.Prepare();
        var operations = new FakeOperations(session) { FailOnceOn = "tone.mp3", MoveOldBeforeFailure = true };
        var result = Run(session, operations);
        Require(!result.Succeeded && result.RolledBack && !result.NeedsRecovery && operations.Starts == 0,
            "部分替换失败必须恢复整批，不能启动混合版本");
        f.AssertOriginalFiles();
        f.AssertPersonalFiles();
        Require(!File.Exists(Path.Combine(f.Directory, "Added.dll")), "回滚必须移除本批新增文件");
    });

    public static void FailedStartupRestoresBatchWithoutOldAutoRestart() => WithFixture(f =>
    {
        var session = f.Prepare();
        var operations = new FakeOperations(session) { ChildExitsImmediately = true };
        var result = Run(session, operations);
        Require(result.RolledBack && !result.Succeeded && operations.Starts == 1,
            "新版未确认启动须回滚，但强制更新失败不能自动重开旧主界面");
        f.AssertOriginalFiles();
        f.AssertPersonalFiles();
        Require(UpdateBootstrap.IsFailedRelease(f.Target, session.Manifest), "失败版本必须被记录以防无限自动重试");
    });

    public static void LiveUnconfirmedProcessPreventsUnsafeRollback() => WithFixture(f =>
    {
        var session = f.Prepare();
        var operations = new FakeOperations(session) { SendHealthy = false, StopChildSucceeds = false };
        var result = Run(session, operations, TimeSpan.FromMilliseconds(5));
        Require(!result.Succeeded && result.NeedsRecovery && operations.Child.StopCalled,
            "无法停止未就绪的新进程时必须保留备份，不得覆盖其运行中的文件");
        Require(File.Exists(session.BackupPath) && session.ReadPhase() == "recovery-required", "恢复材料必须保留");
        f.AssertPersonalFiles();
    });

    public static void UserResourceConflictRejectsWholeUpdate() => WithFixture(f =>
    {
        File.WriteAllText(Path.Combine(f.Directory, "sounds", "tone.mp3"), "用户修改的声音");
        Throws<InvalidDataException>(() => f.Prepare(), "用户修改的资源不得被默认旧哈希快照放行");
        Require(File.ReadAllBytes(f.Target).SequenceEqual(f.OldMain), "冲突不应先覆盖主EXE");
        Require(File.ReadAllText(Path.Combine(f.Directory, "sounds", "tone.mp3")) == "用户修改的声音", "原资源必须保留");
        f.AssertPersonalFiles();
    });

    public static void MidBatchResourceEditIsPreserved() => WithFixture(f =>
    {
        var session = f.Prepare();
        var changed = false;
        var operations = new FakeOperations(session)
        {
            AfterReplace = target =>
            {
                if (changed || !UpdateSession.SamePath(target, f.Target)) return;
                changed = true;
                File.WriteAllText(Path.Combine(f.Directory, "Library.dll"), "更新中用户改动");
            },
        };
        var result = Run(session, operations);
        Require(result.RolledBack && !result.Succeeded, "批次中途冲突应恢复之前已替换的文件");
        Require(File.ReadAllBytes(f.Target).SequenceEqual(f.OldMain), "主EXE必须回滚");
        Require(File.ReadAllText(Path.Combine(f.Directory, "Library.dll")) == "更新中用户改动",
            "尚未写入的冲突文件应保持用户内容");
        f.AssertPersonalFiles();
    });

    public static void HealthyProgramWaitsForCommitPermission() => WithFixture(f =>
    {
        var session = f.Prepare();
        session.WritePhase("awaiting-health");
        using var lease = UpdateBootstrap.AcquireLease(f.Target);
        var task = UpdateStartup.WaitForCommitAsync(session, 2345, 200, TimeSpan.FromSeconds(2));
        Require(!task.IsCompleted, "仅有启动健康请求时不能放行输入");
        session.MarkCommitted(2345, 201); // 错误进程启动时间不能放行。
        Thread.Sleep(100); // 允许真实等待分支至少读到一次错误确认，不只检查任务的初始状态。
        Require(!task.IsCompleted, "错误进程的许可不能放行当前新版");
        session.MarkCommitted(2345, 200);
        task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Require(task.IsCompletedSuccessfully, "正确提交许可到达后才能恢复正常运行");
    });

    public static void DowngradeFloorCannotBeOverriddenByRetry() => WithFixture(f =>
    {
        var lower = f.Prepare("1.0.54");
        var higher = f.Prepare("1.0.55");
        UpdateBootstrap.RecordAccepted(higher);
        Require(!UpdateBootstrap.IsFailedRelease(f.Target, lower.Manifest), "版本地板不能混入可重试失败记录");
        Throws<InvalidDataException>(() => UpdateBootstrap.EnsureReleaseAllowed(f.Target, lower.Manifest), "必须拒绝低于已接受版本");
        Throws<InvalidDataException>(() => f.Prepare("1.0.54"), "会话创建同样不能绕过版本地板");
        var operations = new FakeOperations(lower);
        var result = Run(lower, operations);
        Require(!result.Succeeded && operations.Replacements == 0, "已准备的旧清单也必须在助手替换前重新检查地板");
        f.AssertOriginalFiles();
    });

    public static void SessionTamperingCannotChangeTargetOrManifest() => WithFixture(f =>
    {
        var session = f.Prepare();
        Throws<InvalidDataException>(() => UpdateSession.Load(session.SessionPath, new string('0', 64), f.PublicKey, Fixture.KeyId),
            "错误会话token不能用于更新");
        var json = JsonNode.Parse(File.ReadAllText(session.SessionPath))!.AsObject();
        json["TargetPath"] = Path.Combine(f.Directory, "data", "settings.exe");
        File.WriteAllText(session.SessionPath, json.ToJsonString());
        Throws<InvalidDataException>(() => UpdateSession.Load(session.SessionPath, session.Token, f.PublicKey, Fixture.KeyId),
            "会话目录绑定应拒绝改写到data内的不同目标");
        f.AssertOriginalFiles();
        f.AssertPersonalFiles();
    });

    public static void SameAcceptedVersionCannotChangePackage() => WithFixture(f =>
    {
        var altered = f.Prepare("1.0.55", new string('B', 64));
        var original = f.Prepare("1.0.55");
        UpdateBootstrap.RecordAccepted(original);
        Throws<InvalidDataException>(() => UpdateBootstrap.EnsureReleaseAllowed(f.Target, altered.Manifest),
            "同一个已接受版本不能更换包哈希");
        var operations = new FakeOperations(altered);
        var result = Run(altered, operations);
        Require(!result.Succeeded && operations.Replacements == 0, "同版变包不能通过助手阶段");
        f.AssertOriginalFiles();
    });

    public static void AcceptedRecordFailureCannotEnableNewProgram() => WithFixture(f =>
    {
        var session = f.Prepare();
        var operations = new FakeOperations(session)
        {
            BeforeHealthy = () => Directory.CreateDirectory(Path.Combine(UpdateSession.UpdateRoot(f.Target),
                "release.json")),
        };
        var result = Run(session, operations);
        Require(!result.Succeeded && result.RolledBack && !session.HasCommitAcknowledgement,
            "接受记录未成功持久化不能发布输入许可，应回滚仍未就绪新版");
        f.AssertOriginalFiles();
        f.AssertPersonalFiles();
    });

    public static void InterruptedBatchBlocksStartupAndCanRecoverOffline() => WithFixture(f =>
    {
        var session = f.Prepare();
        Directory.CreateDirectory(Path.GetDirectoryName(session.BackupPath)!);
        File.Replace(session.CandidatePath, f.Target, session.BackupPath);
        File.Move(session.CandidateFor("Added.dll"), session.TargetFor("Added.dll"));
        session.WritePhase("applying");
        Throws<InvalidDataException>(() => UpdateBootstrap.EnsureNoIncompleteTransaction(f.Target, f.PublicKey, Fixture.KeyId),
            "中断事务不能作为普通程序继续启动混合文件");
        var operations = new FakeOperations(session);
        var result = new UpdateRunner(operations).RecoverAsync(session).GetAwaiter().GetResult();
        Require(result.RolledBack && !result.NeedsRecovery && operations.Starts == 0,
            "明确离线恢复应还原整批且不能自动启动旧界面");
        f.AssertOriginalFiles();
        f.AssertPersonalFiles();
        UpdateBootstrap.EnsureNoIncompleteTransaction(f.Target, f.PublicKey, Fixture.KeyId);
    });

    public static void RecoveryRefusesLiveOrUnrelatedTarget() => WithFixture(f =>
    {
        var session = f.Prepare();
        Directory.CreateDirectory(Path.GetDirectoryName(session.BackupPath)!);
        File.Replace(session.CandidatePath, f.Target, session.BackupPath);
        session.WritePhase("applying");
        var running = new FakeOperations(session) { TargetRunning = true };
        var liveResult = new UpdateRunner(running).RecoverAsync(session).GetAwaiter().GetResult();
        Require(liveResult.NeedsRecovery && session.ReadPhase() == "applying",
            "主程序仍运行时恢复入口必须拒绝且不能改写事务状态");
        File.WriteAllText(f.Target, "外部放入的另一个程序");
        var result = new UpdateRunner(new FakeOperations(session)).RecoverAsync(session).GetAwaiter().GetResult();
        Require(result.NeedsRecovery && File.ReadAllText(f.Target) == "外部放入的另一个程序",
            "恢复前必须核对整批身份，不能覆盖后放入的未知程序");
        f.AssertPersonalFiles();
    });

    public static void MainRenameKeepsFailureAndVersionFloor() => WithFixture(f =>
    {
        var lower = f.Prepare("1.0.54");
        var accepted = f.Prepare("1.0.55");
        var altered = f.Prepare("1.0.55", new string('B', 64));
        var failed = f.Prepare("1.0.56");
        UpdateBootstrap.RecordAccepted(accepted);
        UpdateBootstrap.RecordFailure(failed, "受控更新失败");
        var renamed = Path.Combine(f.Directory, "拾刻-自用改名.exe");
        File.Move(f.Target, renamed);
        Require(UpdateBootstrap.IsFailedRelease(renamed, failed.Manifest), "主EXE改名后仍须识别同版同包的失败记录");
        Throws<InvalidDataException>(() => UpdateBootstrap.EnsureReleaseAllowed(renamed, lower.Manifest),
            "主EXE改名不能重置已接受版本地板");
        Throws<InvalidDataException>(() => UpdateBootstrap.EnsureReleaseAllowed(renamed, altered.Manifest),
            "主EXE改名不能绕过相同已接受版本的包哈希约束");
        UpdateBootstrap.EnsureReleaseAllowed(renamed, accepted.Manifest);
        Require(File.Exists(Path.Combine(UpdateSession.UpdateRoot(renamed), "release.json")),
            "发行记录必须留在安装目录的固定状态文件中");
        f.AssertPersonalFiles();
    });

    public static void SeparateInstallationsKeepIndependentReleaseState() => WithFixture(f =>
    {
        var lower = f.Prepare("1.0.54");
        var accepted = f.Prepare("1.0.55");
        var failed = f.Prepare("1.0.56");
        UpdateBootstrap.RecordAccepted(accepted);
        UpdateBootstrap.RecordFailure(failed, "受控更新失败");
        var otherDirectory = Path.Combine(f.Directory, "另一份独立安装");
        Directory.CreateDirectory(otherDirectory);
        var otherExecutable = Path.Combine(otherDirectory, "拾刻.exe");
        File.WriteAllBytes(otherExecutable, f.OldMain);
        UpdateBootstrap.EnsureReleaseAllowed(otherExecutable, lower.Manifest);
        Require(!UpdateBootstrap.IsFailedRelease(otherExecutable, failed.Manifest),
            "不同安装目录不能共享失败记录或版本地板");
        Require(!Directory.Exists(UpdateSession.UpdateRoot(otherExecutable)),
            "只读取独立安装状态时不应额外创建更新目录");
        Require(UpdateBootstrap.IsFailedRelease(f.Target, failed.Manifest), "检查其他安装不能清除原安装失败记录");
    });

    private static UpdateRunResult Run(UpdateSession session, FakeOperations operations, TimeSpan? startupTimeout = null)
        => new UpdateRunner(operations, new UpdateRunnerTimeouts(TimeSpan.FromSeconds(1),
                startupTimeout ?? TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20)))
            .RunAsync(session).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

    private sealed class FakeProcess : IUpdateProcess
    {
        public int Id { get; init; }
        public long StartTimeUtcTicks { get; set; }
        public required string ExecutablePath { get; init; }
        public bool HasExited { get; set; }
        public bool StopCalled { get; private set; }
        internal bool StopSucceeds { get; set; } = true;
        internal Action? OnWait { get; set; }
        public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            OnWait?.Invoke();
            if (!HasExited) await Task.Delay(1, token);
            return HasExited;
        }
        public Task<bool> StopOwnedAsync(TimeSpan timeout)
        {
            StopCalled = true;
            HasExited = StopSucceeds;
            return Task.FromResult(StopSucceeds);
        }
        public void Dispose() { }
    }

    private sealed class FakeOperations : IUpdateOperations
    {
        internal FakeOperations(UpdateSession session)
        {
            Parent = new FakeProcess { Id = session.ParentId, StartTimeUtcTicks = session.ParentStartTimeUtcTicks,
                ExecutablePath = session.TargetPath };
            Child = new FakeProcess { Id = 2345, StartTimeUtcTicks = 200, ExecutablePath = session.TargetPath };
            Parent.OnWait = () =>
            {
                if (ApproveParentExit) session.ApproveExit();
                Parent.HasExited = true;
            };
            Child.OnWait = () =>
            {
                if (SendHealthy) { BeforeHealthy?.Invoke(); session.MarkHealthy(Child.Id, Child.StartTimeUtcTicks); }
            };
        }
        internal FakeProcess Parent { get; }
        internal FakeProcess Child { get; }
        internal bool ApproveParentExit { get; init; } = true;
        internal bool SendHealthy { get; init; } = true;
        internal bool ChildExitsImmediately { get; init; }
        internal bool StopChildSucceeds { get; init; } = true;
        internal bool TargetRunning { get; init; }
        internal Action? BeforeHealthy { get; init; }
        internal string? FailOnceOn { get; set; }
        internal bool MoveOldBeforeFailure { get; init; }
        internal Action<string>? AfterReplace { get; init; }
        internal int Replacements { get; private set; }
        internal int Starts { get; private set; }
        public IUpdateProcess? OpenParent(int processId) => processId == Parent.Id ? Parent : null;
        public IUpdateProcess StartUpdated(UpdateSession session)
        {
            Starts++;
            Child.HasExited = ChildExitsImmediately;
            Child.StopSucceeds = StopChildSucceeds;
            return Child;
        }
        public void Replace(string source, string target, string backup)
        {
            Replacements++;
            if (Path.GetFileName(target) == FailOnceOn)
            {
                FailOnceOn = null;
                if (MoveOldBeforeFailure) File.Move(target, backup);
                throw new IOException("受控的文件替换失败");
            }
            File.Replace(source, target, backup, false);
            AfterReplace?.Invoke(target);
        }
        public void Add(string source, string target) => File.Move(source, target);
        public void RemoveAdded(string target) => File.Delete(target);
        public bool IsTargetRunning(string executablePath) => TargetRunning;
    }

    private sealed class Fixture : IDisposable
    {
        internal const string KeyId = "runner-temporary-key";
        private readonly RSA _key = RSA.Create(2048);
        internal string Directory { get; }
        internal string Target => Path.Combine(Directory, "拾刻.exe");
        internal string PublicKey => _key.ExportSubjectPublicKeyInfoPem();
        internal byte[] OldMain { get; } = Encoding.UTF8.GetBytes("old-program");
        private readonly Dictionary<string, byte[]> _old = new()
        {
            ["Library.dll"] = Encoding.UTF8.GetBytes("old-library"),
            ["sounds/tone.mp3"] = Encoding.UTF8.GetBytes("old-tone"),
        };
        private readonly Dictionary<string, byte[]> _new = new()
        {
            ["拾刻.exe"] = Encoding.UTF8.GetBytes("new-program"),
            ["Library.dll"] = Encoding.UTF8.GetBytes("new-library"),
            ["Added.dll"] = Encoding.UTF8.GetBytes("new-added-library"),
            ["sounds/tone.mp3"] = Encoding.UTF8.GetBytes("new-tone"),
        };
        internal Fixture(string directory)
        {
            Directory = directory;
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllBytes(Target, OldMain);
            foreach (var (path, bytes) in _old) Write(path, bytes);
            Write("data/settings.json", Encoding.UTF8.GetBytes("用户配置保留"));
            Write("sounds/custom.mp3", Encoding.UTF8.GetBytes("用户音效保留"));
        }
        internal UpdateSession Prepare(string version = "1.0.53", string? packageHash = null)
        {
            var payload = new JsonObject
            {
                ["schema"] = 1, ["version"] = version, ["manualUrl"] = UpdateManifest.OfficialManualUrl,
                ["mode"] = "files", ["minUpdaterVersion"] = 1, ["minAppVersion"] = "1.0.52",
                ["runtimeMajor"] = 10, ["platform"] = "win-x64", ["configSchema"] = 1,
                ["packageUrl"] = $"https://open.cancanneed.top/download/releases/{version}/shike-win-x64.zip",
                ["packageSize"] = 10, ["packageSha256"] = packageHash ?? new string('A', 64),
                ["exeSize"] = _new["拾刻.exe"].Length, ["exeSha256"] = Hash(_new["拾刻.exe"]),
                ["files"] = new JsonArray(_new.Select(pair => (JsonNode)new JsonObject
                {
                    ["path"] = pair.Key, ["size"] = pair.Value.Length, ["sha256"] = Hash(pair.Value),
                    ["baseSha256"] = _old.TryGetValue(pair.Key, out var previous) ? new JsonArray(Hash(previous)) : new JsonArray(),
                    ["allowCreate"] = pair.Key == "Added.dll",
                }).ToArray()),
            };
            var data = Encoding.UTF8.GetBytes(payload.ToJsonString());
            var envelope = new JsonObject
            {
                ["latest"] = version, ["url"] = UpdateManifest.OfficialManualUrl,
                ["autoUpdate"] = new JsonObject
                {
                    ["keyId"] = KeyId, ["payload"] = Convert.ToBase64String(data),
                    ["signature"] = Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)),
                },
            }.ToJsonString();
            var manifest = UpdateManifest.Verify(envelope, PublicKey, KeyId);
            var session = UpdateSession.CreateCore(manifest, Target, new Version(1, 0, 52), 1234, 100);
            foreach (var (path, bytes) in _new)
            {
                var destination = Path.Combine(session.DownloadDirectory, "candidates", path.Replace('/', Path.DirectorySeparatorChar));
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
            }
            session.PrepareCandidateAsync(Path.Combine(session.DownloadDirectory, "candidates", "拾刻.exe"), CancellationToken.None)
                .GetAwaiter().GetResult();
            return session;
        }
        internal void AssertNewFiles()
        {
            foreach (var (path, bytes) in _new)
                Require(File.ReadAllBytes(Path.Combine(Directory, path.Replace('/', Path.DirectorySeparatorChar))).SequenceEqual(bytes),
                    "声明文件未更新：" + path);
        }
        internal void AssertOriginalFiles()
        {
            Require(File.ReadAllBytes(Target).SequenceEqual(OldMain), "主EXE原件未恢复");
            foreach (var (path, bytes) in _old)
                Require(File.ReadAllBytes(Path.Combine(Directory, path.Replace('/', Path.DirectorySeparatorChar))).SequenceEqual(bytes),
                    "原资源未恢复：" + path);
            Require(!File.Exists(Path.Combine(Directory, "Added.dll")), "本批新增DLL应不存在");
        }
        internal void AssertPersonalFiles()
        {
            Require(File.ReadAllText(Path.Combine(Directory, "data/settings.json")) == "用户配置保留", "更新不能修改个人配置");
            Require(File.ReadAllText(Path.Combine(Directory, "sounds/custom.mp3")) == "用户音效保留", "更新不能修改清单外用户资源");
        }
        private void Write(string path, byte[] bytes)
        {
            var destination = Path.Combine(Directory, path.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
        }
        public void Dispose() => _key.Dispose();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void WithFixture(Action<Fixture> action)
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = Path.Combine(root, "update-runner-tests-" + Guid.NewGuid().ToString("N"));
        try { using var fixture = new Fixture(directory); action(fixture); }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理目录越界");
            if (System.IO.Directory.Exists(full)) System.IO.Directory.Delete(full, recursive: true);
        }
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
