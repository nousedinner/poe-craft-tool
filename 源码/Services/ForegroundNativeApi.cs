using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShiKe.Services;

internal interface IForegroundNativeApi
{
    nint GetForegroundWindow();
    uint GetWindowProcessId(nint window);
    IForegroundProcessHandle? OpenProcess(uint processId, bool verifyLifetime);
    string? GetManagedProcessName(uint processId);
}

internal interface IForegroundProcessHandle : IDisposable
{
    string? ReadImageName();
    bool IsAlive();
}

internal sealed class ForegroundNativeApi : IForegroundNativeApi
{
    internal static readonly ForegroundNativeApi Instance = new();
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    nint IForegroundNativeApi.GetForegroundWindow() => GetForegroundWindow();

    uint IForegroundNativeApi.GetWindowProcessId(nint window)
        => GetWindowThreadProcessId(window, out var processId) == 0 ? 0 : processId;

    public IForegroundProcessHandle? OpenProcess(uint processId, bool verifyLifetime)
    {
        var handle = OpenProcess(QueryLimitedInformation | (verifyLifetime ? Synchronize : 0), false, processId);
        if (!handle.IsInvalid) return new ProcessHandle(handle, processId);
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        // SYNCHRONIZE 不可用只是不能启用缓存，后续仍会走原权限的查询路径。
        // 不逐次记录这个可恢复的优化探测，否则受限进程会额外产生高频日志。
        if (!verifyLifetime) Diag.Win32Error($"OpenProcess(pid={processId})", error);
        return null;
    }

    public string? GetManagedProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        }
        catch (Exception error)
        {
            Diag.Log($"[前台] 托管 API 失败: pid={processId}, {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    private sealed class ProcessHandle(SafeProcessHandle handle, uint processId) : IForegroundProcessHandle
    {
        public string? ReadImageName()
        {
            // 保留旧260字符查询范围，失败仍走托管兜底；避免逐次分配路径缓冲和完整路径字符串。
            var buffer = ArrayPool<char>.Shared.Rent(260);
            try
            {
                uint size = 260;
                if (QueryFullProcessImageNameW(handle, 0, buffer, ref size))
                    return Path.GetFileName(buffer.AsSpan(0, checked((int)size))).ToString();
                Diag.Win32Error($"QueryFullProcessImageNameW(pid={processId})", Marshal.GetLastWin32Error());
                return null;
            }
            finally { ArrayPool<char>.Shared.Return(buffer); }
        }

        public bool IsAlive()
        {
            var result = WaitForSingleObject(handle, 0);
            if (result == 0x00000102) return true; // WAIT_TIMEOUT: 进程尚未退出。
            if (result == uint.MaxValue)
                Diag.Win32Error($"WaitForSingleObject(pid={processId})", Marshal.GetLastWin32Error());
            return false; // 已退出、等待失败和任何意外结果均不允许缓存命中。
        }

        public void Dispose() => handle.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags,
        [Out] char[] buffer, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
