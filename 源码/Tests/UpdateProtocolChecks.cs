using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ShiKe.Services.Updates;

// 清单签名使用内存中的临时测试密钥，HTTP 使用替身；不访问发行服务或运行下载内容。
internal static class UpdateProtocolChecks
{
    private static readonly byte[] ProgramBytes = Encoding.UTF8.GetBytes("受控主程序内容，不是可执行文件");

    public static void ManifestRequiresTrustedSignatureAndLegacyBinding()
    {
        using var fixture = new Fixture();
        var payload = Payload(Archive(("拾刻.exe", ProgramBytes)), ProgramBytes);
        var envelope = fixture.Sign(payload);
        var manifest = fixture.Verify(envelope);
        Require(manifest.VersionText == "1.0.53" && manifest.SourceJson == envelope &&
                manifest.ManualUrl == UpdateManifest.OfficialManualUrl, "签名清单必须保留验证后的发行信息");
        Throws<InvalidDataException>(() => UpdateManifest.Verify(envelope, "", Fixture.KeyId), "空信任根不能退化为仅哈希校验");
        using var other = RSA.Create(2048);
        Throws<InvalidDataException>(() => UpdateManifest.Verify(envelope, other.ExportSubjectPublicKeyInfoPem(), Fixture.KeyId),
            "其他私钥不能签发可信更新");
        Throws<InvalidDataException>(() => UpdateManifest.Verify(envelope, fixture.PublicKey, "unknown-key"), "未知密钥标识必须拒绝");
        var changed = JsonNode.Parse(envelope)!.AsObject();
        changed["latest"] = "1.0.99";
        Throws<InvalidDataException>(() => fixture.Verify(changed.ToJsonString()), "旧版版本号也必须由签名内容绑定");
        changed = JsonNode.Parse(envelope)!.AsObject();
        changed["url"] = "https://open.cancanneed.top/other.zip";
        Throws<InvalidDataException>(() => fixture.Verify(changed.ToJsonString()), "旧版下载入口不能独立被修改");
        changed = JsonNode.Parse(envelope)!.AsObject();
        var tampered = Encoding.UTF8.GetBytes(payload.ToJsonString().Replace("1.0.53", "1.0.54", StringComparison.Ordinal));
        changed["autoUpdate"]!["payload"] = Convert.ToBase64String(tampered);
        Throws<InvalidDataException>(() => fixture.Verify(changed.ToJsonString()), "载荷字节变化必须导致签名失败");
        Throws<InvalidDataException>(() => fixture.Verify(fixture.Sign(payload, RSASignaturePadding.Pkcs1)),
            "不能接受协议以外的签名算法");
        changed = JsonNode.Parse(envelope)!.AsObject();
        changed["autoUpdate"]!["publicKey"] = fixture.PublicKey;
        Throws<InvalidDataException>(() => fixture.Verify(changed.ToJsonString()), "服务器不能夹带新公钥作为信任根");
    }

