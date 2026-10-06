namespace ShiKe.Host;

/// <summary>
/// 可选能力接口：坐标录制。
/// 需要坐标录制的抽屉实现（洗装），宿主提供统一录制 UI 与触发热键。
/// </summary>
public interface ICoordinateProvider
{
    IReadOnlyList<CoordinateSlot> GetCoordinateSlots();
}
