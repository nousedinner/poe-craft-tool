using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ShiKe.Host;

namespace ShiKe.Services;

/// <summary>
/// 统一热键管理（对齐 Python 版 main.py HotkeyManager，方案 §3.2）：
/// - 抽屉只声明 GetHotkeyRequests()，不自己注册
/// - Toggle 模式：RegisterHotKey + WM_HOTKEY（无需管理员权限）
/// - Hold 模式：WH_KEYBOARD_LL 低级钩子（按下 → Handler，抬起 → ReleaseHandler）
/// - 冲突检测：注册前查重，收集全部冲突返回（宿主弹窗）
/// - 回调统一在 UI 线程执行（WM_HOTKEY 天然 UI 线程；LL 钩子回调经 Dispatcher.BeginInvoke）
/// - 两类 ID 分开清理（对应 Python 坑 #2）
/// </summary>
public sealed class HotkeyManager
{
    private const int WM_HOTKEY = 0x0312;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly Dictionary<int, HotkeyRequest> _registered = [];   // hotkeyId → Toggle 请求
    private readonly Dictionary<ushort, HotkeyRequest> _holdByVk = [];  // vk → Hold 请求
    private readonly HashSet<ushort> _holdPressed = [];                 // 正在按住的 vk
    private readonly LowLevelKeyboardProc _hookProc;

    private HwndSource? _source;
    private nint _hwnd;
    private int _nextId = 0xC001;
    private nint _hookHandle;

    public HotkeyManager()
    {
        _hookProc = HookCallback; // 实例方法委托，防止回调被 GC
    }

    // ── Win32 ──
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(string? lpModuleName);

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    /// <summary>附着到主窗口（拿句柄 + WM_HOTKEY 消息）。窗口句柄未就绪时等 SourceInitialized。</summary>
    public void AttachToWindow(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle != 0)
        {
            SetupHook(helper.Handle);
        }
        else
        {
            window.SourceInitialized += (_, _) => SetupHook(new WindowInteropHelper(window).Handle);
        }
    }

    private void SetupHook(nint hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>
    /// 注册全部热键。返回冲突/失败描述列表（空 = 全部成功）。
    /// 冲突规则（对齐 Python _check_conflicts）：键名大小写不敏感查重，重复键跳过注册。
    /// </summary>
    public List<string> RegisterAll(IReadOnlyList<HotkeyRequest> requests)
    {
        var conflicts = new List<string>();
        if (_hwnd == 0)
        {
            conflicts.Add("热键管理器尚未附着到主窗口，无法注册");
            return conflicts;
        }

        // 1. 键重复检测
        var dupKeys = requests
            .Where(r => !string.IsNullOrWhiteSpace(r.Key))
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dup in dupKeys)
        {
            var names = requests.Where(r => string.Equals(r.Key, dup, StringComparison.OrdinalIgnoreCase))
                                .Select(r => r.DisplayName);
            conflicts.Add($"热键 '{dup}' 同时绑定到了「{string.Join("」和「", names)}」");
        }

        // 2. 逐个注册（重复键跳过）
        foreach (var req in requests)
        {
            if (string.IsNullOrWhiteSpace(req.Key) || dupKeys.Contains(req.Key))
                continue;

            var parsed = HotkeyParser.Parse(req.Key);
            if (parsed is null)
            {
                conflicts.Add($"无法解析热键 '{req.Key}'（{req.DisplayName}）");
                continue;
            }

            if (req.Mode == HotkeyMode.Toggle)
            {
                int id = _nextId++;
                // MOD_NOREPEAT：按住不重复触发（对齐 Python add_hotkey 行为）
                if (RegisterHotKey(_hwnd, id, parsed.Value.Modifiers | HotkeyParser.MOD_NOREPEAT, parsed.Value.Vk))
                {
                    _registered[id] = req;
                }
                else
                {
                    conflicts.Add($"热键 '{req.Key}'（{req.DisplayName}）注册失败：可能被其他程序占用");
                }
            }
            else // Hold
            {
                _holdByVk[parsed.Value.Vk] = req;
                if (_hookHandle == 0)
                {
                    _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, GetModuleHandleW(null), 0);
                }
            }
        }

        return conflicts;
    }

    /// <summary>注销全部（Toggle 与 Hold 两类分开清理，对应 Python 坑 #2）。</summary>
    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList())
            UnregisterHotKey(_hwnd, id);
        _registered.Clear();

        if (_hookHandle != 0)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = 0;
        }
        _holdByVk.Clear();
        _holdPressed.Clear();
    }

    public void ReRegister(IReadOnlyList<HotkeyRequest> requests)
    {
        UnregisterAll();
        RegisterAll(requests);
    }

    // ── WM_HOTKEY（UI 线程，直接调用）──
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var req))
        {
            req.Handler();
            handled = true;
        }
        return 0;
    }

    // ── WH_KEYBOARD_LL（钩子线程，经 Dispatcher 回 UI 线程）──
    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var vk = (ushort)Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
            var msg = wParam.ToInt32();
            var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            var up = msg is WM_KEYUP or WM_SYSKEYUP;

            if ((down || up) && _holdByVk.TryGetValue(vk, out var req))
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (down && !_holdPressed.Contains(vk))
                {
                    _holdPressed.Add(vk);
                    dispatcher?.BeginInvoke(() => req.Handler());
                }
                else if (up && _holdPressed.Contains(vk))
                {
                    _holdPressed.Remove(vk);
                    dispatcher?.BeginInvoke(() => req.ReleaseHandler?.Invoke());
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }
}

/// <summary>热键字符串解析："F5" / "Ctrl+F5" → (修饰键, 虚拟键码)。</summary>
public static class HotkeyParser
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;

    public static (uint Modifiers, ushort Vk)? Parse(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var parts = key.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        uint mods = 0;
        ushort vk = 0;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= MOD_CONTROL;
                    break;
                case "alt":
                    mods |= MOD_ALT;
                    break;
                case "shift":
                    mods |= MOD_SHIFT;
                    break;
                case "win":
                    mods |= MOD_WIN;
                    break;
                default:
                    vk = KeyCode.Parse(part);
                    break;
            }
        }
        if (vk == 0) return null;
        return (mods, vk);
    }
}
