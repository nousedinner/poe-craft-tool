using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShiKe.Services.Updates;

/// <summary>只有通过内置信任根验证的发行数据才能成为此类型；属性不允许外部改写。</summary>
public sealed record UpdateManifest
{
    public const int UpdaterProtocolVersion = 1;
    public const int CurrentRuntimeMajor = 10;
    public const int CurrentConfigSchema = 1;
    public const string CurrentPlatform = "win-x64";
    public const long MaximumArtifactBytes = 128L * 1024 * 1024;
    public const string OfficialManualUrl = "https://open.cancanneed.top/download/shike-latest.zip";
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumPayloadBytes = 64 * 1024;
    private static readonly Regex VersionPattern = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex PackageNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.zip$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private UpdateManifest(int schema, Version version, string manualUrl, string mode, int minUpdaterVersion,
        Version minAppVersion, int runtimeMajor, string platform, int configSchema, string packageUrl,
        long packageSize, string packageSha256, long exeSize, string exeSha256, IReadOnlyList<UpdateFileEntry> files,
        string keyId, string sourceJson)
    {
        Schema = schema;
        Version = version;
        ManualUrl = manualUrl;
        Mode = mode;
        MinUpdaterVersion = minUpdaterVersion;
        MinAppVersion = minAppVersion;
        RuntimeMajor = runtimeMajor;
        Platform = platform;
        ConfigSchema = configSchema;
        PackageUrl = packageUrl;
        PackageSize = packageSize;
        PackageSha256 = packageSha256;
        ExeSize = exeSize;
        ExeSha256 = exeSha256;
        Files = files;
        KeyId = keyId;
        SourceJson = sourceJson;
    }

    public int Schema { get; }
    public Version Version { get; }
    public string VersionText => Version.ToString(3);
    public string ManualUrl { get; }
    public string Mode { get; }
    public int MinUpdaterVersion { get; }
    public Version MinAppVersion { get; }
    public int RuntimeMajor { get; }
    public string Platform { get; }
    public int ConfigSchema { get; }
    public string PackageUrl { get; }
    public Uri PackageUri => new(PackageUrl, UriKind.Absolute);
    public long PackageSize { get; }
    public string PackageSha256 { get; }
    public long ExeSize { get; }
    public string ExeSha256 { get; }
    public IReadOnlyList<UpdateFileEntry> Files { get; }
    public string KeyId { get; }
    public string SourceJson { get; }

