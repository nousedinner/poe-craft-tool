using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace ShiKe.Services.Updates;

/// <summary>下载可信版本 ZIP，只暂存签名清单列出的程序文件，不执行整包解压。</summary>
public sealed class UpdatePackageDownloader : IDisposable
{
    private readonly HttpClient _http;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(3);

    public UpdatePackageDownloader()
        : this(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { }

    internal UpdatePackageDownloader(HttpMessageHandler handler)
        => _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<string> PrepareAsync(UpdateManifest manifest, string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        if (manifest.Mode is not ("exe-only" or "files") || manifest.MinUpdaterVersion > UpdateManifest.UpdaterProtocolVersion ||
            manifest.RuntimeMajor != UpdateManifest.CurrentRuntimeMajor || manifest.Platform != UpdateManifest.CurrentPlatform ||
            manifest.ConfigSchema != UpdateManifest.CurrentConfigSchema)
            throw new InvalidDataException("此发行不满足本更新器的兼容要求");
        var staging = PrepareEmptyDirectory(directory);
        var packagePart = Path.Combine(staging, "package.zip.part");
        var package = Path.Combine(staging, "package.zip");
        var candidates = Path.Combine(staging, "candidates");
        var ownedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        var token = timeout.Token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, manifest.PackageUri);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                if ((int)response.StatusCode is >= 300 and <= 399)
                    throw new InvalidDataException("更新包不能重定向到其他下载地址");
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } contentLength && contentLength != manifest.PackageSize)
                    throw new InvalidDataException("更新包响应大小与签名清单不一致");
                using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await CopyVerifiedAsync(source, packagePart, manifest.PackageSize, manifest.PackageSha256, ownedFiles, token)
                    .ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(packagePart, package);
            ownedFiles.Add(package);
            using (var packageStream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                if (archive.Entries.Count > 256) throw new InvalidDataException("更新包包含过多文件项");
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    ValidateArchivePath(entry.FullName);
                    if (!entries.TryAdd(entry.FullName.TrimEnd('/'), entry))
                        throw new InvalidDataException("更新包文件项重复或存在大小写冲突");
                }
                Directory.CreateDirectory(candidates);
                ownedDirectories.Add(candidates);
                foreach (var file in manifest.Files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!entries.TryGetValue(file.Path, out var entry) || entry.FullName != file.Path)
                        throw new InvalidDataException("更新包缺少清单声明的文件 " + file.Path);
                    var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if ((entry.ExternalAttributes & (int)FileAttributes.Directory) != 0 || unixType is 0x4000 or 0xA000 ||
                        entry.Length != file.Size)
                        throw new InvalidDataException("更新文件类型或大小与签名清单不一致");
                    var target = Path.GetFullPath(Path.Combine(candidates, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(candidates + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新文件超出暂存目录");
                    var parent = Path.GetDirectoryName(target)!;
                    if (!Directory.Exists(parent))
                    {
                        Directory.CreateDirectory(parent);
                        ownedDirectories.Add(parent);
                    }
                    AssertNoLinks(parent);
                    var part = target + ".part";
                    using (var content = entry.Open())
                        await CopyVerifiedAsync(content, part, file.Size, file.Sha256, ownedFiles, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    File.Move(part, target);
                    ownedFiles.Add(target);
                }
            }
            token.ThrowIfCancellationRequested();
            return Path.Combine(candidates, "拾刻.exe");
        }
        catch
        {
            // 只清理本次成功创建的文件；永不覆盖或删除预先存在的文件与目录。
            foreach (var path in ownedFiles)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (var path in ownedDirectories.OrderByDescending(path => path.Length))
            {
                try { Directory.Delete(path, recursive: false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static string PrepareEmptyDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidDataException("更新暂存目录必须是绝对路径");
        var fullPath = Path.GetFullPath(directory);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("更新暂存目录必须位于本地磁盘");
        AssertNoLinks(fullPath);
        Directory.CreateDirectory(fullPath);
        if (Directory.EnumerateFileSystemEntries(fullPath).Any())
            throw new InvalidDataException("更新暂存目录不是空目录，已保留原文件");
        return fullPath;
    }

    private static void AssertNoLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current.FullName); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            // 不使用 Exists 预筛选，否则目标已失效的链接可能被当作尚未创建的目录。
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新暂存路径不能包含链接");
        }
    }

    private static void ValidateArchivePath(string path)
    {
        var parts = path.TrimEnd('/').Split('/');
        if (parts.Length == 0 || path.StartsWith('/') || parts.Any(part => !UpdateManifest.IsSafeFileName(part)))
            throw new InvalidDataException("更新包包含无效路径");
    }

    private static async Task CopyVerifiedAsync(Stream source, string path, long expectedSize, string expectedHash,
        HashSet<string> ownedFiles, CancellationToken token)
    {
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        ownedFiles.Add(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > expectedSize || total > UpdateManifest.MaximumArtifactBytes)
                throw new InvalidDataException("更新文件实际大小超过清单");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        if (total != expectedSize || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expectedHash)))
            throw new InvalidDataException("更新文件大小或 SHA-256 校验失败");
        await destination.FlushAsync(token).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    public void Dispose() => _http.Dispose();
}
