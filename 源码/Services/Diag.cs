using System.Diagnostics;
using System.IO;

namespace ShiKe.Services;

/// <summary>
/// 诊断日志（临时，问题定位后移除）。
/// 写入 data/debug.log + Debug.WriteLine（DebugView 可捕获）。
/// 线程安全，所有写入用 lock 保护。
/// </summary>
internal static class Diag
{
    private static readonly object Lock = new();
    private static string? _logPath;

    /// <summary>初始化日志路径（首次调用时创建目录）。</summary>
    private static string LogPath
    {
        get
        {
            if (_logPath is null)
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "data");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "debug.log");
            }
            return _logPath;
        }
    }

    /// <summary>记录诊断日志。时间戳 + 消息。</summary>
    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);
        lock (Lock)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { /* 日志写入失败不能影响主逻辑 */ }
        }
    }

    /// <summary>记录 Win32 API 错误（自动获取 Marshal.GetLastWin32Error）。</summary>
    public static void Win32Error(string api, int errorCode)
    {
        Log($"[Win32] {api} 失败, errorCode={errorCode} (0x{errorCode:X8})");
    }

    /// <summary>清除旧日志（启动时调用，避免日志无限增长）。</summary>
    public static void ClearOldLog()
    {
        try
        {
            var path = LogPath;
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) // >512KB 清除
                File.WriteAllText(path, $"[{DateTime.Now}] === 日志已截断 ==={Environment.NewLine}");
        }
        catch { }
    }
}
