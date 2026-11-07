using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShiKe.Services.Updates;

namespace ShiKe.ReleaseTools;

internal static class RemotePublisher
{
    internal const string ManifestUrl = "https://open.cancanneed.top/version.json";
    private const string CacheMigrationNotice = "Before the first release under a new fixed-entry cache policy, historical CDN entries must be cleared by the selected cloudflare strategy or manually, or allowed to expire after the policy takes effect. Browser caches are separate. Ordinary public GET checks observe this connection only; they do not prove historical caches were cleared.";

    internal static async Task RunAsync(UpdateManifest manifest, string manifestPath, string zipPath,
        string host, string remoteRoot, bool execute, bool rollback, string? transaction, string? zoneId, string tokenEnvironment,
        string cacheStrategy = "headers")
    {
        ValidateRemoteArguments(host, remoteRoot);
        var strategy = ParseCacheStrategy(cacheStrategy);
        if (!rollback && transaction is not null)
            throw new ArgumentException("新发布不得指定已有事务标识；恢复旧事务只能使用显式 rollback");
        if (rollback && (transaction is null || !Regex.IsMatch(transaction, "^[a-f0-9]{32}$")))
            throw new ArgumentException("回退必须指定发布日志中的 transaction 标识");
        transaction ??= Guid.NewGuid().ToString("N");
        var packagePath = $"{remoteRoot}/downloads/releases/{manifest.VersionText}/shike-win-x64.zip";
        var plan = new
        {
            mode = execute ? rollback ? "rollback" : "execute" : "plan-only", host, remoteRoot,
            version = manifest.VersionText, transaction, immutablePackage = packagePath, cacheStrategy = StrategyName(strategy),
            fixedAlias = remoteRoot + "/downloads/shike-latest.zip", manifest = remoteRoot + "/version.json",
            phases = new[] { "verify-local-signature-and-package",
                strategy == CacheStrategy.Cloudflare ? "purge-and-verify-existing-fixed-entry-cache-policy" : "verify-existing-fixed-entry-cache-policy",
                "upload-immutable-version", "verify-public-versioned-package", "switch-fixed-alias",
                strategy == CacheStrategy.Cloudflare ? "purge-fixed-cache-via-cloudflare" : "use-effective-fixed-entry-cache-headers",
                "verify-public-fixed-package-and-cache-policy", "announce-manifest-last",
                strategy == CacheStrategy.Cloudflare ? "purge-and-verify-public-manifest" : "verify-public-manifest",
                "record-completion" },
            cacheGate = "Both strategies require ordinary public GET content/hash/cache checks. Fixed ZIP must require revalidation and manifest must be no-store. Only cloudflare strategy requires API credentials.",
            historicalCacheMigration = CacheMigrationNotice,
            credentialsRead = false, serverContacted = false,
        };
        Console.WriteLine(JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        if (!execute) return; // No credential lookup, SSH, SCP or HTTP occurs above this boundary.

        var cachePolicy = PrepareCachePolicy(strategy, zoneId, tokenEnvironment);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
        var control = new RemoteControl(host, remoteRoot, manifest, manifestPath, transaction);
        var beginAttempted = false;
        var completed = false;
        var recordPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "publish-" + transaction + ".json");
        void Record(string phase, string? error = null)
        {
            var temp = recordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, transaction, version = manifest.VersionText,
                phase, host, remoteRoot, cacheStrategy = cachePolicy.Name, packageSha256 = manifest.PackageSha256,
                recordedUtc = DateTimeOffset.UtcNow, error, historicalCacheMigration = CacheMigrationNotice,
                recovery = $"Publish-UpdateRelease.ps1 -ReleaseDirectory <this directory> -Rollback -Transaction {transaction} -CacheStrategy {cachePolicy.Name} -Execute" +
                    (strategy == CacheStrategy.Cloudflare ? " <Cloudflare arguments>" : "") },
                new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, recordPath, overwrite: true);
        }
        Record("preflight");
        try
        {
            if (rollback)
            {
                var restored = await control.InvokeAsync("rollback");
                await ApplyCachePolicyAndVerifyAsync(http, cachePolicy,
                    () => VerifyRollbackAsync(http, restored, CancellationToken.None), CancellationToken.None);
                Record("rolled-back");
                Console.WriteLine(JsonSerializer.Serialize(new { status = "rolled-back", transaction,
                    cacheStrategy = cachePolicy.Name, immutablePackageRetained = true }));
                return;
            }
            // Check known public cache policy before any mutation; still recheck after alias switch.
            await ApplyCachePolicyAndVerifyAsync(http, cachePolicy, async () =>
            {
                await RequireManifestNoStoreAsync(http, CancellationToken.None);
                await RequireFixedEntryRevalidationAsync(http, CancellationToken.None);
            }, CancellationToken.None);
            beginAttempted = true;
            var start = await control.InvokeAsync("begin");
            Record("begun");
            await CopyAsync(host, zipPath, start.GetProperty("part").GetString()!);
            await CopyAsync(host, manifestPath, start.GetProperty("nextManifest").GetString()!);
            await control.InvokeAsync("install-package");
            Record("package-installed");
            await VerifyPublicPackageAsync(http, manifest.PackageUrl, manifest.PackageSize, manifest.PackageSha256, false, CancellationToken.None);
            await control.InvokeAsync("switch-alias");
            Record("alias-switched");
            await ApplyCachePolicyAndVerifyAsync(http, cachePolicy, async () =>
            {
                await VerifyPublicPackageAsync(http, ReleasePreparation.ManualUrl, manifest.PackageSize, manifest.PackageSha256, true, CancellationToken.None);
                await RequireManifestNoStoreAsync(http, CancellationToken.None);
            }, CancellationToken.None);
            await control.InvokeAsync("announce");
            Record("announced");
            await ApplyCachePolicyAndVerifyAsync(http, cachePolicy,
                () => VerifyPublicManifestAsync(http, File.ReadAllText(manifestPath), CancellationToken.None), CancellationToken.None);
            await control.InvokeAsync("complete");
            completed = true;
            Record("complete");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "published", version = manifest.VersionText, transaction, cacheStrategy = cachePolicy.Name,
                verified = new[] { "signature", "immutable-package-size-and-sha256", "fixed-package-size-and-sha256",
                    "fixed-cache-policy", "public-manifest-exact-bytes" }
                    .Concat(strategy == CacheStrategy.Cloudflare ? new[] { "cloudflare-purge-accepted" } : []),
                limitation = CacheMigrationNotice,
            }));
        }
        catch (Exception error)
        {
            if (completed)
                throw new IOException("线上发布已完成，但本地完成记录未保存；不能因此重复发布。transaction=" + transaction, error);
            if (!beginAttempted)
            {
                try { Record(rollback ? "recovery-required" : "failed-before-publication", error.Message); } catch (Exception) { }
                throw;
            }
            JsonElement restored = default;
            var recovery = await RecoverAfterFailureAsync(
                async () => (await control.InvokeAsync("status")).GetProperty("phase").GetString(),
                async () => { await control.InvokeAsync("complete"); },
                async () =>
                {
                    await VerifyPublicPackageAsync(http, ReleasePreparation.ManualUrl, manifest.PackageSize, manifest.PackageSha256, true, CancellationToken.None);
                    await VerifyPublicManifestAsync(http, File.ReadAllText(manifestPath), CancellationToken.None);
                },
                async () =>
                {
                    restored = await control.InvokeAsync("rollback-incomplete");
                },
                () => ApplyCachePolicyAndVerifyAsync(http, cachePolicy,
                    () => VerifyRollbackAsync(http, restored, CancellationToken.None), CancellationToken.None));
            if (recovery.Kind == PublicationRecovery.Completed)
            {
                try { Record("complete-confirmed-after-error", error.Message); } catch (Exception) { }
                Console.WriteLine(JsonSerializer.Serialize(new { status = "published", transaction, cacheStrategy = cachePolicy.Name, acknowledgementRecovered = true,
                    verified = new[] { "remote-complete", "public-fixed-package", "public-signed-manifest" } }));
                return;
            }
            if (recovery.Kind == PublicationRecovery.RolledBack)
            {
                try { Record("rolled-back", error.Message); } catch (Exception) { }
                Console.Error.WriteLine("本次发布未完成，已恢复之前清单与固定别名；版本文件保留。transaction=" + transaction);
                throw;
            }
            if (recovery.Kind == PublicationRecovery.NotStarted)
            {
                try { Record("failed-before-publication", error.Message); } catch (Exception) { }
                throw;
            }
            var phase = recovery.Kind == PublicationRecovery.CompletedUnconfirmed ? "completed-publication-unconfirmed" : "recovery-required";
            try { Record(phase, error.Message + "; " + recovery.Message); } catch (Exception) { }
            throw new IOException("发布结果需核对，已保留线上现状且未猜测回滚。transaction=" + transaction +
                "; 原因=" + error.Message + "; 核对原因=" + recovery.Message, error);
        }
    }

    internal enum CacheStrategy { Headers, Cloudflare }

    internal static CacheStrategy ParseCacheStrategy(string value) => value.ToLowerInvariant() switch
    {
        "headers" => CacheStrategy.Headers,
        "cloudflare" => CacheStrategy.Cloudflare,
        _ => throw new ArgumentException("缓存策略必须为 headers 或 cloudflare"),
    };

    private static string StrategyName(CacheStrategy strategy)
        => strategy == CacheStrategy.Headers ? "headers" : "cloudflare";

    internal sealed class CachePolicy(CacheStrategy strategy, string? zoneId, string? token)
    {
        internal string Name => StrategyName(strategy);

        internal Task RefreshAsync(HttpClient http, CancellationToken cancellationToken)
            => strategy == CacheStrategy.Headers ? Task.CompletedTask :
                PurgeAsync(http, zoneId!, token!, cancellationToken);
    }

    internal static CachePolicy PrepareCachePolicy(CacheStrategy strategy, string? zoneId, string tokenEnvironment,
        Func<string, string?>? readEnvironment = null)
    {
        // headers 不读取环境变量或使用 Cloudflare 参数，不要求任何第三方凭据。
        if (strategy == CacheStrategy.Headers) return new(strategy, null, null);
        if (strategy != CacheStrategy.Cloudflare) throw new ArgumentException("未知缓存策略");
        if (zoneId is null || !Regex.IsMatch(zoneId, @"\A[a-fA-F0-9]{32}\z"))
            throw new InvalidOperationException("cloudflare 策略需要有效的 Cloudflare ZoneId");
        if (!Regex.IsMatch(tokenEnvironment, @"\A[A-Za-z_][A-Za-z0-9_]*\z"))
            throw new ArgumentException("凭据环境变量名无效");
        var token = (readEnvironment ?? Environment.GetEnvironmentVariable)(tokenEnvironment);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("cloudflare 策略缺少指定环境变量中的 API token，已阻止服务器写入");
        return new(strategy, zoneId, token);
    }

    internal static async Task ApplyCachePolicyAndVerifyAsync(HttpClient http, CachePolicy policy,
        Func<Task> verify, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await policy.RefreshAsync(http, token);
        token.ThrowIfCancellationRequested();
        // 两种策略都必须执行相同内容与缓存核验；跳过 purge 不等于跳过验证。
        await verify();
    }

    internal enum PublicationRecovery { NotStarted, Completed, RolledBack, RecoveryRequired, CompletedUnconfirmed }
    internal sealed record PublicationRecoveryResult(PublicationRecovery Kind, string? Message = null);

    /// <summary>真实异常路径使用的恢复协调；完整事务确认丢失和未知状态都不能触发猜测回退。</summary>
    internal static async Task<PublicationRecoveryResult> RecoverAfterFailureAsync(Func<Task<string?>> readPhase,
        Func<Task> complete, Func<Task> verifyCompleted, Func<Task> rollback, Func<Task> verifyRolledBack)
    {
        string? phase;
        try { phase = await readPhase(); }
        catch (Exception error) { return new(PublicationRecovery.RecoveryRequired, "远端状态无法确认：" + error.Message); }
        if (phase == "not-started") return new(PublicationRecovery.NotStarted);
        if (phase == "complete")
        {
            try
            {
                // Python complete幂等校验当前发行并只释放本事务残留锁，绝不撤销公开入口。
                await complete();
                await verifyCompleted();
                return new(PublicationRecovery.Completed);
            }
            catch (Exception error) { return new(PublicationRecovery.CompletedUnconfirmed, error.Message); }
        }
        if (phase is not ("prepared" or "acquiring" or "begun" or "package-installed" or "switching-alias" or
                          "alias-switched" or "announcing" or "announced" or "rolling-back" or "rolled-back"))
            return new(PublicationRecovery.RecoveryRequired, "远端状态未知或缺少可恢复身份，保持现状");
        try
        {
            // status读取与rollback均由服务端核对同一transaction和claim；不会操作他人事务。
            await rollback();
            await verifyRolledBack();
            return new(PublicationRecovery.RolledBack);
        }
        catch (Exception error) { return new(PublicationRecovery.RecoveryRequired, error.Message); }
    }

    internal static void ValidateRemoteArguments(string host, string root)
    {
        if (!Regex.IsMatch(host, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,120}$")) throw new ArgumentException("SSH host 只能使用已配置的简单别名");
        if (!Regex.IsMatch(root, "^/[A-Za-z0-9_./-]+$") || root.Split('/').Contains("..") || root.EndsWith('/'))
            throw new ArgumentException("服务器发布目录格式无效");
    }

    internal static async Task PurgeAsync(HttpClient http, string zoneId, string token, CancellationToken cancellationToken,
        TimeSpan? completeTimeout = null)
    {
        using var deadline = CreateDeadline(cancellationToken, completeTimeout);
        cancellationToken = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.cloudflare.com/client/v4/zones/{zoneId}/purge_cache");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(new { files = new[] { ReleasePreparation.ManualUrl, ManifestUrl } }),
            Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Cloudflare 精确缓存清理失败，HTTP " + (int)response.StatusCode);
        var bytes = await ReadLimitedAsync(response, 1048576, cancellationToken);
        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Cloudflare 未确认缓存清理成功");
    }

    internal static async Task VerifyPublicPackageAsync(HttpClient http, string url, long expectedSize, string expectedHash,
        bool fixedAlias, CancellationToken cancellationToken, TimeSpan? completeTimeout = null)
    {
        using var deadline = CreateDeadline(cancellationToken, completeTimeout);
        cancellationToken = deadline.Token;
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException("公网发布文件未返回200，HTTP " + (int)response.StatusCode);
        if (fixedAlias && !AllowsRevalidation(response.Headers.CacheControl))
            throw new InvalidDataException("固定 ZIP 的公开响应仍允许长缓存，已阻止发布 latest");
        if (response.Content.Headers.ContentLength is { } declared && declared != expectedSize)
            throw new InvalidDataException("公网包大小与已签名清单不一致");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            received += read;
            if (received > expectedSize) throw new InvalidDataException("公网包超过已签名大小");
            hash.AppendData(buffer, 0, read);
        }
        if (received != expectedSize || Convert.ToHexString(hash.GetHashAndReset()) != expectedHash)
            throw new InvalidDataException("公网包完整性校验失败，不能切换版本清单");
    }

    internal static bool AllowsRevalidation(CacheControlHeaderValue? cache)
    {
        if (cache is null) return false;
        if (cache.NoStore || (cache.NoCache && cache.NoCacheHeaders.Count == 0)) return true;
        // max-age=0 只使响应立即过期。还须禁止旧内容重用，且共享缓存期限不能覆盖它。
        // 限定字段的 no-cache 不能替代整份 ZIP 的重新验证。
        return cache.MaxAge == TimeSpan.Zero && cache.MustRevalidate &&
               (cache.SharedMaxAge is null || cache.SharedMaxAge == TimeSpan.Zero) &&
               !cache.Extensions.Any(extension =>
                   (extension.Name.Equals("stale-if-error", StringComparison.OrdinalIgnoreCase) ||
                    extension.Name.Equals("stale-while-revalidate", StringComparison.OrdinalIgnoreCase)) &&
                   extension.Value?.Trim('"') != "0");
    }

    internal static async Task RequireFixedEntryRevalidationAsync(HttpClient http, CancellationToken token,
        TimeSpan? completeTimeout = null)
    {
        using var deadline = CreateDeadline(token, completeTimeout);
        // 普通 GET，不添加 no-cache 请求头或随机查询参数；不能掩盖旧客户端实际取得的策略。
        using var response = await http.GetAsync(ReleasePreparation.ManualUrl,
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode != HttpStatusCode.OK || !AllowsRevalidation(response.Headers.CacheControl))
            throw new InvalidDataException("固定 ZIP 当前公开响应不要求重新验证；请先处理缓存策略与历史旧缓存，再发布");
    }

    internal static async Task VerifyRollbackAsync(HttpClient http, JsonElement restored, CancellationToken token)
    {
        await VerifyPublicPackageAsync(http, ReleasePreparation.ManualUrl,
            restored.GetProperty("previousPackageSize").GetInt64(), restored.GetProperty("previousPackageSha256").GetString()!, true, token);
        await VerifyPublicManifestHashAsync(http, restored.GetProperty("previousManifestSha256").GetString()!, token);
    }

    internal static async Task VerifyPublicManifestHashAsync(HttpClient http, string expectedHash, CancellationToken token,
        TimeSpan? completeTimeout = null)
    {
        using var deadline = CreateDeadline(token, completeTimeout);
        token = deadline.Token;
        using var response = await http.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
            throw new InvalidDataException("恢复后的旧清单公开响应或缓存策略不符");
        var bytes = await ReadLimitedAsync(response, 1048576, token);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != expectedHash)
            throw new InvalidDataException("公网旧清单尚未恢复，不能报告回滚成功");
    }

    internal static async Task RequireManifestNoStoreAsync(HttpClient http, CancellationToken token, TimeSpan? completeTimeout = null)
    {
        using var deadline = CreateDeadline(token, completeTimeout);
        token = deadline.Token;
        using var response = await http.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
            throw new InvalidDataException("公开 version.json 必须返回200且明确no-store；已阻止发布");
        await ReadLimitedAsync(response, 1048576, token);
    }

    internal static async Task VerifyPublicManifestAsync(HttpClient http, string expected, CancellationToken token, TimeSpan? completeTimeout = null)
    {
        using var deadline = CreateDeadline(token, completeTimeout);
        token = deadline.Token;
        using var response = await http.GetAsync(ManifestUrl, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true)
            throw new InvalidDataException("新清单公开响应或缓存策略不符");
        var bytes = await ReadLimitedAsync(response, 1048576, token);
        if (!bytes.SequenceEqual(Encoding.UTF8.GetBytes(expected))) throw new InvalidDataException("公开清单尚未切换为本次签名内容");
        UpdateManifest.Verify(Encoding.UTF8.GetString(bytes), ReleaseTrust.PublicKeyPem, ReleaseTrust.KeyId);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("发布接口响应超出上限");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken token, TimeSpan? completeTimeout)
    {
        // ResponseHeadersRead结束后HttpClient.Timeout不再覆盖流读取；本令牌必须贯穿整个操作。
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(completeTimeout ?? TimeSpan.FromMinutes(3));
        return deadline;
    }

    private sealed class RemoteControl(string host, string root, UpdateManifest manifest, string manifestPath, string transaction)
    {
        internal async Task<JsonElement> InvokeAsync(string operation)
        {
            var input = JsonSerializer.Serialize(new { root, version = manifest.VersionText, transaction, operation,
                packageSha256 = manifest.PackageSha256, packageSize = manifest.PackageSize,
                manifestSha256 = ReleasePreparation.HashFile(manifestPath) });
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "publish-remote.py"));
            var output = await RunProcessAsync("ssh", ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=15",
                host, "python3 - " + QuoteUnix(input)], source, TimeSpan.FromSeconds(45));
            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.Clone();
        }
    }

    private static async Task CopyAsync(string host, string local, string remote)
    {
        if (!Regex.IsMatch(remote, "^/[A-Za-z0-9_./-]+$") || remote.Split('/').Contains(".."))
            throw new InvalidDataException("服务器返回了不安全的上传路径");
        await RunProcessAsync("scp", ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=15",
            local, host + ":" + remote], null, TimeSpan.FromMinutes(5));
    }

    internal static string QuoteUnix(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static async Task<string> RunProcessAsync(string executable, IEnumerable<string> arguments, string? input, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = input is not null, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动发布依赖工具：" + executable);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input is not null) { await process.StandardInput.WriteAsync(input.Replace("\r\n", "\n", StringComparison.Ordinal)); process.StandardInput.Close(); }
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("发布依赖工具超过限定时间：" + executable);
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException(executable + " 未成功：" + error.Trim());
        return output;
    }
}
