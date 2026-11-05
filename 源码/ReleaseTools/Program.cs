using System.Text;
using System.Text.Json;
using ShiKe.ReleaseTools;
using ShiKe.Services.Updates;

Console.OutputEncoding = new UTF8Encoding(false);
try
{
    if (args.Length == 0) throw new ArgumentException("需要命令：init-key、prepare、verify、publish、rollback 或 self-test");
    var options = Arguments.Parse(args.Skip(1).ToArray());
    switch (args[0])
    {
        case "init-key":
        {
            options.Only("key-path");
            var keyPath = Path.GetFullPath(options.Required("key-path"));
            var publicPath = ReleaseKeyStore.Initialize(keyPath);
            Print(new { status = "created", privateKeyPath = keyPath, protection = "Windows DPAPI CurrentUser", publicKeyPath = publicPath });
            break;
        }
        case "prepare":
        {
            options.Only("zip", "exe", "version", "key-path", "key-id", "output", "mode", "min-app-version", "min-updater-version", "runtime-major", "platform", "config-schema", "managed-files", "base-packages", "base-manifests", "notes");
            var version = options.Required("version");
            ReleaseNotesArtifact.Validate(options.Required("notes"), options.Required("exe"), version);
            var mode = options.Get("mode", "exe-only");
            var artifact = ReleasePreparation.Inspect(options.Required("zip"), options.Required("exe"), version,
                mode, options.Optional("managed-files"), options.Optional("base-packages"), options.Optional("base-manifests"));
            var keyId = options.Get("key-id", ReleaseTrust.KeyId);
            if (keyId != ReleaseTrust.KeyId) throw new InvalidDataException("keyId 必须与客户端固定值一致");
            using var key = ReleaseKeyStore.Open(options.Required("key-path"), ReleaseTrust.PublicKeyPem);
            var manifest = ReleasePreparation.CreateManifest(artifact, key, keyId, mode,
                options.Get("min-app-version", "1.0.52"), options.Int("min-updater-version", 1),
                options.Int("runtime-major", 10), options.Get("platform", "win-x64"), options.Int("config-schema", 1));
            var output = Path.GetFullPath(options.Required("output"));
            Directory.CreateDirectory(output);
            var manifestPath = Path.Combine(output, "version.json");
            if (File.Exists(manifestPath))
            {
                var previous = File.ReadAllText(manifestPath);
                UpdateManifest.Verify(previous, ReleaseTrust.PublicKeyPem, keyId);
                using var previousJson = JsonDocument.Parse(previous);
                using var candidateJson = JsonDocument.Parse(manifest);
                if (previousJson.RootElement.GetProperty("autoUpdate").GetProperty("payload").GetString() !=
                    candidateJson.RootElement.GetProperty("autoUpdate").GetProperty("payload").GetString())
                    throw new IOException("同一发布输出已有不同清单，拒绝改写；修改内容必须使用新版本");
                manifest = previous; // PSS签名随机；相同payload复用已有签名，使准备步骤可重复。
            }
            var preparedZip = Path.Combine(output, "shike-win-x64.zip");
            if (File.Exists(preparedZip))
            {
                if (ReleasePreparation.HashFile(preparedZip) != artifact.PackageSha256)
                    throw new IOException("输出目录已有不同发布包，拒绝覆盖");
            }
            else File.Copy(artifact.ZipPath, preparedZip, overwrite: false);
            ReleasePreparation.WriteNewOrIdentical(manifestPath, manifest);
            Print(new { status = "prepared", version, mode, package = preparedZip, manifest = manifestPath,
                packageSize = artifact.PackageSize, packageSha256 = artifact.PackageSha256,
                files = artifact.Files, keyId, published = false });
            break;
        }
        case "verify":
        {
            options.Only("manifest", "zip");
            var manifest = UpdateManifest.Verify(File.ReadAllText(options.Required("manifest")), ReleaseTrust.PublicKeyPem, ReleaseTrust.KeyId);
            ReleasePreparation.VerifyPackage(options.Required("zip"), manifest);
            Print(new { status = "verified", version = manifest.VersionText, published = false });
            break;
        }
        case "publish":
        case "rollback":
        {
            options.Only("manifest", "zip", "host", "remote-root", "execute", "zone-id", "token-env", "transaction", "cache-strategy");
            var manifestPath = Path.GetFullPath(options.Required("manifest"));
            var manifest = UpdateManifest.Verify(File.ReadAllText(manifestPath), ReleaseTrust.PublicKeyPem, ReleaseTrust.KeyId);
            var zipPath = Path.GetFullPath(options.Required("zip"));
            ReleasePreparation.VerifyPackage(zipPath, manifest);
            await RemotePublisher.RunAsync(manifest, manifestPath, zipPath, options.Get("host", "myserver"),
                options.Get("remote-root", "/var/www/poead"), options.Has("execute"), args[0] == "rollback",
                options.Optional("transaction"), options.Optional("zone-id"), options.Get("token-env", "SHIKE_CF_API_TOKEN"),
                options.Get("cache-strategy", "headers"));
            break;
        }
        case "self-test":
            options.Only();
            await ReleaseSelfTests.RunAsync();
            break;
        default: throw new ArgumentException("未知发布命令");
    }
    return 0;
}
catch (Exception error)
{
    // 私钥及HTTP授权头不进入日志；仅显示经工具控制的错误说明。
    Console.Error.WriteLine("发布工具未完成：" + error.Message);
    return 1;
}

static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

internal sealed class Arguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    internal static Arguments Parse(string[] values)
    {
        var result = new Arguments();
        for (var index = 0; index < values.Length; index++)
        {
            var key = values[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) || key.Length < 3) throw new ArgumentException("参数必须使用 --name value");
            key = key[2..];
            var value = key == "execute" ? "true" : ++index < values.Length ? values[index] : throw new ArgumentException("参数缺少值：" + key);
            if (!result._values.TryAdd(key, value)) throw new ArgumentException("参数重复：" + key);
        }
        return result;
    }
    internal void Only(params string[] names)
    {
        if (_values.Keys.Any(key => !names.Contains(key, StringComparer.Ordinal))) throw new ArgumentException("命令包含不支持的参数");
    }
    internal string Required(string name) => _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException("缺少参数 --" + name);
    internal string? Optional(string name) => _values.GetValueOrDefault(name);
    internal string Get(string name, string fallback) => Optional(name) ?? fallback;
    internal int Int(string name, int fallback) => Optional(name) is { } value
        ? int.TryParse(value, out var number) ? number : throw new ArgumentException("参数必须为整数：" + name) : fallback;
    internal bool Has(string name) => _values.ContainsKey(name);
}
