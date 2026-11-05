using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShiKe.Services.Updates;

internal static class ProcessSuite
{
    private static readonly string[] ProtectedFiles = ["data/settings.json", "data/rules.json", "data/coordinates.json", "data/presets/player.json", "sounds/player-custom.mp3"];

    internal static async Task RunAsync(string oldExecutable, string newExecutable, string privateKeyPath, string output)
    {
        output = ProbeFiles.ValidateArtifactDirectory(output);
        oldExecutable = Path.GetFullPath(oldExecutable);
        newExecutable = Path.GetFullPath(newExecutable);
        privateKeyPath = Path.GetFullPath(privateKeyPath);
        Require(privateKeyPath == Path.Combine(output, "test-private.pkcs8"), "仅允许本轮临时测试私钥");
        Require(FileVersionInfo.GetVersionInfo(oldExecutable).FileVersion == "1.0.52.0" &&
                FileVersionInfo.GetVersionInfo(newExecutable).FileVersion == "1.0.53.0", "fixture版本不符");
        using var key = RSA.Create();
        var privateBytes = File.ReadAllBytes(privateKeyPath);
        try { key.ImportPkcs8PrivateKey(privateBytes, out _); }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
        var reports = new List<object>();
        try
        {
            foreach (var mode in new[] { "success", "startup-failure", "no-permit" })
                reports.Add(await RunCaseAsync(mode, output, oldExecutable, newExecutable, key));
            var result = new { status = "passed", cases = reports, total = reports.Count, passed = reports.Count,
                productionSourcesLinked = new[] { "UpdateManifest", "UpdatePackageDownloader", "UpdateSession", "UpdateRunner", "UpdateBootstrap" },
                scope = "real child processes + real Windows file replacement + signed manifest; headless fixtures only; no WPF/hotkeys/game inputs/network",
                productionSigningKeyUsed = false };
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PROCESS_UPDATE_RESULT total=3, passed=3, failed=0");
        }
        finally { File.Delete(privateKeyPath); }
    }

