using System.Text.Json;
using System.Windows;

namespace ShiKe.Host;

/// <summary>
/// 抽屉标准接口（核心扩展点）。
/// 新增功能 = 实现本接口 + 注册表加一行，宿主零改动。
/// </summary>
public interface ITool
{
    // ── 身份 ──
    string Id { get; }          // "craft", "clicker", "keyloop", "hideout"
    string Name { get; }        // "洗词缀", "连点器", ...
    string IconKey { get; }     // 资源字典图标 key（先占位）

    // ── 生命周期 ──
    /// <summary>启动注册：保存 host 引用、读取设置。</summary>
    void Initialize(ToolHost host);

    /// <summary>创建并返回工具页面（宿主缓存，首次选中时懒加载）。</summary>
    FrameworkElement CreatePage();

    /// <summary>导航切到此工具时调用。</summary>
    void OnActivate();

    /// <summary>导航切走时调用。</summary>
    void OnDeactivate();

    /// <summary>程序退出时调用：停线程、保存设置、注销热键。</summary>
    void OnShutdown();

    // ── 热键声明（宿主统一注册 + 冲突检测，抽屉不自己注册）──
    IReadOnlyList<HotkeyRequest> GetHotkeyRequests();

    // ── 存储（宿主按抽屉分节）──
    /// <summary>宿主从 settings.json 的 [Id] 节加载。</summary>
    void LoadSettings(JsonElement section);

    /// <summary>宿主保存时调用，写入 [Id] 节。</summary>
    void SaveSettings(Utf8JsonWriter writer);
}
