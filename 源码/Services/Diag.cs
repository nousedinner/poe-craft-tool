using System.Diagnostics;

namespace ShiKe.Services;

/// <summary>
/// 有界批量日志；正常容量压力背压，磁盘写入失败不影响业务。
/// </summary>
internal static class Diag
{
    private static readonly DiagnosticLogOwner Owner = new(() =>
        new BufferedDiagnosticLog(Path.Combine(AppContext.BaseDirectory, "data", "debug.log")));

    /// <summary>记录诊断日志。时间戳 + 消息。</summary>
    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);
        try
        {
            Owner.WriteLine(line);
        }
        catch (Exception error) { Debug.WriteLine($"[日志] 无法接收诊断消息：{error.Message}"); }
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
            Owner.ClearOldLog();
        }
        catch (Exception error) { Debug.WriteLine($"[日志] 无法清理旧日志：{error.Message}"); }
    }

    /// <summary>不创建日志。成功仅表示此前队列已处理且最近写入正常；历史失败另有计数。</summary>
    internal static bool Flush(TimeSpan timeout) => Owner.Flush(timeout);

    /// <summary>最终退出调用，不用于业务或更新放行；晚到消息不复活线程。</summary>
    internal static bool Shutdown(TimeSpan timeout) => Owner.Shutdown(timeout);

    internal static DiagnosticLogSnapshot? Snapshot() => Owner.Snapshot();
}

/// <summary>惰性生命周期可独立验证；助手/早退只调用关闭不会创建文件或工作线程。</summary>
internal sealed class DiagnosticLogOwner(Func<BufferedDiagnosticLog> create)
{
    private readonly object _gate = new();
    private BufferedDiagnosticLog? _log;
    private bool _shutdown;

    private BufferedDiagnosticLog? GetLog()
    {
        lock (_gate)
        {
            if (_shutdown) return _log;
            return _log ??= create();
        }
    }

    internal long WriteLine(string line)
    {
        var log = GetLog();
        if (log is not null) return log.WriteLine(line);
        Debug.WriteLine("[日志] 最终关闭后的消息仅保留在调试输出。");
        return 0;
    }

    internal void ClearOldLog() => GetLog()?.ClearOldLog();

    internal bool Flush(TimeSpan timeout)
    {
        BufferedDiagnosticLog? log;
        lock (_gate) log = _log;
        return log?.Flush(timeout) ?? true;
    }

    internal bool Shutdown(TimeSpan timeout)
    {
        BufferedDiagnosticLog? log;
        lock (_gate) { _shutdown = true; log = _log; }
        return log?.Shutdown(timeout) ?? true;
    }

    internal DiagnosticLogSnapshot? Snapshot()
    {
        lock (_gate) return _log?.Snapshot();
    }
}
