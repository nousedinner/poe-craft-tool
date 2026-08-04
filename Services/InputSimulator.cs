using System.Runtime.InteropServices;
using System.Windows;

namespace ShiKe.Services;

/// <summary>
/// 输入模拟（SendInput 封装，对齐 Python 版 auto_operator.py 时序）。
/// - 所有等待用 Task.Delay(ms, token) 可中断
/// - 紧急停止：每次输入前检查光标 ≤(1,1) → EmergencyCts.Cancel()（对应 Python FAILSAFE）
/// - Shift 的管理（HoldShift/ReleaseShift/ReleaseAllKeys）由引擎层控制（铁律：Shift 全程按住，
///   改造循环内不释放；ShiftClick 只是带 ±10px 偏移的拆分点击，自身不按/放 Shift）
/// - 时序细节（源码为准，非 ARCHITECTURE 简化版）：
///   MoveTo: SetCursorPos → 0.03s
///   RightClick: MoveTo → delay → 右键 → delay×3
///   ShiftClick: ±10px → MoveTo → delay → down → 0.02s → up → delay×2
///   CtrlAltC: 0.05s → 组合键 → max(delay×5, 0.15) 后返回
/// </summary>
public sealed class InputSimulator
{
    private readonly Func<CancellationTokenSource> _emergencyCtsProvider;
    private readonly ForegroundDetector _foreground;

    public InputSimulator(Func<CancellationTokenSource> emergencyCtsProvider, ForegroundDetector foreground)
    {
        _emergencyCtsProvider = emergencyCtsProvider;
        _foreground = foreground;
    }

    // ── Win32 常量 ──
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    // ── Win32 结构体（Sequential 布局让 CLR 自动处理64位对齐）──

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    /// <summary>
    /// INPUT union — Sequential 布局让 CLR 自动处理64位对齐填充。
    /// 旧版用 Explicit+FieldOffset(4) 在64位上结构体大小错误，导致 SendInput 读到垃圾数据。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUT_UNION union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    // ── Win32 API ──

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // ── 紧急停止（对应 Python FAILSAFE：光标到屏幕角落 = 强制停止）──

    private void CheckEmergencyStop()
    {
        if (GetCursorPos(out var pt) && pt.X <= 1 && pt.Y <= 1)
        {
            Diag.Log($"[输入] 紧急停止触发: 光标位于 ({pt.X}, {pt.Y})");
            _emergencyCtsProvider().Cancel();
            throw new OperationCanceledException("紧急停止：光标已移到屏幕角落 (≤1,1)");
        }
    }

    /// <summary>
    /// 等待目标进程回到前台（对齐 Python _interruptible_sleep 的前台检查）。
    /// TargetProcess 为空时直接返回（不检测）。
    /// </summary>
    private async Task AwaitForegroundAsync(CancellationToken token)
    {
        if (string.IsNullOrEmpty(_foreground.TargetProcess)) return;
        if (_foreground.IsTargetForeground()) return;

        Diag.Log($"[输入] AwaitForeground: 等待前台切换至 {_foreground.TargetProcess}");
        while (!_foreground.IsTargetForeground())
            await Task.Delay(200, token);
        Diag.Log("[输入] AwaitForeground: 前台已切换");
    }

