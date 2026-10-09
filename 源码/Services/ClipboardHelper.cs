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
    public static string GetText()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var handle = GetClipboardData(CF_UNICODETEXT);
                    if (handle == IntPtr.Zero) return "";
                    var ptr = GlobalLock(handle);
                    if (ptr == IntPtr.Zero) return "";
                    try
                    {
                        return Marshal.PtrToStringUni(ptr) ?? "";
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(20); // 其他进程占用剪贴板时稍候重试
        }
        return "";
    }

    /// <summary>
    /// 清空旧剪贴板，使下一次读取只能接受本次 Ctrl+Alt+C 新写入的物品文本。
    /// 失败时返回 false，调用方必须停止本次判定，不能继续使用可能过期的内容。
    /// </summary>
    public static bool TryClear()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (EmptyClipboard()) return true;
                    Diag.Win32Error("EmptyClipboard", Marshal.GetLastWin32Error());
                    return false;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(20);
        }

        Diag.Win32Error("OpenClipboard(clear)", Marshal.GetLastWin32Error());
        return false;
    }

    /// <summary>仅接受 PoE 简繁中文或英文物品类别字段头，防止把其他应用文本送进词缀判定。</summary>
    public static bool IsItemText(string text)
    {
        var value = text.TrimStart();
        return Regex.IsMatch(value, @"^物品(?:[类類][别別]|種類)\s*[:：]") ||
               value.StartsWith("Item Class:", StringComparison.OrdinalIgnoreCase);
    }
}
