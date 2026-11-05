using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShiKe.Services.Updates;

namespace ShiKe.ReleaseTools;

internal static class ReleaseSelfTests
{
    internal static async Task RunAsync()
    {
        using var key = RSA.Create(3072); // 只用于本次进程的测试密钥，不写入磁盘或客户端。
        var artifact = new ReleaseArtifact("1.0.60", "unused.zip", "unused.exe", 3,
            Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })), 2, new string('A', 64),
            [new("拾刻.exe", 2, new string('A', 64), [], false)]);
        var json = ReleasePreparation.CreateManifest(artifact, key, "test-key", "exe-only", "1.0.52", 1, 10, "win-x64", 1);
        var manifest = UpdateManifest.Verify(json, key.ExportSubjectPublicKeyInfoPem(), "test-key");
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("版本说明绑定版本与EXE元数据且拒绝旧EXE", () =>
            {
                var hash = new string('A', 64);
                var marker = "描述 [ReleaseNotesVersion=1.0.60;Sha256=" + hash + "]";
                ReleaseNotesArtifact.ValidateMetadata("1.0.60.0", marker, "1.0.60", hash);
                Reject(() => ReleaseNotesArtifact.ValidateMetadata("1.0.60.0", "旧EXE无标记", "1.0.60", hash));
                Reject(() => ReleaseNotesArtifact.ValidateMetadata("1.0.60.0", marker, "1.0.61", hash));
                Reject(() => ReleaseNotesArtifact.ValidateMetadata("1.0.60.0", marker, "1.0.60", new string('B', 64)));
                Reject(() => ReleaseNotesArtifact.ValidateMetadata("1.0.60.0", marker + marker, "1.0.60", hash));
                Require(ReleaseNotesFormat.Parse("Version: 1.0.60\n\n说明", "1.0.60") == "说明" &&
                        ReleaseNotesFormat.Parse("Version: 1.0.59\n说明", "1.0.60") is null,
                    "准备端应使用和客户端相同的版本绑定格式");
                return Task.CompletedTask;
            }),
            ("签名清单由客户端同协议验证", () =>
            {
                Require(manifest.VersionText == artifact.Version && manifest.PackageSha256 == artifact.PackageSha256 &&
                    manifest.CanApply(new Version(1, 0, 52)), "发布端与客户端协议不一致");
                return Task.CompletedTask;
            }),
            ("签名覆盖载荷且兼容旧字段", () =>
            {
                var envelope = JsonNode.Parse(json)!.AsObject();
                Require(envelope["latest"]!.GetValue<string>() == artifact.Version &&
                    envelope["url"]!.GetValue<string>() == ReleasePreparation.ManualUrl, "旧客户端入口变化");
                var payload = Convert.FromBase64String(envelope["autoUpdate"]!["payload"]!.GetValue<string>());
                payload[^2] ^= 1;
                envelope["autoUpdate"]!["payload"] = Convert.ToBase64String(payload);
                Reject(() => UpdateManifest.Verify(envelope.ToJsonString(), key.ExportSubjectPublicKeyInfoPem(), "test-key"));
                return Task.CompletedTask;
            }),
            ("受管理资源的官方基线进入签名", () =>
            {
                var files = artifact.Files.Concat(new[] { new ReleaseFile("sounds/default_ding.mp3", 5, new string('B', 64),
                    [new string('C', 64), new string('D', 64)], true) }).ToArray();
                var signed = ReleasePreparation.CreateManifest(artifact with { Files = files }, key, "test-key", "files", "1.0.52", 1, 10, "win-x64", 1);
                var parsed = UpdateManifest.Verify(signed, key.ExportSubjectPublicKeyInfoPem(), "test-key");
                var audio = parsed.Files.Single(file => file.Path.StartsWith("sounds/", StringComparison.Ordinal));
                Require(audio.BaseSha256.Count == 2 && audio.AllowCreate, "资源基线或新增策略未签名");
                return Task.CompletedTask;
            }),
            ("SSH目标与目录拒绝命令字符", () =>
            {
                RemotePublisher.ValidateRemoteArguments("myserver", "/var/www/poead");
                Reject(() => RemotePublisher.ValidateRemoteArguments("myserver;echo", "/var/www/poead"));
                Reject(() => RemotePublisher.ValidateRemoteArguments("myserver", "/var/www/../etc"));
                Require(RemotePublisher.QuoteUnix("a'b") == "'a'\\''b'", "远端参数引用不正确");
                return Task.CompletedTask;
            }),
            ("默认发布只输出计划不读取凭据或联网", async () =>
            {
                foreach (var strategy in new[] { "headers", "cloudflare" })
                    await RemotePublisher.RunAsync(manifest, "not-read.json", "not-read.zip", "myserver", "/var/www/poead",
                        false, false, null, null, "SHIKE_ABSENT_TEST_" + Guid.NewGuid().ToString("N"), strategy);
            }),
            ("选择Cloudflare策略但缺凭据不得开始服务器写入", async () =>
            {
                await RejectAsync(() => RemotePublisher.RunAsync(manifest, "not-read.json", "not-read.zip", "myserver", "/var/www/poead",
                    true, false, null, new string('a', 32), "SHIKE_ABSENT_TEST_" + Guid.NewGuid().ToString("N"), "cloudflare"));
            }),
            ("headers策略不读取凭据也不发送Cloudflare请求", async () =>
            {
                var reads = 0;
                var requests = 0;
                var verification = 0;
                var policy = RemotePublisher.PrepareCachePolicy(RemotePublisher.CacheStrategy.Headers,
                    "unused-zone", "unused env name", _ => { reads++; throw new Exception("不能读取CF凭据"); });
                using var http = Client((_, _) =>
                {
                    requests++;
                    return Response(Encoding.UTF8.GetBytes("{\"success\":true}"), "no-store");
                });
                await RemotePublisher.ApplyCachePolicyAndVerifyAsync(http, policy,
                    () => { verification++; return Task.CompletedTask; }, CancellationToken.None);
                Require(policy.Name == "headers" && reads == 0 && requests == 0 && verification == 1,
                    "默认策略应直接保留核验步骤，不索取或使用CF凭据");
            }),
            ("只有Cloudflare策略检查并读取其凭据", () =>
            {
                var reads = 0;
                string? Read(string name) { reads++; Require(name == "TEST_TOKEN", "读取了错误的环境变量"); return "test-only-token"; }
                var policy = RemotePublisher.PrepareCachePolicy(RemotePublisher.CacheStrategy.Cloudflare,
                    new string('a', 32), "TEST_TOKEN", Read);
                Require(policy.Name == "cloudflare" && reads == 1, "显式CF策略应读取一次指定凭据");
                Reject(() => RemotePublisher.PrepareCachePolicy(RemotePublisher.CacheStrategy.Cloudflare, null, "TEST_TOKEN", Read));
                Require(reads == 1, "Zone无效时不应读取凭据");
                Reject(() => RemotePublisher.PrepareCachePolicy(RemotePublisher.CacheStrategy.Cloudflare,
                    new string('a', 32), "TEST_TOKEN", _ => null));
                Reject(() => RemotePublisher.ParseCacheStrategy("skip"));
                return Task.CompletedTask;
            }),
            ("固定包重新验证策略拒绝共享缓存覆盖和字段限定", () =>
            {
                foreach (var cache in new[] { "no-store", "no-cache", "no-cache, max-age=14400",
                             "max-age=0, must-revalidate", "max-age=0, s-maxage=0, must-revalidate",
                             "max-age=0, must-revalidate, stale-if-error=0" })
                    Require(RemotePublisher.AllowsRevalidation(CacheControlHeaderValue.Parse(cache)), "应允许生效的严格策略：" + cache);
                foreach (var cache in new[] { "max-age=0", "public, max-age=14400", "must-revalidate",
                             "max-age=0, s-maxage=60, must-revalidate", "no-cache=\"Set-Cookie\"",
                             "max-age=0, must-revalidate, stale-while-revalidate=30",
                             "max-age=0, must-revalidate, stale-if-error=30" })
                    Require(!RemotePublisher.AllowsRevalidation(CacheControlHeaderValue.Parse(cache)), "不能允许旧正文重用：" + cache);
                Require(!RemotePublisher.AllowsRevalidation(null), "缺少明确缓存策略不得发布");
                return Task.CompletedTask;
            }),
            ("发布前固定入口检查使用普通GET且不掩盖缓存", async () =>
            {
                var requests = 0;
                using var http = Client((request, _) =>
                {
                    requests++;
                    Require(request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == ReleasePreparation.ManualUrl &&
                        request.RequestUri.Query.Length == 0 && request.Headers.CacheControl is null && !request.Headers.Pragma.Any(),
                        "检查不能改地址或添加绕缓存请求头");
                    return Response([1], "max-age=0, must-revalidate");
                });
                await RemotePublisher.RequireFixedEntryRevalidationAsync(http, CancellationToken.None);
                Require(requests == 1, "应实际检查固定入口");
                using var longCache = Client((_, _) => Response([1], "max-age=14400"));
                await RejectAsync(() => RemotePublisher.RequireFixedEntryRevalidationAsync(longCache, CancellationToken.None));
            }),
            ("两种缓存策略都必须验证包内容且失败不能跳过", async () =>
            {
                foreach (var strategy in new[] { RemotePublisher.CacheStrategy.Headers, RemotePublisher.CacheStrategy.Cloudflare })
                {
                    var actions = new List<string>();
                    var policy = RemotePublisher.PrepareCachePolicy(strategy, new string('a', 32), "TEST_TOKEN", _ => "test-only-token");
                    using var http = Client((request, _) =>
                    {
                        if (request.RequestUri!.Host == "api.cloudflare.com")
                        {
                            actions.Add("purge");
                            return Response(Encoding.UTF8.GetBytes("{\"success\":true}"), "no-store");
                        }
                        actions.Add("verify-package");
                        Require(request.Headers.CacheControl is null && request.RequestUri.Query.Length == 0, "验真应是普通GET");
                        return Response([1, 2, 3], "no-cache");
                    });
                    await RemotePublisher.ApplyCachePolicyAndVerifyAsync(http, policy,
                        () => RemotePublisher.VerifyPublicPackageAsync(http, ReleasePreparation.ManualUrl, 3, artifact.PackageSha256, true, CancellationToken.None),
                        CancellationToken.None);
                    Require(actions.SequenceEqual(strategy == RemotePublisher.CacheStrategy.Headers
                        ? new[] { "verify-package" } : new[] { "purge", "verify-package" }), "只有CF策略增加purge，内容核验不能改变");
                    await RejectAsync(() => RemotePublisher.ApplyCachePolicyAndVerifyAsync(http, policy,
                        () => RemotePublisher.VerifyPublicPackageAsync(http, ReleasePreparation.ManualUrl, 3, new string('0', 64), true, CancellationToken.None),
                        CancellationToken.None));
                }
            }),
            ("headers回滚仍核验旧包与清单且不调用CF", async () =>
            {
                var oldManifest = Encoding.UTF8.GetBytes("{\"latest\":\"1.0.51\"}");
                using var restored = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    previousPackageSize = 3, previousPackageSha256 = artifact.PackageSha256,
                    previousManifestSha256 = Convert.ToHexString(SHA256.HashData(oldManifest)),
                }));
                var paths = new List<string>();
                var policy = RemotePublisher.PrepareCachePolicy(RemotePublisher.CacheStrategy.Headers, null, "",
                    _ => throw new Exception("headers回滚不能读取凭据"));
                using var http = Client((request, _) =>
                {
                    Require(request.RequestUri!.Host == "open.cancanneed.top" && request.Method == HttpMethod.Get &&
                        request.Headers.CacheControl is null && request.RequestUri.Query.Length == 0, "回滚不得清CF或绕缓存验真");
                    paths.Add(request.RequestUri.AbsolutePath);
                    return request.RequestUri.AbsolutePath == "/version.json"
                        ? Response(oldManifest, "no-store") : Response([1, 2, 3], "no-cache");
                });
                await RemotePublisher.ApplyCachePolicyAndVerifyAsync(http, policy,
                    () => RemotePublisher.VerifyRollbackAsync(http, restored.RootElement, CancellationToken.None), CancellationToken.None);
                Require(paths.SequenceEqual(new[] { "/download/shike-latest.zip", "/version.json" }), "回滚不能省略任一公开入口核验");
            }),
            ("headers清单仍要求no-store且字节不能改变", async () =>
            {
                var policy = RemotePublisher.PrepareCachePolicy(RemotePublisher.CacheStrategy.Headers, null, "");
                var bytes = Encoding.UTF8.GetBytes("{\"latest\":\"1.0.51\"}");
                var expected = Convert.ToHexString(SHA256.HashData(bytes));
                using var longCache = Client((_, _) => Response(bytes, "max-age=60"));
                await RejectAsync(() => RemotePublisher.ApplyCachePolicyAndVerifyAsync(longCache, policy,
                    () => RemotePublisher.VerifyPublicManifestHashAsync(longCache, expected, CancellationToken.None), CancellationToken.None));
                using var altered = Client((_, _) => Response(Encoding.UTF8.GetBytes("{}"), "no-store"));
                await RejectAsync(() => RemotePublisher.ApplyCachePolicyAndVerifyAsync(altered, policy,
                    () => RemotePublisher.VerifyPublicManifestHashAsync(altered, expected, CancellationToken.None), CancellationToken.None));
            }),
            ("重复发布不能借已有事务触发自动回滚", async () =>
            {
                await RejectAsync(() => RemotePublisher.RunAsync(manifest, "not-read.json", "not-read.zip", "myserver", "/var/www/poead",
                    false, false, new string('c', 32), null, "SHIKE_ABSENT_TEST_" + Guid.NewGuid().ToString("N")));
            }),
            ("固定ZIP长缓存阻断发布", async () =>
            {
                using var http = Client((_, _) => Response([1, 2, 3], "public, max-age=14400"));
                await RejectAsync(() => RemotePublisher.VerifyPublicPackageAsync(http, ReleasePreparation.ManualUrl, 3,
                    artifact.PackageSha256, true, CancellationToken.None));
            }),
            ("固定ZIP哈希大小与缓存全部校验", async () =>
            {
                using var http = Client((_, _) => Response([1, 2, 3], "no-cache, no-store"));
                await RemotePublisher.VerifyPublicPackageAsync(http, ReleasePreparation.ManualUrl, 3, artifact.PackageSha256, true, CancellationToken.None);
                await RejectAsync(() => RemotePublisher.VerifyPublicPackageAsync(http, ReleasePreparation.ManualUrl, 3, new string('0', 64), true, CancellationToken.None));
                await RejectAsync(() => RemotePublisher.VerifyPublicPackageAsync(http, ReleasePreparation.ManualUrl, 2, artifact.PackageSha256, true, CancellationToken.None));
            }),
            ("Cloudflare只清理两个固定入口", async () =>
            {
                var observed = false;
                using var http = Client(async (request, token) =>
                {
                    Require(request.Method == HttpMethod.Post && request.RequestUri!.Host == "api.cloudflare.com", "缓存清理接口不正确");
                    Require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "test-only-token", "缓存清理凭据未仅使用授权头");
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                    Require(body["files"]!.AsArray().Select(value => value!.GetValue<string>()).SequenceEqual(new[] { ReleasePreparation.ManualUrl, RemotePublisher.ManifestUrl }),
                        "不得全站purge或清理版本文件");
                    observed = true;
                    return Response(Encoding.UTF8.GetBytes("{\"success\":true}"), "no-store");
                });
                await RemotePublisher.PurgeAsync(http, new string('b', 32), "test-only-token", CancellationToken.None);
                Require(observed, "没有实际调用受控缓存处理器");
            }),
            ("Cloudflare失败不能冒充清理成功", async () =>
            {
                using var http = Client((_, _) => Response(Encoding.UTF8.GetBytes("{\"success\":false}"), "no-store"));
                await RejectAsync(() => RemotePublisher.PurgeAsync(http, new string('b', 32), "test-only-token", CancellationToken.None));
            }),
            ("收到响应头后阻塞的正文也受完整超时约束", async () =>
            {
                using var http = Client((_, _) =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingBody()) };
                    response.Headers.CacheControl = CacheControlHeaderValue.Parse("no-store");
                    return response;
                });
                Func<Task>[] operations =
                [
                    () => RemotePublisher.VerifyPublicPackageAsync(http, ReleasePreparation.ManualUrl, 3, artifact.PackageSha256, true,
                        CancellationToken.None, TimeSpan.FromMilliseconds(25)),
                    () => RemotePublisher.PurgeAsync(http, new string('b', 32), "test-only-token", CancellationToken.None, TimeSpan.FromMilliseconds(25)),
                    () => RemotePublisher.RequireManifestNoStoreAsync(http, CancellationToken.None, TimeSpan.FromMilliseconds(25)),
                    () => RemotePublisher.VerifyPublicManifestAsync(http, "{}", CancellationToken.None, TimeSpan.FromMilliseconds(25)),
                    () => RemotePublisher.VerifyPublicManifestHashAsync(http, new string('A', 64), CancellationToken.None, TimeSpan.FromMilliseconds(25)),
                ];
                foreach (var operation in operations)
                {
                    try { await operation().WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch (OperationCanceledException) { continue; }
                    throw new InvalidOperationException("正文阻塞必须由完整请求期限取消");
                }
            }),
            ("发布ZIP拒绝客户端不接受的文件类型", () =>
            {
                var directory = Path.Combine(AppContext.BaseDirectory, "release-zip-check-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    foreach (var attributes in new[] { 0, (int)FileAttributes.Directory, unchecked((int)0xA1FF0000) })
                    {
                        var path = Path.Combine(directory, attributes.ToString("X8") + ".zip");
                        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                        {
                            foreach (var name in ReleasePreparation.ExpectedEntries)
                            {
                                var entry = archive.CreateEntry(name);
                                if (name == "拾刻.exe") entry.ExternalAttributes = attributes;
                                using var stream = entry.Open();
                                stream.Write(name == "拾刻.exe" ? new byte[] { 4, 5 } : new byte[] { 6 });
                            }
                        }
                        var exeHash = Convert.ToHexString(SHA256.HashData(new byte[] { 4, 5 }));
                        var zipArtifact = artifact with
                        {
                            ZipPath = path, PackageSize = new FileInfo(path).Length,
                            PackageSha256 = ReleasePreparation.HashFile(path), ExeSha256 = exeHash,
                            Files = [new("拾刻.exe", 2, exeHash, [], false)],
                        };
                        var signed = ReleasePreparation.CreateManifest(zipArtifact, key, "test-key", "exe-only", "1.0.52", 1, 10, "win-x64", 1);
                        var verified = UpdateManifest.Verify(signed, key.ExportSubjectPublicKeyInfoPem(), "test-key");
                        if (attributes == 0) ReleasePreparation.VerifyPackage(path, verified);
                        else Reject(() => ReleasePreparation.VerifyPackage(path, verified));
                    }
                }
                finally
                {
                    var root = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("测试清理目录越界");
                    Directory.Delete(directory, recursive: true);
                }
                return Task.CompletedTask;
            }),
            ("回滚必须从公网验证旧包和旧清单", async () =>
            {
                var oldManifest = Encoding.UTF8.GetBytes("{\"latest\":\"1.0.51\"}");
                using var restored = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    previousPackageSize = 3, previousPackageSha256 = artifact.PackageSha256,
                    previousManifestSha256 = Convert.ToHexString(SHA256.HashData(oldManifest)),
                }));
                using var correct = Client((request, _) => request.RequestUri!.AbsolutePath == "/version.json"
                    ? Response(oldManifest, "no-store") : Response([1, 2, 3], "no-cache"));
                await RemotePublisher.VerifyRollbackAsync(correct, restored.RootElement, CancellationToken.None);
                using var stale = Client((request, _) => request.RequestUri!.AbsolutePath == "/version.json"
                    ? Response(Encoding.UTF8.GetBytes("{\"latest\":\"1.0.52\"}"), "no-store") : Response([1, 2, 3], "no-cache"));
                await RejectAsync(() => RemotePublisher.VerifyRollbackAsync(stale, restored.RootElement, CancellationToken.None));
            }),
            ("远端完成确认丢失只续验不自动撤销", async () =>
            {
                foreach (var publicVerificationFails in new[] { false, true })
                {
                    var actions = new List<string>();
                    var result = await RemotePublisher.RecoverAfterFailureAsync(
                        () => { actions.Add("status"); return Task.FromResult<string?>("complete"); },
                        () => { actions.Add("complete"); return Task.CompletedTask; },
                        () =>
                        {
                            actions.Add("verify");
                            return publicVerificationFails ? Task.FromException(new IOException("受控公网结果改变")) : Task.CompletedTask;
                        },
                        () => { actions.Add("rollback"); return Task.CompletedTask; },
                        () => { actions.Add("verify-rollback"); return Task.CompletedTask; });
                    Require(actions.SequenceEqual(new[] { "status", "complete", "verify" }), "complete确认丢失不能落入自动rollback");
                    Require(result.Kind == (publicVerificationFails ? RemotePublisher.PublicationRecovery.CompletedUnconfirmed :
                        RemotePublisher.PublicationRecovery.Completed), "完整事务只能确认完成或保留现状等待核对");
                }
            }),
            ("发布状态无法确认时保留现场不猜测回退", async () =>
            {
                Func<Task<string?>>[] statuses = [() => Task.FromException<string?>(new IOException("受控SSH不可达")),
                    () => Task.FromResult<string?>("recovery-required"), () => Task.FromResult<string?>("unknown"),
                    () => Task.FromResult<string?>(null)];
                foreach (var status in statuses)
                {
                    var operations = 0;
                    Task Unexpected() { operations++; return Task.CompletedTask; }
                    var result = await RemotePublisher.RecoverAfterFailureAsync(status, Unexpected, Unexpected, Unexpected, Unexpected);
                    Require(result.Kind == RemotePublisher.PublicationRecovery.RecoveryRequired && operations == 0,
                        "没有可信事务阶段时不得complete或rollback");
                }
            }),
            ("只对已确认未完成事务执行回退并验真", async () =>
            {
                foreach (var phase in new[] { "prepared", "acquiring", "begun", "package-installed", "switching-alias",
                             "alias-switched", "announcing", "announced", "rolling-back", "rolled-back", "not-started" })
                {
                    var actions = new List<string>();
                    var result = await RemotePublisher.RecoverAfterFailureAsync(() => Task.FromResult<string?>(phase),
                        () => { actions.Add("complete"); return Task.CompletedTask; },
                        () => { actions.Add("verify-complete"); return Task.CompletedTask; },
                        () => { actions.Add("rollback"); return Task.CompletedTask; },
                        () => { actions.Add("verify-rollback"); return Task.CompletedTask; });
                    if (phase == "not-started") Require(result.Kind == RemotePublisher.PublicationRecovery.NotStarted && actions.Count == 0,
                        "未发布事务不应回退公开入口");
                    else Require(result.Kind == RemotePublisher.PublicationRecovery.RolledBack &&
                        actions.SequenceEqual(new[] { "rollback", "verify-rollback" }), "未完成事务必须按回退后验真顺序收尾");
                }
            }),
            ("远端在状态读取后完成时自动恢复拒绝也不转人工回退", async () =>
            {
                var phase = "announced";
                var calls = new List<string>();
                var result = await RemotePublisher.RecoverAfterFailureAsync(
                    () =>
                    {
                        var stale = phase;
                        phase = "complete";
                        calls.Add("status-announced");
                        return Task.FromResult<string?>(stale);
                    },
                    () => { calls.Add("complete"); return Task.CompletedTask; },
                    () => { calls.Add("verify-complete"); return Task.CompletedTask; },
                    () =>
                    {
                        calls.Add("rollback-incomplete");
                        Require(phase == "complete", "测试必须覆盖远端在读状态后才完成");
                        return Task.FromException(new IOException("completed publication cannot be rolled back automatically"));
                    },
                    () => { calls.Add("verify-rollback"); return Task.CompletedTask; });
                Require(result.Kind == RemotePublisher.PublicationRecovery.RecoveryRequired &&
                    calls.SequenceEqual(new[] { "status-announced", "rollback-incomplete" }),
                    "服务端原子门禁拒绝后必须保留现状，不能再次通用rollback或伪报回退成功");
            }),
        };
        var failed = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + error.Message); }
        }
        Console.WriteLine($"RELEASE_RESULT total={tests.Length}, passed={tests.Length - failed}, failed={failed}");
        if (failed != 0) throw new InvalidOperationException("发布流水线受控检查未全部通过");
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> action)
        => new(new FakeHandler((request, token) => Task.FromResult(action(request, token))));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action)
        => new(new FakeHandler(action));
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => action(request, cancellationToken);
    }
    private sealed class BlockingBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static HttpResponseMessage Response(byte[] bytes, string cache)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Headers.CacheControl = CacheControlHeaderValue.Parse(cache);
        return response;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException or InvalidOperationException) { return; }
        throw new Exception("操作应该被拒绝");
    }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException or InvalidOperationException) { return; }
        throw new Exception("操作应该被拒绝");
    }
}
