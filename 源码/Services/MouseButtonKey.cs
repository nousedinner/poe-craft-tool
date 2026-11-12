namespace ShiKe.Services;

/// <summary>
/// 鼠标侧键的名称与虚拟键码。
/// Windows 只定义 5 个鼠标键，第 6 个及以后的侧键不会以鼠标消息到达任何程序，
/// 必须由厂商驱动映射为键盘键（建议 F13～F24）后才能被本程序录制；这里只暴露 4/5 号侧键。
/// 与 <see cref="KeyCode"/> 分开维护：KeyCode 仍是“键盘虚拟键”的唯一来源，
/// 避免按键循环把鼠标键当键盘扫描码发送。
/// </summary>
public static class MouseButtonKey
{
    /// <summary>4 号键，多数鼠标的后退侧键。</summary>
    public const ushort XButton1 = 0x05;

    /// <summary>5 号键，多数鼠标的前进侧键。</summary>
    public const ushort XButton2 = 0x06;

    public static bool IsSideButton(ushort vk) => vk is XButton1 or XButton2;

    /// <summary>解析键名。只接受侧键，左右中键既不参与热键也不参与模拟。</summary>
    public static bool TryParse(string? name, out ushort vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        switch (name.Trim().ToLowerInvariant())
        {
            case "mouse4" or "m4" or "xbutton1":
                vk = XButton1;
                return true;
            case "mouse5" or "m5" or "xbutton2":
                vk = XButton2;
                return true;
            default:
                return false;
        }
    }

    /// <summary>写入配置与参与解析比较的规范键名。</summary>
    public static string KeyName(ushort vk) => vk == XButton2 ? "Mouse5" : "Mouse4";

    /// <summary>界面显示名；避免用户误以为要按键盘上的 4。</summary>
    public static string DisplayName(ushort vk) => vk == XButton2 ? "鼠标5" : "鼠标4";

    /// <summary>
    /// 注入侧键时的 <c>MOUSEINPUT.mouseData</c>：按钮编号放在**低字**（XBUTTON1 = 1、XBUTTON2 = 2）。
    /// 与下面的钩子解码不是同一套编码，切勿合并。
    /// </summary>
    public static uint ToInputMouseData(ushort vk) => vk == XButton2 ? 2u : 1u;

    /// <summary>
    /// 从低级鼠标钩子的 <c>MSLLHOOKSTRUCT.mouseData</c> 还原键码。
    /// 这里按钮编号在**高字**——Win32 对“注入”和“钩子上报”用了两套位置：
    /// 曾经两边都按高字处理，结果 SendInput 返回成功但系统没生成任何 XBUTTON，游戏自然收不到。
    /// </summary>
    public static ushort FromHookMouseData(uint mouseData) => (mouseData >> 16) switch
    {
        1 => XButton1,
        2 => XButton2,
        _ => 0,
    };
}
