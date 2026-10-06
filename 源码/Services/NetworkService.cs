using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShiKe.Services;

/// <summary>广告条目（对齐 Python 版 ads.json 数据结构）。</summary>
public sealed class AdItem
{
    public string Location { get; init; } = "";  // "top" | "bottom"
    public string Type { get; init; } = "";      // "text"
    public string Text { get; init; } = "";
    public string? Style { get; init; }
    public string? Link { get; init; }
}

/// <summary>版本检查结果。</summary>
public sealed record VersionCheckResult(string Latest, string DownloadUrl);

/// <summary>
/// 网络服务（对齐 Python 版 main.py _send_daily_ping）：
/// - 版本检查：GET version.json → 新版返回 (latest, url)，宿主强制更新弹窗
/// - 签到上报：POST api/send —— payload 为 Python 实际发送的嵌套格式
///   {type:"event", payload:{website,url,hostname:"拾刻",language, screen:"1920x1080", title:"拾刻启动", event:"pageview"}}
///   ⚠️ 勿改成 ARCHITECTURE 早期写的 Umami 平铺格式（文档错误，以源码为准）
/// - 广告拉取：GET ads.json → {ads:[{location,type,text,style,link}]}
/// 全部 5s 超时 + 静默失败，不影响启动。
/// </summary>
public sealed class NetworkService : IDisposable
{
    private static readonly Uri BaseUri = new("https://open.cancanneed.top");
    internal const int MaxResponseBytes = 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private bool _disposed;

    public NetworkService() : this(new HttpClientHandler(), Diag.Log) { }

    // 验证可以注入虚拟 HTTP 处理器与日志，不接触真实服务或用户日志。
    internal NetworkService(HttpMessageHandler handler, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(log);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 poe-craft-tool/{CurrentVersion.ToString(3)}");
        _log = log;
    }

    /// <summary>当前程序版本（csproj Version 单一来源）。</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    /// <summary>版本检查。null = 已最新或请求失败；非 null = 需更新（latest, url）。</summary>
    public async Task<VersionCheckResult?> CheckVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await ReadJsonTextAsync("/version.json", cancellationToken).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("版本数据顶层必须是对象");
            var latest = ReadString(doc.RootElement, "latest");
            if (!Version.TryParse(latest, out var version))
                throw new InvalidDataException("版本数据缺少有效的 latest 版本号");
            // System.Version 比较（Python 字符串比较在 "1.0.10" 场景会误判强制更新，C# 修复）
            if (version > CurrentVersion)
            {
                if (!BrowserLauncher.TryNormalize(ReadString(doc.RootElement, "url"), out var url))
                    throw new InvalidDataException("新版缺少有效下载地址，已跳过强制更新");
                return new VersionCheckResult(latest!, url);
            }
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogFailure("版本检查", ex);
            return null; // 网络失败静默跳过（Python 行为）
        }
    }

    /// <summary>签到上报（pageview），静默失败。</summary>
    public async Task SendPingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var content = new StringContent(CreatePingPayload().ToJsonString(), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "/api/send")) { Content = content };
            using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogFailure("启动统计", ex); // 不弹窗，不增加任何上报字段。
        }
    }

    internal static bool NeedsUpdate(string? latest, Version current)
        => Version.TryParse(latest, out var latestVersion) && latestVersion > current;

    /// <summary>固定匿名 pageview 载荷；不得加入机器名、账号、真实屏幕、游戏或剪贴板数据。</summary>
    internal static JsonObject CreatePingPayload() => new()
    {
        ["type"] = "event",
        ["payload"] = new JsonObject
        {
            ["website"] = "0c4520ad-ae66-4453-a901-7bdfef3c4b44",
            ["url"] = "/app/poe-craft-tool",
            ["hostname"] = "拾刻",
            ["language"] = "zh-CN",
            ["screen"] = "1920x1080",
            ["title"] = "拾刻启动",
            ["event"] = "pageview",
        },
    };

    /// <summary>拉取广告。失败返回空列表（UI 显示"广告位招租"占位）。</summary>
    public async Task<List<AdItem>> FetchAdsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await ReadJsonTextAsync("/ads.json", cancellationToken).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("广告数据顶层必须是对象");
            var ads = new List<AdItem>();
            if (doc.RootElement.TryGetProperty("ads", out var arr))
            {
                if (arr.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("ads 必须是数组");
                var skipped = 0;
                foreach (var el in arr.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (el.ValueKind != JsonValueKind.Object) { skipped++; continue; }
                    var location = ReadString(el, "location");
                    var type = ReadString(el, "type");
                    var text = ReadString(el, "text");
                    if (location is not ("top" or "bottom") || type != "text" || string.IsNullOrWhiteSpace(text))
                    { skipped++; continue; }
                    var link = ReadString(el, "link");
                    if (!string.IsNullOrWhiteSpace(link) && !BrowserLauncher.TryNormalize(link, out link))
                    { link = null; skipped++; }
                    ads.Add(new AdItem
                    {
                        Location = location,
                        Type = type,
                        Text = text,
                        Style = ReadString(el, "style"),
                        Link = link,
                    });
                }
                if (skipped > 0) _log($"[网络] 广告包含 {skipped} 项无效条目或链接，已忽略无效部分");
            }
            return ads;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogFailure("广告获取", ex);
            return [];
        }
    }

    private static string? ReadString(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task<string> ReadJsonTextAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        // ResponseHeadersRead 不包含后续读取的 HttpClient 超时；将 5 秒期限覆盖整个响应。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, path));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException("服务响应超过 1MB，已忽略异常数据");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidDataException("服务响应超过 1MB，已忽略异常数据");
            buffer.Write(bytes, 0, read);
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private void LogFailure(string operation, Exception error)
    {
        var reason = error is OperationCanceledException ? "请求超过 5 秒，已跳过" : error.Message;
        _log($"[网络] {operation}失败: {error.GetType().Name}: {reason}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