    /// <summary>只允许兼容的前进升级。已安装版本的第四段程序集修订号不改变发行版本语义。</summary>
    public bool CanApply(Version current, int updaterVersion = UpdaterProtocolVersion)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Build < 0 || updaterVersion < 1) return false;
        var installed = new Version(current.Major, current.Minor, current.Build);
        return Version > installed && installed >= MinAppVersion && (Mode is "exe-only" or "files") &&
               MinUpdaterVersion <= updaterVersion && RuntimeMajor == CurrentRuntimeMajor &&
               Platform == CurrentPlatform && ConfigSchema == CurrentConfigSchema;
    }

    /// <summary>先验证原始 payload 字节的签名，再读取、信任下载地址与发行约束。</summary>
    public static UpdateManifest Verify(string json, string publicKeyPem, string expectedKeyId)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem) || string.IsNullOrWhiteSpace(expectedKeyId))
            throw new InvalidDataException("尚未配置可信发行公钥，自动更新已停用");
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
            throw new InvalidDataException("更新清单为空或超过大小上限");
        try
        {
            using var envelopeDocument = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var envelope = ObjectProperties(envelopeDocument.RootElement, "更新清单");
            var signed = ObjectProperties(Required(envelope, "autoUpdate"), "自动更新签名");
            ExactFields(signed, "keyId", "payload", "signature");
            var keyId = StringValue(signed, "keyId", 80);
            if (!string.Equals(keyId, expectedKeyId, StringComparison.Ordinal))
                throw new InvalidDataException("更新清单使用未知发行密钥");
            var payload = Convert.FromBase64String(StringValue(signed, "payload", MaximumPayloadBytes * 2));
            var signature = Convert.FromBase64String(StringValue(signed, "signature", 2048));
            if (payload.Length is 0 or > MaximumPayloadBytes || signature.Length is < 256 or > 1024)
                throw new InvalidDataException("更新签名或载荷大小无效");
            using (var rsa = RSA.Create())
            {
                rsa.ImportFromPem(publicKeyPem);
                if (rsa.KeySize < 2048 || !rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                    throw new InvalidDataException("更新清单签名验证失败");
            }

            // JsonDocument 直接解析已验证的 UTF-8 字节，不重新序列化或规范化签名内容。
            using var payloadDocument = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            var values = ObjectProperties(payloadDocument.RootElement, "发行载荷");
            ExactFields(values, "schema", "version", "manualUrl", "mode", "minUpdaterVersion", "minAppVersion",
                "runtimeMajor", "platform", "configSchema", "packageUrl", "packageSize", "packageSha256", "exeSize", "exeSha256", "files");
            var schema = PositiveInt(values, "schema");
            if (schema != 1) throw new InvalidDataException("不支持此更新清单格式");
            var versionText = StringValue(values, "version", 32);
            var version = ParseVersion(versionText);
            var manualUrl = StringValue(values, "manualUrl", 512);
            if (manualUrl != OfficialManualUrl) throw new InvalidDataException("完整下载入口不是官方固定地址");
            if (StringValue(envelope, "latest", 32) != versionText || StringValue(envelope, "url", 512) != manualUrl)
                throw new InvalidDataException("旧客户端字段与签名发行数据不一致");
            var mode = StringValue(values, "mode", 16);
            if (mode is not ("exe-only" or "files")) throw new InvalidDataException("未知更新类型");
            var minUpdaterVersion = PositiveInt(values, "minUpdaterVersion");
            var minAppVersion = ParseVersion(StringValue(values, "minAppVersion", 32));
            if (minAppVersion > version) throw new InvalidDataException("最低适用版本高于目标版本");
            var runtimeMajor = PositiveInt(values, "runtimeMajor");
            var configSchema = PositiveInt(values, "configSchema");
            var platform = StringValue(values, "platform", 32);
            if (!Regex.IsMatch(platform, @"\A[a-z0-9-]+\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("平台标识无效");
            var packageUrl = StringValue(values, "packageUrl", 2048);
            ValidatePackageUrl(packageUrl, versionText);
            var exeSize = ArtifactSize(values, "exeSize");
            var exeSha256 = HashValue(values, "exeSha256");
            var files = ReadFiles(Required(values, "files"));
            var main = files.SingleOrDefault(file => file.Path == "拾刻.exe");
            if (main is null || main.AllowCreate || main.Size != exeSize || main.Sha256 != exeSha256 ||
                mode == "exe-only" && files.Count != 1)
                throw new InvalidDataException("文件清单与主程序信息或更新类型不一致");
            return new UpdateManifest(schema, version, manualUrl, mode, minUpdaterVersion, minAppVersion,
                runtimeMajor, platform, configSchema, packageUrl, ArtifactSize(values, "packageSize"),
                HashValue(values, "packageSha256"), exeSize, exeSha256, files,
                keyId, json);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is JsonException or FormatException or CryptographicException or
                                          ArgumentException or OverflowException)
        {
            throw new InvalidDataException("更新清单格式或签名无效", error);
        }
    }

    private static IReadOnlyList<UpdateFileEntry> ReadFiles(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 64)
            throw new InvalidDataException("更新文件清单必须包含 1～64 个条目");
        var files = new List<UpdateFileEntry>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var element in array.EnumerateArray())
        {
            var values = ObjectProperties(element, "更新文件");
            ExactFields(values, "path", "size", "sha256", "baseSha256", "allowCreate");
            var path = StringValue(values, "path", 160);
            ValidateTargetPath(path);
            if (!paths.Add(path)) throw new InvalidDataException("更新文件路径重复或存在大小写冲突");
            var size = ArtifactSize(values, "size");
            totalBytes += size;
            if (totalBytes > MaximumArtifactBytes) throw new InvalidDataException("更新文件合计大小超过上限");
            var bases = Required(values, "baseSha256");
            if (bases.ValueKind != JsonValueKind.Array || bases.GetArrayLength() > 32)
                throw new InvalidDataException("旧资源哈希清单无效");
            var baseHashes = new List<string>();
            foreach (var hash in bases.EnumerateArray())
            {
                if (hash.ValueKind != JsonValueKind.String) throw new InvalidDataException("旧资源哈希必须是字符串");
                var normalized = NormalizeHash(hash.GetString()!);
                if (baseHashes.Contains(normalized, StringComparer.Ordinal)) throw new InvalidDataException("旧资源哈希重复");
                baseHashes.Add(normalized);
            }
            var allowCreate = Required(values, "allowCreate");
            if (allowCreate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("新增文件规则必须为布尔值");
            files.Add(new UpdateFileEntry(path, size, HashValue(values, "sha256"),
                baseHashes.AsReadOnly(), allowCreate.GetBoolean()));
        }
        return files.AsReadOnly();
    }

    /// <summary>首版只开放主程序、根 DLL 和 sounds 音频；data 与个人配置没有可写入口。</summary>
    public static void ValidateTargetPath(string path)
    {
        if (path == "拾刻.exe") return;
        if (Regex.IsMatch(path, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.dll$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) &&
            IsSafeFileName(path)) return;
        if (path.StartsWith("sounds/", StringComparison.Ordinal) && IsSafeFileName(path[7..]) &&
            new[] { ".mp3", ".wav", ".ogg", ".flac" }.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return;
        throw new InvalidDataException("更新目标路径不在允许的程序与内置音频范围内");
    }

    internal static bool IsSafeFileName(string name)
    {
        if (name.Length is < 1 or > 128 || name != name.Trim() || name.EndsWith('.') ||
            name.Any(character => char.IsControl(character) || "\\/:*?\"<>|".Contains(character)) || name is "." or "..") return false;
        var stem = name.Split('.')[0];
        return !Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static void ValidatePackageUrl(string url, string version)
    {
        if (url != url.Trim() || url.Any(char.IsControl) || url.Contains('\\') ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.IdnHost, "open.cancanneed.top", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0)
            throw new InvalidDataException("更新包地址必须是官方 HTTPS 版本化地址");
        var prefix = "/download/releases/" + version + "/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) ||
            !PackageNamePattern.IsMatch(uri.AbsolutePath[prefix.Length..]))
            throw new InvalidDataException("更新包必须位于该版本的固定 ZIP 路径");
    }

    private static Version ParseVersion(string text)
    {
        if (!VersionPattern.IsMatch(text) || !System.Version.TryParse(text, out var version))
            throw new InvalidDataException("发行版本必须为规范的三段数字");
        return version;
    }

    private static Dictionary<string, JsonElement> ObjectProperties(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException(name + "必须是对象");
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!properties.TryAdd(property.Name, property.Value)) throw new InvalidDataException(name + "包含重复字段");
        return properties;
    }

    private static void ExactFields(Dictionary<string, JsonElement> values, params string[] names)
    {
        if (values.Count != names.Length || names.Any(name => !values.ContainsKey(name)))
            throw new InvalidDataException("更新清单缺少字段或包含未知字段");
    }

    private static JsonElement Required(Dictionary<string, JsonElement> values, string name)
        => values.TryGetValue(name, out var value) ? value : throw new InvalidDataException("更新清单缺少 " + name);

    private static string StringValue(Dictionary<string, JsonElement> values, string name, int maximumLength)
    {
        var value = Required(values, name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text || text.Length > maximumLength)
            throw new InvalidDataException("更新字段 " + name + " 必须是有效字符串");
        return text;
    }

    private static int PositiveInt(Dictionary<string, JsonElement> values, string name)
    {
        var value = Required(values, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 1)
            throw new InvalidDataException("更新字段 " + name + " 必须是正整数");
        return number;
    }

    private static long ArtifactSize(Dictionary<string, JsonElement> values, string name)
    {
        var value = Required(values, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) ||
            number is < 1 or > MaximumArtifactBytes)
            throw new InvalidDataException("更新文件大小超出允许范围");
        return number;
    }

    private static string HashValue(Dictionary<string, JsonElement> values, string name)
    {
        var value = StringValue(values, name, 64);
        return NormalizeHash(value);
    }

    private static string NormalizeHash(string value)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("更新文件 SHA-256 无效");
        return value.ToUpperInvariant();
    }
}

public sealed record UpdateFileEntry
{
    internal UpdateFileEntry(string path, long size, string sha256, IReadOnlyList<string> baseSha256, bool allowCreate)
    {
        Path = path;
        Size = size;
        Sha256 = sha256;
        BaseSha256 = baseSha256;
        AllowCreate = allowCreate;
    }

    public string Path { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public IReadOnlyList<string> BaseSha256 { get; }
    public bool AllowCreate { get; }
}