    /// <summary>发送鼠标事件。返回 true=SendInput 报告成功。</summary>
    private static bool SendMouse(uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            union = new INPUT_UNION { mi = new MOUSEINPUT { dwFlags = flags, dwExtraInfo = 0 } }
        };
        var sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            Diag.Win32Error($"SendInput(mouse=0x{flags:X4})", Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    /// <summary>
    /// 发送键盘事件（虚拟键码 + 扫描码）。
    /// 游戏用 DirectInput/Raw Input 只认硬件扫描码，必须同时设置 wVk 和 wScan。
    /// </summary>
    private static bool SendKey(ushort vk, bool up)
    {
        var scan = (ushort)MapVirtualKey(vk, 0); // MAPVK_VK_TO_VSC
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new INPUT_UNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = up ? KEYEVENTF_KEYUP : 0,
                    dwExtraInfo = 0,
                }
            }
        };
        var sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            Diag.Win32Error($"SendInput(key=0x{vk:X2},scan=0x{scan:X2},up={up})", Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    // ── 基础操作 ──

    /// <summary>移动到坐标并等 0.03s（Python _move_to）。</summary>
    public async Task MoveToAsync(int x, int y, CancellationToken token)
    {
        CheckEmergencyStop();
        SetCursorPos(x, y);
        await Task.Delay(30, token);
    }

    /// <summary>右键：MoveTo → delay → 右键（down→0.02s→up）→ delay×3。
    /// down/up 间等待不可取消（防右键残留）。</summary>
    public async Task RightClickAsync(int x, int y, int delayMs, CancellationToken token)
    {
        await AwaitForegroundAsync(token);
        await MoveToAsync(x, y, token);
        await Task.Delay(delayMs, token);
        CheckEmergencyStop();
        SendMouse(MOUSEEVENTF_RIGHTDOWN);
        await Task.Delay(20, CancellationToken.None);
        SendMouse(MOUSEEVENTF_RIGHTUP);
        await Task.Delay(delayMs * 3, token);
    }

    /// <summary>
    /// Shift+点击（Shift 已由外层按住）：±10px 随机偏移 → MoveTo → delay → down → 0.02s → up → delay×2。
    /// </summary>
    public async Task ShiftClickAsync(int x, int y, int delayMs, CancellationToken token)
    {
        await AwaitForegroundAsync(token);
        int ox = x + Random.Shared.Next(-10, 11);
        int oy = y + Random.Shared.Next(-10, 11);
        CheckEmergencyStop();
        SetCursorPos(ox, oy);
        await Task.Delay(delayMs, token);
        CheckEmergencyStop();
        SendMouse(MOUSEEVENTF_LEFTDOWN);
        await Task.Delay(20, CancellationToken.None);
        SendMouse(MOUSEEVENTF_LEFTUP);
        await Task.Delay(delayMs * 2, token);
    }

    /// <summary>普通左键点击（当前位置）。down/up 间不可取消（原子）。</summary>
    public async Task ClickAsync(int delayMs, CancellationToken token)
    {
        await AwaitForegroundAsync(token);
        CheckEmergencyStop();
        SendMouse(MOUSEEVENTF_LEFTDOWN);
        await Task.Delay(20, CancellationToken.None);
        SendMouse(MOUSEEVENTF_LEFTUP);
        await Task.Delay(delayMs, token);
    }

    /// <summary>右键点击（当前位置）。down/up 间不可取消（原子）。</summary>
    public async Task RightClickAsync(int delayMs, CancellationToken token)
    {
        await AwaitForegroundAsync(token);
        CheckEmergencyStop();
        SendMouse(MOUSEEVENTF_RIGHTDOWN);
        await Task.Delay(20, CancellationToken.None);
        SendMouse(MOUSEEVENTF_RIGHTUP);
        await Task.Delay(delayMs, token);
    }

    // ── Shift 管理 ──

    public async Task HoldShiftAsync(CancellationToken token)
    {
        await AwaitForegroundAsync(token);
        CheckEmergencyStop();
        SendKey(KeyCode.Shift, up: false);
        await Task.Delay(50, token);
    }

    public async Task ReleaseShiftAsync(CancellationToken token)
    {
        SendKey(KeyCode.Shift, up: true);
        await Task.Delay(50, token);
    }

    /// <summary>释放 shift/ctrl/alt（对齐 Python _release_all：只释放键盘修饰键，不释放鼠标按键）。</summary>
    public async Task ReleaseAllKeysAsync(CancellationToken token)
    {
        SendKey(KeyCode.Shift, up: true);
        SendKey(KeyCode.Control, up: true);
        SendKey(KeyCode.Alt, up: true);
        await Task.Delay(50, token);
    }

    /// <summary>
    /// 释放鼠标按键（仅在 Loop finally 安全兜底时调用）。
    /// 分开管理：正常运行中不释放鼠标（避免干扰游戏状态），仅在引擎彻底停止后释放。
    /// </summary>
    public void ReleaseMouseButtons()
    {
        SendMouse(MOUSEEVENTF_LEFTUP);
        SendMouse(MOUSEEVENTF_RIGHTUP);
    }

    // ── 键盘 ──

    /// <summary>Ctrl+Alt+C（国服复制物品文本；Shift 应保持按住）。返回后已等 max(delay×5, 0.15)。</summary>
    public async Task CtrlAltCAsync(int delayMs, CancellationToken token)
    {
        await AwaitForegroundAsync(token);
        await Task.Delay(50, token);
        CheckEmergencyStop();
        SendKey(KeyCode.Control, up: false);
        SendKey(KeyCode.Alt, up: false);
        SendKey(KeyCode.C, up: false);
        SendKey(KeyCode.C, up: true);
        SendKey(KeyCode.Alt, up: true);
        SendKey(KeyCode.Control, up: true);
        await Task.Delay(Math.Max(delayMs * 5, 150), token);
    }

    public void KeyDown(string key) => SendKey(KeyCode.Parse(key), up: false);
    public void KeyUp(string key) => SendKey(KeyCode.Parse(key), up: true);

    /// <summary>
    /// 逐字符输入文本（KEYEVENTF_UNICODE，不依赖键盘布局；对齐 Python keyboard.write）。
    /// 每字符 5ms 间隔，防游戏丢字符。
    /// </summary>
    public async Task TypeTextAsync(string text, CancellationToken token)
    {
        await AwaitForegroundAsync(token);
        foreach (var ch in text)
        {
            token.ThrowIfCancellationRequested();
            SendKeyUnicode(ch, down: true);
            SendKeyUnicode(ch, down: false);
            await Task.Delay(5, token);
        }
    }

    private static void SendKeyUnicode(char ch, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            union = new INPUT_UNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = down ? KEYEVENTF_UNICODE : KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                    dwExtraInfo = 0,
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    /// <summary>按下并释放一个键（对齐 Python keyboard.press_and_release）。</summary>
    public void PressAndRelease(string key)
    {
        var vk = KeyCode.Parse(key);
        SendKey(vk, up: false);
        Thread.Sleep(10); // 小延迟确保游戏能收到按下事件
        SendKey(vk, up: true);
    }
}

/// <summary>键名 → 虚拟键码。</summary>
public static class KeyCode
{
    public const ushort Shift = 0x10;
    public const ushort Control = 0x11;
    public const ushort Alt = 0x12;
    public const ushort C = 0x43;

    private static readonly Dictionary<string, ushort> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shift"] = 0x10, ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["win"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20,
        ["tab"] = 0x09, ["backspace"] = 0x08, ["delete"] = 0x2E, ["insert"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91,
        ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC,
        [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF,
    };

    /// <summary>解析键名。未知键返回 0。</summary>
    public static ushort Parse(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        key = key.Trim();
        if (Map.TryGetValue(key, out var vk)) return vk;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') return (ushort)c;
            if (c is >= '0' and <= '9') return (ushort)c;
            return 0;
        }
        if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') &&
            int.TryParse(key[1..], out var n) && n is >= 1 and <= 24)
            return (ushort)(0x70 + n - 1);
        return 0;
    }
}
