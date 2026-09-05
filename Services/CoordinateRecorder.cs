using System.Runtime.InteropServices;
using System.Windows;
using ShiKe.Host;

namespace ShiKe.Services;

/// <summary>
/// 坐标录制（跨抽屉共享，对齐 Python 版 main.py HotkeyManager._coord_target + _on_set_coord）。
/// - 抽屉实现 ICoordinateProvider 声明槽位，宿主提供录制 UI（三态按钮）
/// - StartRecording(slot) → 等 F7 → OnRecordHotkey() 取光标位置 → 持久化 → 事件
/// - ⚠️ 信号分离铁律（坑 #10）：录制完成事件与通货选中逻辑完全独立，绝不共享
/// </summary>
public sealed class CoordinateRecorder
{
    private readonly StorageService _storage;
    private readonly Func<Point?> _cursorPositionProvider;
    private CoordinateSlot? _activeSlot;

    /// <summary>录制完成事件（槽位 + 坐标）。UI 层监听更新按钮三态。与选中信号独立。</summary>
    public event Action<CoordinateSlot, Point>? RecordingCompleted;

    /// <summary>光标读取失败事件。失败时保持录制状态，不写入伪造的 (0,0)，用户可再次触发热键。</summary>
    public event Action<CoordinateSlot, string>? RecordingFailed;

    public CoordinateRecorder(StorageService storage, Func<Point?>? cursorPositionProvider = null)
    {
        _storage = storage;
        _cursorPositionProvider = cursorPositionProvider ?? GetCursorPosition;
    }

    public bool IsRecording => _activeSlot is not null;

    public CoordinateSlot? ActiveSlot => _activeSlot;

    public void StartRecording(CoordinateSlot slot) => _activeSlot = slot;

    /// <summary>录制热键（F7）触发：取当前鼠标位置 → 存槽 → 持久化 → 事件 → 清除活动槽位。</summary>
    public void OnRecordHotkey()
    {
        if (_activeSlot is null)
            return;
        var slot = _activeSlot;
        var pt = _cursorPositionProvider();
        if (pt is null)
        {
            const string message = "无法读取鼠标位置；请移动鼠标后再次按录制热键";
            Diag.Log($"[坐标] {message}, slot={slot.SlotId}");
            RecordingFailed?.Invoke(slot, message);
            return;
        }

        _activeSlot = null; // 成功取得位置后再清除；失败允许原槽位直接重试
        var coords = _storage.LoadCoordinates();
        coords[slot.SlotId] = pt.Value;
        _storage.SaveCoordinates(coords);
        RecordingCompleted?.Invoke(slot, pt.Value);
    }

    public void CancelRecording() => _activeSlot = null;

    /// <summary>读取已保存坐标（未设置返回 null）。</summary>
    public Point? GetCoordinate(string slotId)
        => _storage.LoadCoordinates().TryGetValue(slotId, out var pt) ? pt : null;

    public void ClearCoordinate(string slotId)
    {
        var coords = _storage.LoadCoordinates();
        if (coords.Remove(slotId))
            _storage.SaveCoordinates(coords);
    }

    // ── 光标位置 ──
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private static Point? GetCursorPosition()
    {
        if (!GetCursorPos(out var pt))
        {
            Diag.Win32Error("GetCursorPos", Marshal.GetLastWin32Error());
            return null;
        }
        return new Point(pt.X, pt.Y);
    }
}
