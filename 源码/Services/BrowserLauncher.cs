using System.Diagnostics;

namespace ShiKe.Services;

/// <summary>软件网页入口统一接受 HTTP(S) 地址，拒绝文件、命令与带凭据的地址。</summary>
public static class BrowserLauncher
{
    internal static bool TryNormalize(string? value, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        url = uri.AbsoluteUri;
        return true;
    }

    public static bool TryOpen(string? value, out string error)
    {
        if (!TryNormalize(value, out var url))
        {
            error = "网页地址无效，请使用完整的 http 或 https 地址";
            Diag.Log("[网页] 拒绝无效网页地址");
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"无法打开浏览器：{ex.Message}";
            Diag.Log($"[网页] {error}");
            return false;
        }
    }
}