    private static async Task<object> RunCaseAsync(string mode, string output, string oldExecutable, string newExecutable, RSA key)
    {
        var root = Path.Combine(output, "case-" + mode);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "probe-case.json"), JsonSerializer.Serialize(new { owner = "ShiKe.UpdateProcessProbe.v1", mode }));
        var install = Path.Combine(root, "installation with 中文 空格");
        Directory.CreateDirectory(install);
        var target = Path.Combine(install, "ProbeClient.exe");
        File.Copy(oldExecutable, target);
        Write(install, "Support.dll", "old official DLL");
        Write(install, "sounds/default_ding.mp3", "old official resource");
        foreach (var path in ProtectedFiles) Write(install, path, "player sentinel: " + path);
        var protectedBefore = ProtectedFiles.ToDictionary(path => path, path => ProbeFiles.Hash(Local(install, path)));
        var oldHashes = new[] { "拾刻.exe", "Support.dll", "sounds/default_ding.mp3" }.ToDictionary(path => path,
            path => ProbeFiles.Hash(path == "拾刻.exe" ? target : Local(install, path)));
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["拾刻.exe"] = File.ReadAllBytes(newExecutable), ["Support.dll"] = Encoding.UTF8.GetBytes("new official DLL"),
            ["sounds/default_ding.mp3"] = Encoding.UTF8.GetBytes("new official resource"),
            ["Added.dll"] = Encoding.UTF8.GetBytes("new optional DLL"),
        };
        var packagePath = Path.Combine(root, "package.zip");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in contents) AddEntry(archive, name, bytes);
            // 完整包的未列入更新范围内容即使不同，也不应落入玩家目录。
            AddEntry(archive, "data/settings.json", Encoding.UTF8.GetBytes("unselected package defaults"));
            AddEntry(archive, "sounds/player-custom.mp3", Encoding.UTF8.GetBytes("unselected package sound"));
        }
        var files = contents.Select(pair => new
        {
            path = pair.Key, size = (long)pair.Value.Length, sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)),
            baseSha256 = pair.Key == "拾刻.exe" || !oldHashes.TryGetValue(pair.Key, out var baseline) ? Array.Empty<string>() : new[] { baseline },
            allowCreate = pair.Key == "Added.dll",
        }).ToArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, version = "1.0.53", manualUrl = UpdateManifest.OfficialManualUrl,
            mode = "files", minUpdaterVersion = 1, minAppVersion = "1.0.52", runtimeMajor = 10,
            platform = "win-x64", configSchema = 1,
            packageUrl = "https://open.cancanneed.top/download/releases/1.0.53/process-fixture.zip",
            packageSize = new FileInfo(packagePath).Length, packageSha256 = ProbeFiles.Hash(packagePath),
            exeSize = (long)contents["拾刻.exe"].Length, exeSha256 = files[0].sha256, files,
        });
        var manifest = JsonSerializer.Serialize(new
        {
            latest = "1.0.53", url = UpdateManifest.OfficialManualUrl,
            autoUpdate = new { keyId = "update-process-probe", payload = Convert.ToBase64String(payload),
                signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) },
        });
        UpdateManifest.Verify(manifest, key.ExportSubjectPublicKeyInfoPem(), "update-process-probe");
        File.WriteAllText(Path.Combine(root, "version.json"), manifest);

        var start = new ProcessStartInfo(target) { WorkingDirectory = install, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--prepare-update");
        start.ArgumentList.Add(root);
        using var parent = Process.Start(start) ?? throw new IOException("不能启动旧版fixture");
        var stderr = parent.StandardError.ReadToEndAsync();
        var stdout = parent.StandardOutput.ReadToEndAsync();
        try
        {
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
            Require(parent.ExitCode == 0, "旧版fixture未完成交接：" + await stderr);
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "parent-receipt.json")));
            var sessionPath = receipt.RootElement.GetProperty("sessionPath").GetString()!;
            var session = UpdateSession.Load(sessionPath, receipt.RootElement.GetProperty("token").GetString()!, key.ExportSubjectPublicKeyInfoPem(), "update-process-probe");
            var expected = mode == "success" ? "completed" : mode == "no-permit" ? "aborted" : "rolled-back";
            var timer = Stopwatch.StartNew();
            while (session.ReadPhase() != expected || !AllObservedProcessesExited(root))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("进程事务未按时结束：" + session.ReadPhase());
                if (session.ReadPhase() == "recovery-required") throw new IOException("自动恢复未完成");
                await Task.Delay(50);
            }
            var observations = ReadObservations(root);
            Require(observations.Count(item => item.Role == "--prepare-update") == 1 &&
                    observations.Count(item => item.Role == "--apply-update") == 1, "未观察到唯一主进程及真实助手");
            Require(observations.Count(item => item.Role == "--update-start") == (mode == "no-permit" ? 0 : 1), "新版本进程次数不符");
            foreach (var observation in observations) Require(observation.ExitCode.HasValue, "进程未记录完整退出");
            Require(!UpdateBootstrap.IsUpdateBusy(target), "助手退出后更新锁仍被持有");
            foreach (var (path, hash) in protectedBefore) Require(ProbeFiles.Hash(Local(install, path)) == hash, "玩家哨兵变化：" + path);
            if (mode == "success")
            {
                foreach (var (path, bytes) in contents)
                    Require(ProbeFiles.Hash(path == "拾刻.exe" ? target : Local(install, path)) == Convert.ToHexString(SHA256.HashData(bytes)), "成功后文件未更新：" + path);
                Require(session.HasCommitAcknowledgement && File.Exists(Path.Combine(root, "child-confirmed.json")), "缺少双向启动确认");
                Require(observations.All(item => item.ExitCode == 0), "成功路径存在非零退出");
            }
            else
            {
                foreach (var (path, hash) in oldHashes) Require(ProbeFiles.Hash(path == "拾刻.exe" ? target : Local(install, path)) == hash, "旧文件没有保持/恢复：" + path);
                Require(!File.Exists(Local(install, "Added.dll")), "失败后新增文件未撤销");
                Require(!session.HasCommitAcknowledgement, "失败路径不应有提交确认");
                Require(observations.Single(item => item.Role == "--apply-update").ExitCode == (mode == "no-permit" ? 2 : 3), "助手失败退出码不符");
            }
            var report = new { mode, status = "passed", phase = session.ReadPhase(), changes = session.Changes.Count,
                protectedFiles = protectedBefore.Count, protectedUnchanged = true, allProcessesExited = true,
                processes = observations, sessionPath, originalExeSha256 = oldHashes["拾刻.exe"], finalExeSha256 = ProbeFiles.Hash(target) };
            File.WriteAllText(Path.Combine(root, "case-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS_PROCESS " + mode);
            return report;
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            foreach (var observed in ReadObservations(root)) StopOnlyOwnedProcess(observed, root);
            _ = await stdout;
        }
    }

    private static void AddEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        stream.Write(bytes);
    }
    private static string Local(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static void Write(string root, string relative, string text)
    {
        var path = Local(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    private static IReadOnlyList<Observation> ReadObservations(string root)
    {
        var folder = Path.Combine(root, "observations");
        if (!Directory.Exists(folder)) return [];
        var results = new List<Observation>();
        foreach (var path in Directory.GetFiles(folder, "*.json"))
        {
            try { results.Add(JsonSerializer.Deserialize<Observation>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!); }
            catch (IOException) { }
            catch (JsonException) { }
        }
        return results;
    }
    private static bool AllObservedProcessesExited(string root)
    {
        var observations = ReadObservations(root);
        return observations.Count >= 2 && observations.All(item => item.ExitCode.HasValue && !IsSameProcessAlive(item));
    }
    private static bool IsSameProcessAlive(Observation observation)
    {
        try
        {
            using var process = Process.GetProcessById(observation.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == observation.StartTimeUtcTicks;
        }
        catch (ArgumentException) { return false; }
    }
    private static void StopOnlyOwnedProcess(Observation observation, string root)
    {
        if (!observation.ExecutablePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("拒绝结束本轮验证目录外进程");
        try
        {
            using var process = Process.GetProcessById(observation.ProcessId);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == observation.StartTimeUtcTicks &&
                UpdateSession.SamePath(process.MainModule?.FileName ?? "", observation.ExecutablePath))
            {
                process.Kill(entireProcessTree: false);
                Require(process.WaitForExit(5000), "本轮自建进程未退出");
            }
        }
        catch (ArgumentException) { }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed record Observation(int ProcessId, long StartTimeUtcTicks, string ExecutablePath, string Role,
        string Version, int? ExitCode, string? Error);
}
