using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShiKe.Services.Updates;

namespace ShiKe.ReleaseTools;

internal sealed record ReleaseArtifact(string Version, string ZipPath, string ExePath, long PackageSize,
    string PackageSha256, long ExeSize, string ExeSha256, IReadOnlyList<ReleaseFile> Files);
internal sealed record ReleaseFile(string Path, long Size, string Sha256, string[] BaseSha256, bool AllowCreate);

internal static class ReleasePreparation
{
    internal const string ManualUrl = "https://open.cancanneed.top/download/shike-latest.zip";
    internal static readonly string[] ExpectedEntries =
    [
        "拾刻.exe", "data/presets/武器.json", "sounds/default_ding.mp3",
        "sounds/soundreality-notification-piano-443094.mp3",
        "sounds/universfield-new-notification-032-480570.mp3",
        "sounds/universfield-new-notification-036-485897.mp3",
        "sounds/universfield-new-notification-056-494256.mp3",
    ];

    internal static ReleaseArtifact Inspect(string zipPath, string exePath, string version,
        string mode = "exe-only", string? managedFilesPath = null, string? basePackagesPath = null, string? baseManifestsPath = null)
    {
        ValidateVersion(version);
        zipPath = Path.GetFullPath(zipPath);
        exePath = Path.GetFullPath(exePath);
        var exe = new FileInfo(exePath);
        var zipFile = new FileInfo(zipPath);
        if (zipFile.Length is <= 0 or > 134217728 || exe.Length is <= 0 or > 134217728)
            throw new InvalidDataException("发布文件大小超出允许范围");
        if (FileVersionInfo.GetVersionInfo(exePath).FileVersion != version + ".0")
            throw new InvalidDataException("主 EXE 版本与待发布版本不一致");
        var exeHash = HashFile(exePath);
        var selected = ReadManagedFiles(mode, managedFilesPath);
        using var archive = ZipFile.OpenRead(zipPath);
        CheckArchiveEntries(archive, selected.Keys);
        var executable = archive.GetEntry("拾刻.exe")!;
        if (executable.Length != exe.Length) throw new InvalidDataException("ZIP 内 EXE 大小与发布程序不一致");
        if (HashEntry(executable) != exeHash) throw new InvalidDataException("ZIP 内 EXE 与待发布程序不一致");
        var baselines = ReadBaselines(selected.Keys, basePackagesPath, baseManifestsPath);
        var files = selected.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var entry = archive.GetEntry(pair.Key) ?? throw new InvalidDataException("清单文件不存在于完整 ZIP：" + pair.Key);
            return new ReleaseFile(pair.Key, entry.Length, HashEntry(entry),
                pair.Key == "拾刻.exe" ? [] : baselines[pair.Key].Order(StringComparer.Ordinal).ToArray(), pair.Value);
        }).ToArray();
        return new(version, zipPath, exePath, zipFile.Length, HashFile(zipPath), exe.Length, exeHash, files);
    }

    internal static string CreateManifest(ReleaseArtifact artifact, RSA key, string keyId,
        string mode, string minAppVersion, int minUpdaterVersion, int runtimeMajor, string platform, int configSchema)
    {
        ValidateVersion(minAppVersion);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, version = artifact.Version, manualUrl = ManualUrl, mode,
            minUpdaterVersion, minAppVersion, runtimeMajor, platform, configSchema,
            packageUrl = $"https://open.cancanneed.top/download/releases/{artifact.Version}/shike-win-x64.zip",
            packageSize = artifact.PackageSize, packageSha256 = artifact.PackageSha256,
            exeSize = artifact.ExeSize, exeSha256 = artifact.ExeSha256,
            files = artifact.Files.Select(file => new
            {
                path = file.Path, size = file.Size, sha256 = file.Sha256,
                baseSha256 = file.BaseSha256, allowCreate = file.AllowCreate,
            }),
        });
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var result = JsonSerializer.Serialize(new
        {
            latest = artifact.Version, url = ManualUrl,
            autoUpdate = new { keyId, payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature) },
        }, new JsonSerializerOptions { WriteIndented = true });
        // 使用与客户端完全相同的协议验证，签名或字段漂移不得进入发布目录。
        UpdateManifest.Verify(result, key.ExportSubjectPublicKeyInfoPem(), keyId);
        return result;
    }

    internal static void VerifyPackage(string zipPath, UpdateManifest manifest)
    {
        var file = new FileInfo(zipPath);
        if (file.Length != manifest.PackageSize || HashFile(file.FullName) != manifest.PackageSha256)
            throw new InvalidDataException("本地 ZIP 不匹配已签名清单");
        using var archive = ZipFile.OpenRead(file.FullName);
        CheckArchiveEntries(archive, manifest.Files.Select(entry => entry.Path));
        foreach (var expected in manifest.Files)
        {
            var entry = archive.GetEntry(expected.Path);
            if (entry is null || entry.Length != expected.Size || HashEntry(entry) != expected.Sha256)
                throw new InvalidDataException("ZIP 文件内容不匹配签名清单：" + expected.Path);
        }
    }

    private static Dictionary<string, bool> ReadManagedFiles(string mode, string? listPath)
    {
        var files = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["拾刻.exe"] = false };
        if (mode == "exe-only")
        {
            if (listPath is not null) throw new InvalidDataException("exe-only 不接受额外文件清单");
            return files;
        }
        if (mode != "files" || listPath is null) throw new InvalidDataException("files 模式需要显式 managed-files JSON数组");
        using var doc = JsonDocument.Parse(File.ReadAllText(listPath));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("受管理文件清单必须为数组");
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
                !item.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("allowCreate", out var create) || create.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("受管理条目只能包含path和allowCreate");
            var path = pathValue.GetString()!;
            var rootDll = Regex.IsMatch(path, @"^[A-Za-z0-9][A-Za-z0-9_.-]*\.dll$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var sound = path.StartsWith("sounds/", StringComparison.Ordinal) && path[7..].IndexOfAny(['/', '\\', ':']) < 0 &&
                new[] { ".mp3", ".wav", ".ogg", ".flac" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
            if ((!rootDll && !sound) || path.Contains("..", StringComparison.Ordinal) || path.Any(char.IsControl) || !files.TryAdd(path, create.GetBoolean()))
                throw new InvalidDataException("非法或重复受管理路径；仅允许根DLL和sounds单层音频：" + path);
        }
        if (files.Count == 1) throw new InvalidDataException("files 模式必须显式列出至少一个非主EXE文件");
        return files;
    }

    private static Dictionary<string, HashSet<string>> ReadBaselines(IEnumerable<string> paths, string? packagesPath, string? manifestsPath)
    {
        var hashes = paths.ToDictionary(path => path, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
        foreach (var path in ReadPathList(packagesPath))
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
                throw new InvalidDataException("基线ZIP存在大小写重复文件");
            foreach (var target in hashes.Keys)
                if (archive.Entries.SingleOrDefault(entry => string.Equals(entry.FullName, target, StringComparison.OrdinalIgnoreCase)) is { } entry)
                    hashes[target].Add(HashEntry(entry));
        }
        foreach (var path in ReadPathList(manifestsPath))
        {
            var baseline = UpdateManifest.Verify(File.ReadAllText(path), ReleaseTrust.PublicKeyPem, ReleaseTrust.KeyId);
            foreach (var file in baseline.Files)
                if (hashes.TryGetValue(file.Path, out var values)) values.Add(file.Sha256);
        }
        return hashes;
    }

    private static string[] ReadPathList(string? path)
    {
        if (path is null) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("基线清单文件必须是本地路径字符串数组");
        return doc.RootElement.EnumerateArray().Select(item => Path.GetFullPath(item.GetString()!)).ToArray();
    }

    private static void CheckArchiveEntries(ZipArchive archive, IEnumerable<string> managedPaths)
    {
        var allowed = new HashSet<string>(ExpectedEntries.Concat(managedPaths), StringComparer.Ordinal);
        if (archive.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1) ||
            ExpectedEntries.Any(path => archive.GetEntry(path) is null) || archive.Entries.Any(entry => !allowed.Contains(entry.FullName)))
            throw new InvalidDataException("完整 ZIP 必须包含约定七文件，额外文件必须明确列入更新清单；拒绝个人配置或重复条目");
        foreach (var entry in archive.Entries) RequireRegularEntry(entry);
    }

    private static string HashEntry(ZipArchiveEntry entry)
    {
        RequireRegularEntry(entry);
        if (entry.Length is <= 0 or > 134217728) throw new InvalidDataException("ZIP条目大小异常");
        using var stream = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            length += read;
            if (length > entry.Length) throw new InvalidDataException("ZIP实际解压大小超过声明");
            hash.AppendData(buffer, 0, read);
        }
        if (length != entry.Length) throw new InvalidDataException("ZIP实际解压大小不符");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void RequireRegularEntry(ZipArchiveEntry entry)
    {
        var unixType = ((uint)entry.ExternalAttributes >> 16) & 0xF000;
        if ((entry.ExternalAttributes & (int)FileAttributes.Directory) != 0 || unixType is not (0 or 0x8000))
            throw new InvalidDataException("ZIP条目必须是普通文件，不能是目录、符号链接或设备：" + entry.FullName);
    }

    internal static void ValidateVersion(string version)
    {
        if (!Regex.IsMatch(version, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(version, out _)) throw new InvalidDataException("版本必须是规范的三段数字");
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void WriteNewOrIdentical(string path, string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).SequenceEqual(bytes))
                throw new IOException("输出文件已存在且内容不同，拒绝覆盖：" + path);
            return;
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
