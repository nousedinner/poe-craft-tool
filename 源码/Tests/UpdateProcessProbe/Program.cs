using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShiKe.Services;
using ShiKe.Services.Updates;

Console.OutputEncoding = new UTF8Encoding(false);
var role = args.FirstOrDefault() ?? "none";
var caseRoot = ProbeFiles.FindCaseRoot(Environment.ProcessPath!);
ProbeFiles.Observe(caseRoot, role, null, null);
var exitCode = 65;
try
{
    if (UpdateBootstrap.TryRunHelper(args, out var helperCode)) exitCode = helperCode;
    else if (role == "--update-start")
    {
        var startup = UpdateBootstrap.ReadStartup(args) ?? throw new InvalidDataException("缺少更新启动上下文");
        var mode = ProbeFiles.ReadMode(caseRoot!);
        if (mode == "startup-failure") exitCode = 101;
        else
        {
            await startup.MarkHealthyAsync();
            File.WriteAllText(Path.Combine(caseRoot!, "child-confirmed.json"), JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, version = NetworkService.CurrentVersion.ToString(3),
                committed = startup.Session.HasCommitAcknowledgement,
            }));
            await Task.Delay(200);
            exitCode = 0;
        }
    }
    else if (role == "init-test-key" && args.Length == 2)
    {
        var root = ProbeFiles.ValidateArtifactDirectory(args[1]);
        using var key = RSA.Create(3072);
        File.WriteAllBytes(Path.Combine(root, "test-private.pkcs8"), key.ExportPkcs8PrivateKey());
        var source = "namespace ShiKe.Services.Updates;\npublic static class ReleaseTrust\n{\n" +
            "    public const string KeyId = \"update-process-probe\";\n" +
            "    public const string PublicKeyPem = \"\"\"\n" + key.ExportSubjectPublicKeyInfoPem() + "\n\"\"\";\n}\n";
        File.WriteAllText(Path.Combine(root, "TestTrust.g.cs"), source, new UTF8Encoding(false));
        exitCode = 0;
    }
    else if (role == "--prepare-update" && args.Length == 2)
    {
        if (caseRoot is null || !UpdateSession.SamePath(caseRoot, args[1])) throw new InvalidDataException("进程验证范围不符");
        var manifest = UpdateManifest.Verify(File.ReadAllText(Path.Combine(caseRoot, "version.json")), ReleaseTrust.PublicKeyPem, ReleaseTrust.KeyId);
        var session = UpdateSession.Create(manifest, Environment.ProcessPath!);
        using var download = new UpdatePackageDownloader(new LocalPackageHandler(File.ReadAllBytes(Path.Combine(caseRoot, "package.zip")), manifest.PackageUri));
        var candidate = await download.PrepareAsync(manifest, session.DownloadDirectory, CancellationToken.None);
        await session.PrepareCandidateAsync(candidate, CancellationToken.None);
        await UpdateBootstrap.StartHelperAsync(session, CancellationToken.None);
        File.WriteAllText(Path.Combine(caseRoot, "parent-receipt.json"), JsonSerializer.Serialize(new
        {
            sessionPath = session.SessionPath, token = session.Token, processId = Environment.ProcessId,
            version = NetworkService.CurrentVersion.ToString(3), permitted = ProbeFiles.ReadMode(caseRoot) != "no-permit",
        }));
        if (ProbeFiles.ReadMode(caseRoot) != "no-permit") session.ApproveExit();
        exitCode = 0; // 真正结束进程，让真实helper等待到原PID退出。
    }
    else if (role == "suite" && args.Length == 5)
    {
        await ProcessSuite.RunAsync(args[1], args[2], args[3], args[4]);
        exitCode = 0;
    }
    else throw new ArgumentException("未知进程验证命令");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    ProbeFiles.Observe(caseRoot, role, 65, error.ToString());
    return 65;
}
ProbeFiles.Observe(caseRoot, role, exitCode, null);
return exitCode;

internal sealed class LocalPackageHandler(byte[] bytes, Uri expected) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri != expected) throw new InvalidDataException("fixture拒绝未知请求");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}

internal static class ProbeFiles
{
    internal static string ValidateArtifactDirectory(string directory)
    {
        var path = Path.GetFullPath(directory);
        if (!path.Contains(Path.DirectorySeparatorChar + "net10.0-windows" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(path, ".shike-output.json"))) throw new InvalidDataException("验证只允许有标记的固定生成目录");
        return path;
    }
    internal static string? FindCaseRoot(string executable)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "probe-case.json"))) return directory.FullName;
        return null;
    }
    internal static string ReadMode(string directory)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "probe-case.json")));
        if (doc.RootElement.GetProperty("owner").GetString() != "ShiKe.UpdateProcessProbe.v1") throw new InvalidDataException("验证标记不符");
        return doc.RootElement.GetProperty("mode").GetString()!;
    }
    internal static void Observe(string? root, string role, int? exitCode, string? error)
    {
        if (root is null) return;
        _ = ReadMode(root);
        var folder = Path.Combine(root, "observations");
        Directory.CreateDirectory(folder);
        using var process = Process.GetCurrentProcess();
        var path = Path.Combine(folder, process.Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            processId = process.Id, startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            executablePath = Environment.ProcessPath, role, version = NetworkService.CurrentVersion.ToString(3), exitCode, error,
        }));
    }
    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
