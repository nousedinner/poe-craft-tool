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
public sealed class NetworkService
{
    private static readonly Uri BaseUri = new("https://open.cancanneed.top");

    private readonly HttpClient _http;

    public NetworkService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 poe-craft-tool/1.0");
    }

    /// <summary>当前程序版本（csproj Version 单一来源）。</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    /// <summary>版本检查。null = 已最新或请求失败；非 null = 需更新（latest, url）。</summary>
    public async Task<VersionCheckResult?> CheckVersionAsync()
    {
        try
        {
            var resp = await _http.GetStringAsync(new Uri(BaseUri, "/version.json"));
            using var doc = JsonDocument.Parse(resp);
            var latest = doc.RootElement.TryGetProperty("latest", out var l) ? l.GetString() : null;
            var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(latest))
                return null;
            // System.Version 比较（Python 字符串比较在 "1.0.10" 场景会误判强制更新，C# 修复）
            if (NeedsUpdate(latest, CurrentVersion))
                return new VersionCheckResult(latest, url);
            return null;
        }
        catch (Exception)
        {
            return null; // 网络失败静默跳过（Python 行为）
        }
    }

    /// <summary>签到上报（pageview），静默失败。</summary>
    public async Task SendPingAsync()
    {
        try
        {
            var payload = CreatePingPayload();
            var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(new Uri(BaseUri, "/api/send"), content);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception)
        {
            // 静默失败
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
    public async Task<List<AdItem>> FetchAdsAsync()
    {
        try
        {
            var resp = await _http.GetStringAsync(new Uri(BaseUri, "/ads.json"));
            using var doc = JsonDocument.Parse(resp);
            var ads = new List<AdItem>();
            if (doc.RootElement.TryGetProperty("ads", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    ads.Add(new AdItem
                    {
                        Location = el.TryGetProperty("location", out var loc) ? loc.GetString() ?? "" : "",
                        Type = el.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                        Text = el.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "",
                        Style = el.TryGetProperty("style", out var st) ? st.GetString() : null,
                        Link = el.TryGetProperty("link", out var ln) ? ln.GetString() : null,
                    });
                }
            }
            return ads;
        }
        catch (Exception)
        {
            return [];
        }
    }
}
