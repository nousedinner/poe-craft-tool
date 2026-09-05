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
/// - CheckForeground：启动类热键由公共层统一 fail-closed，停止/坐标类请求不检查
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
    private sealed class HoldBinding
    {
        public required HotkeyRequest Request { get; init; }
        public required ParsedHotkey Hotkey { get; init; }
    }

    private readonly Dictionary<ushort, List<HoldBinding>> _holdByVk = []; // 主 vk → Hold 请求
    private readonly HashSet<HoldBinding> _activeHoldBindings = [];
    private readonly HashSet<ushort> _keysDown = [];
    private readonly LowLevelKeyboardProc _hookProc;
    private readonly Func<bool> _isTargetForeground;

    private HwndSource? _source;
    private nint _hwnd;
    private int _nextId = 0xC001;
    private nint _hookHandle;

    public HotkeyManager(Func<bool> isTargetForeground)
    {
        ArgumentNullException.ThrowIfNull(isTargetForeground);
        _isTargetForeground = isTargetForeground;
        _hookProc = HookCallback; // 实例方法委托，防止回调被 GC
    }

    // ── Win32 ──
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);
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

            var parsed = req.Mode == HotkeyMode.Hold
                ? HotkeyParser.ParseHold(req.Key)
                : HotkeyParser.Parse(req.Key);
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
                var binding = new HoldBinding { Request = req, Hotkey = parsed.Value };
                if (!_holdByVk.TryGetValue(parsed.Value.Vk, out var bindings))
                {
                    bindings = [];
                    _holdByVk[parsed.Value.Vk] = bindings;
                }
                bindings.Add(binding);
                if (_hookHandle == 0)
                {
                    _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, GetModuleHandleW(null), 0);
                    if (_hookHandle == 0)
                    {
                        bindings.Remove(binding);
                        if (bindings.Count == 0) _holdByVk.Remove(parsed.Value.Vk);
                        conflicts.Add($"热键 '{req.Key}'（{req.DisplayName}）低级键盘钩子安装失败：Win32 {Marshal.GetLastWin32Error()}");
                    }
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
        _activeHoldBindings.Clear();
        _keysDown.Clear();
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
            TryInvoke(req);
            handled = true;
        }
        return 0;
    }

    /// <summary>执行热键请求前统一应用前台契约；检测异常按危险输入 fail-closed 处理。</summary>
    internal bool TryInvoke(HotkeyRequest request)
    {
        if (!CanDispatch(request))
        {
            request.ForegroundRejectedHandler?.Invoke();
            return false;
        }

        request.Handler();
        return true;
    }

    private bool CanDispatch(HotkeyRequest request)
    {
        if (!request.CheckForeground) return true;
        try
        {
            return _isTargetForeground();
        }
        catch (Exception ex)
        {
            Diag.Log($"[热键] 前台检查异常，已拦截“{request.DisplayName}”: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
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
            var normalizedVk = HotkeyParser.NormalizeModifierVk(vk);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;

            if (down) _keysDown.Add(vk);
            if (up) _keysDown.Remove(vk);

            if (down && _holdByVk.TryGetValue(normalizedVk, out var bindings))
            {
                foreach (var binding in bindings)
                {
                    if (_activeHoldBindings.Contains(binding) ||
                        !HotkeyParser.AreModifiersPressed(binding.Hotkey.Modifiers, key => _keysDown.Contains(key)))
                        continue;

                    // Hold 只在按下阶段检查前台。已经启动的 Hold 无论当前前台如何，
                    // 松开时都必须调用 ReleaseHandler，防止残留鼠标/按键状态。
                    if (!CanDispatch(binding.Request))
                    {
                        dispatcher?.BeginInvoke(() => binding.Request.ForegroundRejectedHandler?.Invoke());
                        continue;
                    }
                    _activeHoldBindings.Add(binding);
                    dispatcher?.BeginInvoke(() => binding.Request.Handler());
                }
            }

            if (up)
            {
                foreach (var binding in _activeHoldBindings.ToList())
                {
                    if (binding.Hotkey.Vk != normalizedVk &&
                        HotkeyParser.AreModifiersPressed(binding.Hotkey.Modifiers, key => _keysDown.Contains(key)))
                        continue;
                    _activeHoldBindings.Remove(binding);
                    dispatcher?.BeginInvoke(() => binding.Request.ReleaseHandler?.Invoke());
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }
}

public readonly record struct ParsedHotkey(uint Modifiers, ushort Vk);

/// <summary>热键字符串解析："F5" / "Ctrl+F5" → (修饰键, 虚拟键码)。</summary>
public static class HotkeyParser
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;

    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkAlt = 0x12;
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;
    private const ushort VkLShift = 0xA0;
    private const ushort VkRShift = 0xA1;
    private const ushort VkLControl = 0xA2;
    private const ushort VkRControl = 0xA3;
    private const ushort VkLAlt = 0xA4;
    private const ushort VkRAlt = 0xA5;

    public static ParsedHotkey? Parse(string key) => ParseInternal(key, allowStandaloneModifier: false);

    public static ParsedHotkey? ParseHold(string key) => ParseInternal(key, allowStandaloneModifier: true);

    private static ParsedHotkey? ParseInternal(string key, bool allowStandaloneModifier)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var parts = key.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        uint mods = 0;
        ushort vk = 0;
        ushort standaloneModifierVk = 0;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= MOD_CONTROL;
                    standaloneModifierVk = VkControl;
                    break;
                case "alt":
                    mods |= MOD_ALT;
                    standaloneModifierVk = VkAlt;
                    break;
                case "shift":
                    mods |= MOD_SHIFT;
                    standaloneModifierVk = VkShift;
                    break;
                case "win":
                    mods |= MOD_WIN;
                    standaloneModifierVk = VkLWin;
                    break;
                default:
                    if (vk != 0) return null; // 不支持两个主键组成的 chord
                    vk = KeyCode.Parse(part);
                    if (vk == 0) return null;
                    break;
            }
        }
        if (vk == 0)
        {
            if (!allowStandaloneModifier || parts.Length != 1 || standaloneModifierVk == 0) return null;
            return new ParsedHotkey(0, standaloneModifierVk);
        }
        return new ParsedHotkey(mods, vk);
    }

    public static ushort NormalizeModifierVk(ushort vk) => vk switch
    {
        VkLShift or VkRShift => VkShift,
        VkLControl or VkRControl => VkControl,
        VkLAlt or VkRAlt => VkAlt,
        VkRWin => VkLWin,
        _ => vk,
    };

    public static bool AreModifiersPressed(uint modifiers, Func<ushort, bool> isDown)
    {
        if ((modifiers & MOD_CONTROL) != 0 &&
            !AnyDown(isDown, VkControl, VkLControl, VkRControl)) return false;
        if ((modifiers & MOD_ALT) != 0 &&
            !AnyDown(isDown, VkAlt, VkLAlt, VkRAlt)) return false;
        if ((modifiers & MOD_SHIFT) != 0 &&
            !AnyDown(isDown, VkShift, VkLShift, VkRShift)) return false;
        if ((modifiers & MOD_WIN) != 0 &&
            !AnyDown(isDown, VkLWin, VkRWin)) return false;
        return true;
    }

    private static bool AnyDown(Func<ushort, bool> isDown, params ushort[] keys) => keys.Any(isDown);
}
