using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShiKe.Services;

/// <summary>
/// 前台进程检测（对齐 Python 版 foreground.py，三层覆盖见方案 §4.3）。
/// 1. 热键回调：启动类热键检查（停止类不检查）
/// 2. 运行中循环：每次操作前检查，非目标进程时暂停
/// 3. 设置页：进程下拉框 + 刷新
/// </summary>
public sealed class ForegroundDetector
{
    /// <summary>目标进程名（带 .exe，如 "PathOfExile.exe"）。空 = 不检测。</summary>
    public string? TargetProcess { get; set; }

    /// <summary>自动检测的 5 个 POE 进程变体（方案 §8）。</summary>
    public static readonly string[] PoeVariants =
    [
        "PathOfExile.exe", "PathOfExile_x64.exe", "PathOfExile",
        "PathOfExileSteam.exe", "PathOfExile_KG.exe"
    ];

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(nint hProcess, uint flags, char[] buffer, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>前台窗口所属进程名（含 .exe）。获取失败返回 null。</summary>
    public static string? GetForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == 0)
            {
                Diag.Log("[前台] GetForegroundWindow 返回 0（无前台窗口）");
                return null;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                Diag.Log($"[前台] GetWindowThreadProcessId 返回 pid=0, hwnd=0x{hwnd:X}");
                return null;
            }

            // ── 方法1：P/Invoke QueryFullProcessImageNameW ──
            var result = TryGetImageNameViaWin32(pid);
            if (result is not null) return result;

            // ── 方法2：托管 API 兜底（Process.GetProcessById）──
            result = TryGetImageNameViaManaged(pid);
            if (result is not null)
            {
                Diag.Log($"[前台] Win32 失败，托管 API 成功: pid={pid}, name={result}");
                return result;
            }

            Diag.Log($"[前台] 两种方法均失败, pid={pid}");
            return null;
        }
        catch (Exception ex)
        {
            Diag.Log($"[前台] GetForegroundProcessName 异常: {ex.Message}");
            return null;
        }
    }

    /// <summary>方法1：Win32 P/Invoke（对齐 Python ctypes）。</summary>
    private static string? TryGetImageNameViaWin32(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0)
        {
            var err = Marshal.GetLastWin32Error();
            Diag.Win32Error($"OpenProcess(pid={pid})", err);
            return null;
        }

        try
        {
            var buffer = new char[260];
            var size = (uint)buffer.Length;
            if (QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                var name = Path.GetFileName(new string(buffer, 0, (int)size));
                Diag.Log($"[前台] Win32 成功: pid={pid}, name={name}");
                return name;
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                Diag.Win32Error($"QueryFullProcessImageNameW(pid={pid})", err);
                return null;
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>方法2：托管 API 兜底（不走 P/Invoke，权限模型不同）。</summary>
    private static string? TryGetImageNameViaManaged(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var name = proc.ProcessName;
            // Process.ProcessName 不含 .exe 后缀，补上以对齐 Win32 版
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name += ".exe";
            return name;
        }
        catch (Exception ex)
        {
            Diag.Log($"[前台] 托管 API 失败: pid={pid}, {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>目标进程是否在前台。TargetProcess 为空 → 恒 True（不检测）。</summary>
    public bool IsTargetForeground()
    {
        if (string.IsNullOrWhiteSpace(TargetProcess))
            return true;
        var current = GetForegroundProcessName();
        if (current is null)
            return false;
        return string.Equals(current, TargetProcess, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>枚举运行中的进程名（含 .exe，去重排序）。设置页下拉框用。</summary>
    public static List<string> GetRunningProcesses()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    names.Add(p.ProcessName + ".exe");
                }
                catch (Exception)
                {
                    // 系统进程访问受限，跳过
                }
            }
        }
        catch (Exception) { }
        return [.. names];
    }

    /// <summary>启动时自动检测 POE 进程（5 变体），命中返回变体名，未命中返回 null。</summary>
    public string? AutoDetectPoe()
    {
        var procs = GetRunningProcesses();
        if (procs.Count == 0) return null;
        var lower = new HashSet<string>(procs, StringComparer.OrdinalIgnoreCase);
        foreach (var variant in PoeVariants)
        {
            if (lower.Contains(variant))
                return variant;
        }
        return null;
    }
}
