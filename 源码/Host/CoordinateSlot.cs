using System.Windows;

namespace ShiKe.Host;

/// <summary>坐标录制槽位声明。</summary>
public sealed class CoordinateSlot
{
    /// <summary>槽位 ID，如 "currency.chaos"、"item"。</summary>
    public string SlotId { get; init; } = "";

    /// <summary>显示名，如 "混沌石位置"。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>默认位置（可为 null）。</summary>
    public Point? DefaultPosition { get; init; }
}