    public static void ManifestRejectsUnsafeOrMalformedSignedFields()
    {
        using var fixture = new Fixture();
        var zip = Archive(("拾刻.exe", ProgramBytes));
        Action<JsonObject>[] changes =
        [
            p => p["schema"] = 2,
            p => p["version"] = "v1.0.53",
            p => p["version"] = "1.0.053",
            p => p["mode"] = "full",
            p => p["minUpdaterVersion"] = 0,
            p => p["minAppVersion"] = "1.0.99",
            p => p["minAppVersion"] = "1.0.52\n",
            p => p["platform"] = "win-x64\n",
            p => p["packageUrl"] = "http://open.cancanneed.top/download/releases/1.0.53/shike.zip",
            p => p["packageUrl"] = "https://example.invalid/download/releases/1.0.53/shike.zip",
            p => p["packageUrl"] = "https://user@open.cancanneed.top/download/releases/1.0.53/shike.zip",
            p => p["packageUrl"] = "https://open.cancanneed.top:444/download/releases/1.0.53/shike.zip",
            p => p["packageUrl"] = "https://open.cancanneed.top/download/releases/1.0.52/shike.zip",
            p => p["packageUrl"] = "https://open.cancanneed.top/download/releases/1.0.53/shike.zip?v=1",
            p => p["packageUrl"] = "https://open.cancanneed.top/download/releases/1.0.53/shike.zip#fragment",
            p => p["packageSize"] = 0,
            p => p["packageSize"] = UpdateManifest.MaximumArtifactBytes + 1,
            p => p["packageSha256"] = "wrong",
            p => p["files"]![0]!["sha256"] = new string('0', 64),
            p => p["files"]![0]!["allowCreate"] = true,
            p => p["files"]![0]!["path"] = "data/settings.json",
            p => p["files"]![0]!["path"] = "../拾刻.exe",
            p => p["files"]![0]!["path"] = "C:/拾刻.exe",
            p => p["files"]![0]!["path"] = "拾刻.exe:payload",
            p => p["files"]![0]!["baseSha256"] = new JsonArray("invalid"),
            p => p["files"]!.AsArray().Add(p["files"]![0]!.DeepClone()),
            p => p["unknown"] = true,
        ];
        foreach (var change in changes)
        {
            var payload = Payload(zip, ProgramBytes);
            change(payload);
            Throws<InvalidDataException>(() => fixture.Verify(fixture.Sign(payload)), "异常签名字段必须拒绝");
        }
        var raw = Payload(zip, ProgramBytes).ToJsonString().Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal);
        Throws<InvalidDataException>(() => fixture.Verify(fixture.SignBytes(Encoding.UTF8.GetBytes(raw))), "载荷重复字段必须拒绝");
        var signed = fixture.Sign(Payload(zip, ProgramBytes));
        var duplicate = signed.Replace("\"latest\":\"1.0.53\"", "\"latest\":\"1.0.53\",\"latest\":\"1.0.53\"", StringComparison.Ordinal);
        Throws<InvalidDataException>(() => fixture.Verify(duplicate), "信封重复字段必须拒绝");
    }

    public static void ManifestAppliesOnlyForwardCompatibleUpdates()
    {
        using var fixture = new Fixture();
        var zip = Archive(("拾刻.exe", ProgramBytes));
        var payload = Payload(zip, ProgramBytes);
        var manifest = fixture.Verify(fixture.Sign(payload));
        Require(manifest.CanApply(new Version(1, 0, 52)) && manifest.CanApply(new Version(1, 0, 52, 0)), "兼容的新版本应该可升级");
        Require(!manifest.CanApply(new Version(1, 0, 53)) && !manifest.CanApply(new Version(1, 0, 54)) &&
                !manifest.CanApply(new Version(1, 0, 51)), "不能降级、同版重装或越过最低兼容版本");
        foreach (var (key, value) in new (string, JsonNode)[]
        {
            ("minUpdaterVersion", JsonValue.Create(2)!), ("runtimeMajor", JsonValue.Create(11)!),
            ("platform", JsonValue.Create("win-arm64")!), ("configSchema", JsonValue.Create(2)!),
        })
        {
            var changed = Payload(zip, ProgramBytes);
            changed[key] = value;
            Require(!fixture.Verify(fixture.Sign(changed)).CanApply(new Version(1, 0, 52)), "不兼容发行不能自动应用");
        }
        payload["mode"] = "files";
        Require(fixture.Verify(fixture.Sign(payload)).CanApply(new Version(1, 0, 52)), "显式files模式仍应支持兼容自动更新");
    }

    public static void FileManifestProtectsConfigurationAndResourceBaselines()
    {
        using var fixture = new Fixture();
        var sound = Encoding.UTF8.GetBytes("new sound");
        var zip = Archive(("拾刻.exe", ProgramBytes), ("sounds/default_ding.mp3", sound));
        var payload = Payload(zip, ProgramBytes);
        payload["mode"] = "files";
        var soundEntry = FileEntry("sounds/default_ding.mp3", sound, allowCreate: true);
        soundEntry["baseSha256"] = new JsonArray(Hash(Encoding.UTF8.GetBytes("official old sound")));
        payload["files"]!.AsArray().Add(soundEntry);
        var manifest = fixture.Verify(fixture.Sign(payload));
        Require(manifest.Files.Count == 2 && manifest.Files[1].AllowCreate && manifest.Files[1].BaseSha256.Count == 1,
            "资源替换条件必须保留在已签名的不可变清单中");
        foreach (var path in new[] { "data/settings.json", "DATA/rules.json", "settings.json", "sounds/../settings.json",
                     "sounds\\default_ding.mp3", "sounds/CON.wav", "sounds/a.mp3.", "sounds/a.mp3:stream", "tools/helper.exe" })
            Throws<InvalidDataException>(() => UpdateManifest.ValidateTargetPath(path), "不能开放个人配置或路径歧义");
        var duplicate = payload.DeepClone().AsObject();
        var copy = soundEntry.DeepClone().AsObject();
        copy["path"] = "sounds/DEFAULT_DING.mp3";
        duplicate["files"]!.AsArray().Add(copy);
        Throws<InvalidDataException>(() => fixture.Verify(fixture.Sign(duplicate)), "Windows路径大小写碰撞必须拒绝");
    }

    public static void PackageStagesOnlyDeclaredFilesAndPreservesUserData() => WithDirectory(root =>
    {
        using var fixture = new Fixture();
        var oldProgram = Path.Combine(root, "拾刻.exe");
        var data = Path.Combine(root, "data");
        var sounds = Path.Combine(root, "sounds");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(sounds);
        File.WriteAllText(oldProgram, "old executable");
        File.WriteAllText(Path.Combine(data, "settings.json"), "personal settings");
        File.WriteAllText(Path.Combine(sounds, "default_ding.mp3"), "custom sound");
        var zip = Archive(("拾刻.exe", ProgramBytes), ("data/settings.json", Encoding.UTF8.GetBytes("packaged defaults")),
            ("sounds/default_ding.mp3", Encoding.UTF8.GetBytes("packaged sound")));
        var manifest = fixture.Verify(fixture.Sign(Payload(zip, ProgramBytes)));
        using var downloader = Downloader(zip);
        var stage = Path.Combine(root, "stage");
        var candidate = downloader.PrepareAsync(manifest, stage, CancellationToken.None).GetAwaiter().GetResult();
        Require(candidate == Path.Combine(stage, "candidates", "拾刻.exe") && File.ReadAllBytes(candidate).SequenceEqual(ProgramBytes),
            "只准备清单声明的主EXE到已确定的暂存位置");
        Require(Directory.GetFiles(Path.Combine(stage, "candidates"), "*", SearchOption.AllDirectories).Length == 1,
            "EXE-only不能解压包内data或sounds");
        Require(File.ReadAllText(oldProgram) == "old executable" &&
                File.ReadAllText(Path.Combine(data, "settings.json")) == "personal settings" &&
                File.ReadAllText(Path.Combine(sounds, "default_ding.mp3")) == "custom sound", "下载准备不能改安装目录文件");

        var dll = Encoding.UTF8.GetBytes("declared dll");
        var sound = Encoding.UTF8.GetBytes("declared sound");
        zip = Archive(("拾刻.exe", ProgramBytes), ("NewLibrary.dll", dll), ("sounds/default_ding.mp3", sound),
            ("data/settings.json", Encoding.UTF8.GetBytes("ignored defaults")));
        var files = Payload(zip, ProgramBytes);
        files["mode"] = "files";
        files["files"]!.AsArray().Add(FileEntry("NewLibrary.dll", dll, true));
        files["files"]!.AsArray().Add(FileEntry("sounds/default_ding.mp3", sound, true));
        using var multiple = Downloader(zip);
        var multiStage = Path.Combine(root, "multi");
        multiple.PrepareAsync(fixture.Verify(fixture.Sign(files)), multiStage, CancellationToken.None).GetAwaiter().GetResult();
        Require(Directory.GetFiles(Path.Combine(multiStage, "candidates"), "*", SearchOption.AllDirectories).Length == 3 &&
                !Directory.Exists(Path.Combine(multiStage, "candidates", "data")), "files也只能暂存显式声明文件");
        Require(File.ReadAllText(Path.Combine(sounds, "default_ding.mp3")) == "custom sound", "资源冲突应交事务检查，下载阶段不得覆盖用户文件");
    });

    public static void PackageRejectsSizeAndHashMismatch() => WithDirectory(root =>
    {
        using var fixture = new Fixture();
        var zip = Archive(("拾刻.exe", ProgramBytes));
        var manifest = fixture.Verify(fixture.Sign(Payload(zip, ProgramBytes)));
        var changed = zip.ToArray();
        changed[^1] ^= 1;
        using (var downloader = Downloader(changed))
            RejectPrepared(downloader, manifest, Path.Combine(root, "hash"));
        using (var downloader = Downloader(zip[..^1]))
            RejectPrepared(downloader, manifest, Path.Combine(root, "truncated"));
        using (var downloader = Downloader(zip.Concat(new byte[] { 0 }).ToArray()))
            RejectPrepared(downloader, manifest, Path.Combine(root, "oversize"));
        var payload = Payload(zip, ProgramBytes);
        payload["exeSha256"] = new string('0', 64);
        payload["files"]![0]!["sha256"] = new string('0', 64);
        using (var downloader = Downloader(zip))
            RejectPrepared(downloader, fixture.Verify(fixture.Sign(payload)), Path.Combine(root, "exe-hash"));
    });

    public static void PackageRejectsAmbiguousEntriesAndUnsafePaths() => WithDirectory(root =>
    {
        using var fixture = new Fixture();
        byte[][] invalidArchives =
        [
            Archive(("拾刻.exe", ProgramBytes), ("拾刻.exe", ProgramBytes)),
            Archive(("拾刻.exe", ProgramBytes), ("拾刻.EXE", ProgramBytes)),
            Archive(("nested/拾刻.exe", ProgramBytes)),
            Archive(("拾刻.exe", ProgramBytes), ("../outside.txt", ProgramBytes)),
            Archive(("拾刻.exe", ProgramBytes), ("data/settings.json", ProgramBytes), ("DATA/settings.json", ProgramBytes)),
        ];
        for (var index = 0; index < invalidArchives.Length; index++)
        {
            var zip = invalidArchives[index];
            using var downloader = Downloader(zip);
            RejectPrepared(downloader, fixture.Verify(fixture.Sign(Payload(zip, ProgramBytes))), Path.Combine(root, "bad-" + index));
        }
        var folder = ArchiveWithAttributes("拾刻.exe", ProgramBytes, (int)FileAttributes.Directory);
        using (var downloader = Downloader(folder))
            RejectPrepared(downloader, fixture.Verify(fixture.Sign(Payload(folder, ProgramBytes))), Path.Combine(root, "directory"));
        var link = ArchiveWithAttributes("拾刻.exe", ProgramBytes, 0xA000 << 16);
        using (var downloader = Downloader(link))
            RejectPrepared(downloader, fixture.Verify(fixture.Sign(Payload(link, ProgramBytes))), Path.Combine(root, "link"));
    });

    public static void PackageRejectsRedirectsAndHttpErrors() => WithDirectory(root =>
    {
        using var fixture = new Fixture();
        var zip = Archive(("拾刻.exe", ProgramBytes));
        var manifest = fixture.Verify(fixture.Sign(Payload(zip, ProgramBytes)));
        var requests = 0;
        using (var redirect = new UpdatePackageDownloader(new StubHttpMessageHandler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.invalid/replacement.zip");
            return Task.FromResult(response);
        })))
            RejectPrepared(redirect, manifest, Path.Combine(root, "redirect"));
        Require(requests == 1, "下载器不能跟随重定向发送第二个请求");
        using var failure = new UpdatePackageDownloader(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        Throws<HttpRequestException>(() => failure.PrepareAsync(manifest, Path.Combine(root, "http-error"), CancellationToken.None)
            .GetAwaiter().GetResult(), "HTTP失败不能当成有效更新包");
    });

    public static void PackageCancellationAndExistingFilesArePreserved() => WithDirectory(root =>
    {
        using var fixture = new Fixture();
        var zip = Archive(("拾刻.exe", ProgramBytes));
        var manifest = fixture.Verify(fixture.Sign(Payload(zip, ProgramBytes)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var downloader = new UpdatePackageDownloader(new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PausedStream(zip, entered)) })));
        var stage = Path.Combine(root, "cancelled");
        var task = downloader.PrepareAsync(manifest, stage, cancellation.Token);
        entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(), "下载取消必须传播");
        Require(!Directory.EnumerateFileSystemEntries(stage).Any(), "取消必须清理本次部分下载");
        var kept = Path.Combine(root, "keep");
        Directory.CreateDirectory(kept);
        var userFile = Path.Combine(kept, "candidate.exe");
        File.WriteAllText(userFile, "must remain");
        using var valid = Downloader(zip);
        Throws<InvalidDataException>(() => valid.PrepareAsync(manifest, kept, CancellationToken.None).GetAwaiter().GetResult(),
            "非空暂存目录不能被覆盖或清理");
        Require(File.ReadAllText(userFile) == "must remain", "原有文件必须保持");
    });

    private static void RejectPrepared(UpdatePackageDownloader downloader, UpdateManifest manifest, string stage)
    {
        Throws<InvalidDataException>(() => downloader.PrepareAsync(manifest, stage, CancellationToken.None).GetAwaiter().GetResult(),
            "无效更新包必须拒绝");
        Require(!Directory.Exists(stage) || !Directory.EnumerateFileSystemEntries(stage).Any(), "拒绝更新后不得留下可误用的候选");
    }

    private static UpdatePackageDownloader Downloader(byte[] bytes) => new(new StubHttpMessageHandler((request, _) =>
    {
        Require(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "open.cancanneed.top",
            "请求必须使用已验证的官方HTTPS地址");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }));

    private static JsonObject Payload(byte[] zip, byte[] executable) => new()
    {
        ["schema"] = 1, ["version"] = "1.0.53", ["manualUrl"] = UpdateManifest.OfficialManualUrl,
        ["mode"] = "exe-only", ["minUpdaterVersion"] = 1, ["minAppVersion"] = "1.0.52",
        ["runtimeMajor"] = 10, ["platform"] = "win-x64", ["configSchema"] = 1,
        ["packageUrl"] = "https://open.cancanneed.top/download/releases/1.0.53/shike-win-x64.zip",
        ["packageSize"] = zip.LongLength, ["packageSha256"] = Hash(zip),
        ["exeSize"] = executable.LongLength, ["exeSha256"] = Hash(executable),
        ["files"] = new JsonArray(FileEntry("拾刻.exe", executable, false)),
    };

    private static JsonObject FileEntry(string path, byte[] content, bool allowCreate) => new()
    {
        ["path"] = path, ["size"] = content.LongLength, ["sha256"] = Hash(content),
        ["baseSha256"] = new JsonArray(), ["allowCreate"] = allowCreate,
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] Archive(params (string Path, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in files)
                using (var entry = archive.CreateEntry(file.Path).Open()) entry.Write(file.Content);
        return stream.ToArray();
    }

    private static byte[] ArchiveWithAttributes(string path, byte[] content, int attributes)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry(path);
            entry.ExternalAttributes = attributes;
            using var output = entry.Open();
            output.Write(content);
        }
        return stream.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        internal const string KeyId = "temporary-test-key";
        private readonly RSA _key = RSA.Create(2048);
        public string PublicKey => _key.ExportSubjectPublicKeyInfoPem();
        public string Sign(JsonObject payload, RSASignaturePadding? padding = null)
            => SignBytes(Encoding.UTF8.GetBytes(payload.ToJsonString()), padding);
        public string SignBytes(byte[] payload, RSASignaturePadding? padding = null) => new JsonObject
        {
            ["latest"] = "1.0.53", ["url"] = UpdateManifest.OfficialManualUrl,
            ["autoUpdate"] = new JsonObject
            {
                ["keyId"] = KeyId, ["payload"] = Convert.ToBase64String(payload),
                ["signature"] = Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256, padding ?? RSASignaturePadding.Pss)),
            },
        }.ToJsonString();
        public UpdateManifest Verify(string envelope) => UpdateManifest.Verify(envelope, PublicKey, KeyId);
        public void Dispose() => _key.Dispose();
    }

    private sealed class PausedStream(byte[] bytes, TaskCompletionSource entered) : Stream
    {
        private bool _first = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_first)
            {
                _first = false;
                var length = Math.Min(buffer.Length, Math.Max(1, bytes.Length / 2));
                bytes.AsMemory(0, length).CopyTo(buffer);
                return length;
            }
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void WithDirectory(Action<string> action)
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = Path.Combine(root, "update-protocol-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试目录越界");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
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
