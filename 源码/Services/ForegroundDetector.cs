using System.Diagnostics;

namespace ShiKe.Services;

/// <summary>
/// 前台进程检测（对齐 Python 版 foreground.py，三层覆盖见方案 §4.3）。
/// 1. 热键回调：启动类热键检查（停止类不检查）
/// 2. 运行中循环：每次操作前检查，非目标进程时暂停
/// 3. 设置页：进程下拉框 + 刷新
/// </summary>
public sealed class ForegroundDetector : IDisposable
{
    private readonly object _gate = new();
    private readonly IForegroundNativeApi _native;
    private ProcessIdentity? _identity;
    private string? _targetProcess;
    private bool _disposed;

    private sealed record ProcessIdentity(uint ProcessId, string Name, IForegroundProcessHandle Handle);

    public ForegroundDetector() : this(ForegroundNativeApi.Instance) { }

    internal ForegroundDetector(IForegroundNativeApi native) => _native = native;

    /// <summary>目标进程名（带 .exe，如 "PathOfExile.exe"）。空时拒绝启动自动操作。</summary>
    public string? TargetProcess
    {
        get { lock (_gate) return _targetProcess; }
        set
        {
            lock (_gate)
            {
                if (string.Equals(_targetProcess, value, StringComparison.Ordinal)) return;
                ClearIdentity();
                _targetProcess = value;
            }
        }
    }

    /// <summary>自动检测的 5 个 POE 进程变体（方案 §8）。</summary>
    public static readonly string[] PoeVariants =
    [
        "PathOfExile.exe", "PathOfExile_x64.exe", "PathOfExile",
        "PathOfExileSteam.exe", "PathOfExile_KG.exe"
    ];

    /// <summary>前台窗口所属进程名（含 .exe）。获取失败返回 null。</summary>
    public static string? GetForegroundProcessName()
    {
        using var detector = new ForegroundDetector();
        lock (detector._gate) return detector.ReadForegroundName();
    }

    // 调用方持有 _gate。只缓存由存活进程句柄绑定的身份；每次仍读取并复核当前 HWND/PID。
    private string? ReadForegroundName()
    {
        try
        {
            var hwnd = _native.GetForegroundWindow();
            if (hwnd == 0)
            {
                ClearIdentity();
                Diag.Log("[前台] GetForegroundWindow 返回 0（无前台窗口）");
                return null;
            }

            var pid = _native.GetWindowProcessId(hwnd);
            if (pid == 0)
            {
                ClearIdentity();
                Diag.Log($"[前台] GetWindowThreadProcessId 返回 pid=0, hwnd=0x{hwnd:X}");
                return null;
            }

            if (_identity is { } current && current.ProcessId == pid)
            {
                // PID 可复用，但一个仍未退出的旧进程不能被同 PID 的新进程替代。
                // 退出或等待失败本次直接拒绝，绝不借旧名称或降级路径继续放行。
                if (!StillForeground(hwnd, pid) || !current.Handle.IsAlive())
                {
                    ClearIdentity();
                    return null;
                }
                return current.Name;
            }

            ClearIdentity();
            var process = _native.OpenProcess(pid, verifyLifetime: true);
            if (process is not null)
            {
                try
                {
                    var name = process.ReadImageName();
                    if (!process.IsAlive()) return null;
                    if (!string.IsNullOrEmpty(name))
                    {
                        // 查名称期间可能切窗或发生 HWND 复用；一致后才接纳缓存并返回身份。
                        if (!StillForeground(hwnd, pid) || !process.IsAlive()) return null;
                        _identity = new ProcessIdentity(pid, name, process);
                        process = null; // 所有权转交缓存，之后由替换/目标变更/Dispose 释放。
                        return name;
                    }
                }
                finally { process?.Dispose(); }
            }

            // SYNCHRONIZE 权限不足或名称查询失败时保留旧查询路径，但绝不缓存该结果。
            using var transient = _native.OpenProcess(pid, verifyLifetime: false);
            var result = transient?.ReadImageName();
            if (string.IsNullOrEmpty(result))
            {
                result = _native.GetManagedProcessName(pid);
                if (result is not null)
                    Diag.Log($"[前台] Win32 失败，托管 API 成功: pid={pid}, name={result}");
            }
            if (string.IsNullOrEmpty(result))
            {
                Diag.Log($"[前台] 两种方法均失败, pid={pid}");
                return null;
            }
            return StillForeground(hwnd, pid) ? result : null;
        }
        catch (Exception ex)
        {
            ClearIdentity();
            Diag.Log($"[前台] GetForegroundProcessName 异常: {ex.Message}");
            return null;
        }
    }

    private bool StillForeground(nint window, uint processId)
    {
        var latestWindow = _native.GetForegroundWindow();
        return latestWindow == window && latestWindow != 0 && _native.GetWindowProcessId(latestWindow) == processId;
    }

    /// <summary>目标进程是否在前台。TargetProcess 为空 → False（危险输入采用 fail-closed）。</summary>
    public bool IsTargetForeground()
    {
        lock (_gate)
        {
            if (_disposed || string.IsNullOrWhiteSpace(_targetProcess)) return false;
            var current = ReadForegroundName();
            return current is not null && string.Equals(current, _targetProcess, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ClearIdentity()
    {
        var previous = _identity;
        _identity = null;
        previous?.Handle.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ClearIdentity();
        }
    }

    /// <summary>枚举运行中的进程名（含 .exe，去重排序）。设置页下拉框用。</summary>
    public static List<string> GetRunningProcesses()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using var process = p;
                try
                {
                    names.Add(process.ProcessName + ".exe");
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
