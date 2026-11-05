using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ShiKe.Services;

/// <summary>
/// 剪贴板读取（对齐 Python pyperclip.paste）。
/// 用 Win32 OpenClipboard/GetClipboardData 而非 WPF Clipboard：
/// 洗装循环在 Task.Run 后台线程（MTA），WPF System.Windows.Clipboard 要求 STA。
/// </summary>
public static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private static readonly Regex ChineseItemHeader = new(@"^物品(?:[类類][别別]|種類)\s*[:：]");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint hMem);

    /// <summary>读取剪贴板文本。占用冲突重试 5 次，失败/空返回 ""（对齐 pyperclip.paste 异常兜底）。</summary>
    public static string GetText() => GetText(CancellationToken.None);

    public static string GetText(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Updates.UpdateAcceptanceContext.ForbidNativeOperation("OpenClipboard(read)");
        return WithOpenClipboard(static () => OpenClipboard(IntPtr.Zero), ReadOpenedText,
            static () => CloseClipboard(), string.Empty, token);
    }

    private static string ReadOpenedText()
    {
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return "";
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(ptr) ?? ""; }
        finally { GlobalUnlock(handle); }
    }

    // 原生访问与重试策略分离，使取消、失败和清理可在不触碰玩家剪贴板的情况下验证。
    internal static T WithOpenClipboard<T>(Func<bool> open, Func<T> action, Action close,
        T failure, CancellationToken token, Action<int, CancellationToken>? wait = null)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (open())
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var result = action();
                    token.ThrowIfCancellationRequested();
                    return result;
                }
                finally { close(); }
            }
            (wait ?? WaitForRetry)(20, token);
        }
        token.ThrowIfCancellationRequested();
        return failure;
    }

    internal static void WaitForRetry(int milliseconds, CancellationToken token)
    {
        if (token.CanBeCanceled) token.WaitHandle.WaitOne(milliseconds);
        else Thread.Sleep(milliseconds);
        token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// 清空旧剪贴板，使下一次读取只能接受本次 Ctrl+C 新写入的物品文本。
    /// 失败时返回 false，调用方必须停止本次判定，不能继续使用可能过期的内容。
    /// </summary>
    public static bool TryClear() => TryClear(CancellationToken.None);

    public static bool TryClear(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Updates.UpdateAcceptanceContext.ForbidNativeOperation("OpenClipboard(clear)");
        var opened = false;
        var lastError = 0;
        var result = WithOpenClipboard(() =>
        {
            opened = OpenClipboard(IntPtr.Zero);
            if (!opened) lastError = Marshal.GetLastWin32Error();
            return opened;
        }, static () =>
        {
            if (EmptyClipboard()) return true;
            Diag.Win32Error("EmptyClipboard", Marshal.GetLastWin32Error());
            return false;
        }, static () => CloseClipboard(), false, token);
        if (!opened) Diag.Win32Error("OpenClipboard(clear)", lastError);
        return result;
    }

    /// <summary>仅接受 PoE 简繁中文或英文物品类别字段头，防止把其他应用文本送进词缀判定。</summary>
    public static bool IsItemText(string text)
    {
        var value = text.TrimStart();
        return ChineseItemHeader.IsMatch(value) ||
               value.StartsWith("Item Class:", StringComparison.OrdinalIgnoreCase);
    }
}
