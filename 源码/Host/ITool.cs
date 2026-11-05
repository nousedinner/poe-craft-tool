using System.Text.Json;
using System.Windows;

namespace ShiKe.Host;

/// <summary>
/// WPF 抽屉适配接口；无界面的规则和运行引擎位于 Core。
/// 新工具注册后通过可选能力接入运行、热键设置和附属配置保存。
/// </summary>
public interface ITool
{
    // ── 身份 ──
    string Id { get; }          // "craft", "clicker", "keyloop", "hideout"
    string Name { get; }        // "洗词缀", "连点器", ...
    string IconKey { get; }     // 资源字典图标 key（先占位）

    // ── 生命周期 ──
    /// <summary>启动注册：保存 host 引用、创建运行时服务；设置随后由宿主统一调用 LoadSettings。</summary>
    void Initialize(ToolHost host);

    /// <summary>创建并返回工具页面（宿主缓存，首次选中时懒加载）。</summary>
    FrameworkElement CreatePage();

    /// <summary>导航切到此工具时调用。</summary>
    void OnActivate();

    /// <summary>导航切走时调用。</summary>
    void OnDeactivate();

    /// <summary>程序退出时停止并清理自身运行任务；不得读取页面或写入配置。宿主随后统一保存并注销热键。</summary>
    void OnShutdown();

    // ── 热键声明（宿主统一注册 + 冲突检测，抽屉不自己注册）──
    IReadOnlyList<HotkeyRequest> GetHotkeyRequests();

    // ── 存储（宿主按抽屉分节）──
    /// <summary>宿主从 settings.json 的 [Id] 节加载。</summary>
    void LoadSettings(JsonElement section);

    /// <summary>宿主保存时调用，写入 [Id] 节。</summary>
    void SaveSettings(Utf8JsonWriter writer);
}

/// <summary>
/// 可按需启用的工具能力。宿主只为已启用工具注册操作热键；配置页面仍可正常打开。
/// </summary>
public interface IEnableableTool
{
    bool IsEnabled { get; }
}
