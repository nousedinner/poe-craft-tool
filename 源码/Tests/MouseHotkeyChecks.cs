using System.Collections;
using System.Reflection;
using ShiKe.Host;
using ShiKe.Runtime;
using ShiKe.Services;
using ShiKe.Tools.KeyLoop;
using ShiKe.Tools.Settings;

/// <summary>
/// 鼠标侧键热键：解析、冲突、注入过滤、Toggle/Hold 派发，以及按键循环槽位的模拟发送边界。
/// 全部用例只驱动托管状态机，不安装钩子、不注册原生热键、不发送真实输入。
/// </summary>
internal static class MouseHotkeyChecks
{
    private const int XButtonDown = 0x020B;
    private const int XButtonUp = 0x020C;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private const uint Injected = 0x01;
    private const uint LowerIntegrityInjected = 0x02;
    private const ushort LeftControl = 0xA2;

    public static void MouseKeysParseWithoutPollutingKeyboardKeys()
    {
        var mouse4 = HotkeyParser.Parse("Mouse4") ?? throw new InvalidOperationException("Mouse4 应可解析");
        Equal((uint)0, mouse4.Modifiers, "纯侧键不应带修饰键");
        Equal(MouseButtonKey.XButton1, mouse4.Vk, "Mouse4 应映射到 4 号键");
        Require(mouse4.IsMouseButton, "鼠标侧键必须能被注册流程识别");

        var combo = HotkeyParser.Parse("alt+MOUSE5") ?? throw new InvalidOperationException("Alt+Mouse5 应可解析");
        Equal(HotkeyParser.MOD_ALT, combo.Modifiers, "侧键组合不得丢失修饰键");
        Equal(MouseButtonKey.XButton2, combo.Vk, "Mouse5 大小写不敏感且应映射到 5 号键");
        Require(combo.IsMouseButton, "组合侧键也必须识别为鼠标键");

        Require(HotkeyParser.ParseHold("Mouse4") is not null, "按住模式也必须接受侧键");
        // KeyCode 仍只表示键盘虚拟键：按键循环的键盘发送路径不能被鼠标键污染。
        Equal((ushort)0, KeyCode.Parse("Mouse4"), "KeyCode 不得把侧键当键盘键");
        Equal((ushort)0, KeyCode.Parse("mouse5"), "KeyCode 不得把侧键当键盘键");

        Equal("鼠标4", HotkeySetting.Display("Mouse4"), "侧键应显示为中文名");
        Equal("Ctrl+鼠标5", HotkeySetting.Display("Ctrl+Mouse5"), "组合侧键只替换主键段");
        Equal("未绑定", HotkeySetting.Display(""), "空绑定提示保持不变");

        Require(HotkeyParser.Parse("Mouse9") is null, "未定义的侧键必须拒绝");
        Require(HotkeyParser.Parse("Mouse1") is null, "左右中键不参与热键");
        Require(HotkeyParser.Parse("Ctrl+Mouse4+Mouse5") is null, "两个主键组成的 chord 必须拒绝");
    }

    public static void MouseHotkeysConflictByTrigger()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["craft_start"] = "Ctrl+Mouse4",
            ["craft_stop"] = "Ctrl+Mouse4",
            ["coordinate"] = "F7",
            ["clicker_toggle"] = "Mouse4",
            ["clicker_hold"] = "Mouse5",
            ["keyloop"] = "F9",
            ["hideout"] = "F2",
        };

        EditableHotkey Binding(string id, string name, string defaultKey, HotkeyMode mode)
            => new(id, name, mode, defaultKey, () => values[id], key => values[id] = key);

        HotkeySettings Capture() => HotkeySettings.Capture(
        [
            Binding("craft_start", "启动洗装", "F5", HotkeyMode.Toggle),
            Binding("craft_stop", "停止洗装", "F6", HotkeyMode.Toggle),
            Binding("coordinate", "坐标录制", "F7", HotkeyMode.Toggle),
            Binding("clicker_toggle", "连点器切换", "F8", HotkeyMode.Toggle),
            Binding("clicker_hold", "连点器按住", "F11", HotkeyMode.Hold),
            Binding("keyloop", "按键循环", "F9", HotkeyMode.Toggle),
            Binding("hideout", "一键回城", "F2", HotkeyMode.Toggle),
        ]);

        var errors = SettingsValidation.ValidateHotkeys(Capture());
        Require(errors.Count == 1 && errors[0].Contains("冲突", StringComparison.Ordinal),
            "两个 Ctrl+Mouse4 必须判为冲突");

        // 同号键不同修饰、以及侧键与键盘键之间都不是冲突。
        values["craft_stop"] = "Ctrl+Mouse5";
        Require(SettingsValidation.ValidateHotkeys(Capture()).Count == 0,
            "Ctrl+Mouse4 与 Ctrl+Mouse5 不应冲突");
        values["craft_stop"] = "F6";
        Require(SettingsValidation.ValidateHotkeys(Capture()).Count == 0,
            "侧键与键盘键不应冲突");
        values["craft_start"] = "F5";
        values["clicker_hold"] = "Ctrl+Mouse4";
        Require(SettingsValidation.ValidateHotkeys(Capture()).Count == 0,
            "Mouse4 与 Ctrl+Mouse4 修饰键不同，不应冲突");
    }

    public static void InjectedMouseEventsDoNotDispatch()
    {
        var runs = 0;
        // 钩子上报的编码是高字，与注入用的低字不同，不能用注入函数造钩子数据。
        var data = HookMouseData(MouseButtonKey.XButton1);
        var manager = CreateMouseManager("Mouse4", HotkeyMode.Toggle, () => true, () => runs++, null);

        manager.HandleMouseHookEvent(XButtonDown, data, Injected);
        manager.HandleMouseHookEvent(XButtonDown, data, LowerIntegrityInjected);
        manager.HandleMouseHookEvent(XButtonDown, 0x00030000, 0);
        Require(runs == 0, "合成侧键事件和未知键号都不得触发热键");

        manager.HandleMouseHookEvent(XButtonDown, data, 0);
        Require(runs == 1, "物理侧键按下应触发一次");
    }

    public static void PhysicalMouseToggleAndHold()
    {
        var data4 = HookMouseData(MouseButtonKey.XButton1);
        var toggles = 0;
        var toggleManager = CreateMouseManager("Mouse4", HotkeyMode.Toggle, () => true, () => toggles++, null);

        toggleManager.HandleMouseHookEvent(XButtonDown, data4, 0);
        toggleManager.HandleMouseHookEvent(XButtonDown, data4, 0);
        Require(toggles == 1, "同一次按住不得重复切换");
        toggleManager.HandleMouseHookEvent(XButtonUp, data4, 0);
        toggleManager.HandleMouseHookEvent(XButtonDown, data4, 0);
        Require(toggles == 2, "松开后再次按下应重新触发");

        var starts = 0;
        var stops = 0;
        var holdManager = CreateMouseManager("Mouse4", HotkeyMode.Hold, () => true,
            () => starts++, () => stops++);
        holdManager.HandleMouseHookEvent(XButtonDown, data4, 0);
        Require(starts == 1 && stops == 0, "按住侧键应启动一次");
        holdManager.HandleMouseHookEvent(XButtonUp, data4, Injected);
        Require(stops == 0, "合成抬起不得结束物理按住");
        holdManager.HandleMouseHookEvent(XButtonUp, data4, 0);
        Require(stops == 1, "物理松开必须结束按住");

        // 修饰键状态来自键盘钩子；没有 Ctrl 时同一侧键不得启动。
        var comboStarts = 0;
        var comboManager = CreateMouseManager("Ctrl+Mouse4", HotkeyMode.Hold, () => true,
            () => comboStarts++, null);
        comboManager.HandleMouseHookEvent(XButtonDown, data4, 0);
        Require(comboStarts == 0, "缺少修饰键时不得启动组合侧键热键");
        comboManager.HandleMouseHookEvent(XButtonUp, data4, 0);
        comboManager.HandleKeyboardHookEvent(LeftControl, KeyDown, 0);
        comboManager.HandleMouseHookEvent(XButtonDown, data4, 0);
        Require(comboStarts == 1, "按住 Ctrl 后侧键应满足组合条件");
        comboManager.HandleKeyboardHookEvent(LeftControl, KeyUp, 0x80);
        comboManager.HandleMouseHookEvent(XButtonUp, data4, 0);
    }

    /// <summary>
    /// 与 RegisterHotKey 的实际语义一致：额外按下的修饰键不参与匹配，
    /// 所以裸侧键与「Ctrl+侧键」会同时派发。这是明确保留的行为，用测试固定下来。
    /// </summary>
    public static void OverlappingModifierCombosBothDispatch()
    {
        var plain = 0;
        var combo = 0;
        var manager = CreateMouseManager(() => true,
            ("Mouse4", HotkeyMode.Toggle, () => plain++, null),
            ("Ctrl+Mouse4", HotkeyMode.Toggle, () => combo++, null));
        var data = HookMouseData(MouseButtonKey.XButton1);

        manager.HandleMouseHookEvent(XButtonDown, data, 0);
        Require(plain == 1 && combo == 0, "没按修饰键时只应触发裸侧键热键");
        manager.HandleMouseHookEvent(XButtonUp, data, 0);

        manager.HandleKeyboardHookEvent(LeftControl, KeyDown, 0);
        manager.HandleMouseHookEvent(XButtonDown, data, 0);
        Require(plain == 2 && combo == 1, "按住 Ctrl 时裸侧键与 Ctrl+侧键都会派发");
        manager.HandleMouseHookEvent(XButtonUp, data, 0);
    }

    /// <summary>
    /// 组合侧键的按住必须和键盘 Hold 一样，跟着必需修饰键松开；
    /// 无修饰键的侧键按住则只认侧键自己的松开。
    /// </summary>
    public static void MouseHoldStopsWhenRequiredModifierReleased()
    {
        var starts = 0;
        var stops = 0;
        var combo = CreateMouseManager("Ctrl+Mouse4", HotkeyMode.Hold, () => true, () => starts++, () => stops++);
        var data = HookMouseData(MouseButtonKey.XButton1);

        combo.HandleKeyboardHookEvent(LeftControl, KeyDown, 0);
        combo.HandleMouseHookEvent(XButtonDown, data, 0);
        Require(starts == 1 && stops == 0, "按住 Ctrl 再按侧键应启动一次按住");

        combo.HandleKeyboardHookEvent(0x41, KeyUp, 0);
        Require(stops == 0, "松开无关按键不得结束侧键按住");

        combo.HandleKeyboardHookEvent(LeftControl, KeyUp, 0);
        Require(stops == 1, "松开必需的修饰键必须立即结束侧键按住");
        combo.HandleMouseHookEvent(XButtonUp, data, 0);
        Require(stops == 1, "已释放的按住不能在松开侧键时重复释放");

        var plainStops = 0;
        var plain = CreateMouseManager("Mouse4", HotkeyMode.Hold, () => true, () => { }, () => plainStops++);
        plain.HandleMouseHookEvent(XButtonDown, data, 0);
        plain.HandleKeyboardHookEvent(LeftControl, KeyUp, 0);
        Require(plainStops == 0, "无修饰键的侧键按住不能因为键盘事件结束");
        plain.HandleMouseHookEvent(XButtonUp, data, 0);
        Require(plainStops == 1, "无修饰键的侧键按住应在松开侧键时结束");
    }

    public static void MouseButtonEncodingAndKeyLoopSlots()
    {
        // 两套编码必须分开：注入走 MOUSEINPUT.mouseData 低字，钩子上报走 MSLLHOOKSTRUCT.mouseData 高字。
        // 曾经两边都按高字处理，SendInput 返回成功却没有生成任何 XBUTTON，游戏收不到侧键。
        Equal(1u, MouseButtonKey.ToInputMouseData(MouseButtonKey.XButton1), "注入 4 号键必须写在 mouseData 低字");
        Equal(2u, MouseButtonKey.ToInputMouseData(MouseButtonKey.XButton2), "注入 5 号键必须写在 mouseData 低字");
        Require(MouseButtonKey.ToInputMouseData(MouseButtonKey.XButton1) != HookMouseData(MouseButtonKey.XButton1),
            "注入编码与钩子编码不能混用同一套高低字约定");

        Equal(MouseButtonKey.XButton1, MouseButtonKey.FromHookMouseData(HookMouseData(MouseButtonKey.XButton1)),
            "钩子解码必须还原 4 号键");
        Equal(MouseButtonKey.XButton2, MouseButtonKey.FromHookMouseData(HookMouseData(MouseButtonKey.XButton2)),
            "钩子解码必须还原 5 号键");
        Equal((ushort)0, MouseButtonKey.FromHookMouseData(0x00030000), "未知键号必须拒绝");
        Equal((ushort)0, MouseButtonKey.FromHookMouseData(1u), "注入编码的原始值不能被当成钩子上报");

        var tool = new KeyLoopTool();
        tool.Slots[0].Enabled = true;
        tool.Slots[0].Key = "Mouse4";
        tool.Slots[0].DelaySeconds = 1;
        Require(tool.ValidateSlots() is null, "按键循环槽位必须接受鼠标侧键");

        tool.Slots[0].Key = "Mouse9";
        Require(tool.ValidateSlots() is not null, "未定义的鼠标键仍应被拒绝");
    }

    /// <summary>按钩子上报的约定造 MSLLHOOKSTRUCT.mouseData（按钮编号在高字）。</summary>
    private static uint HookMouseData(ushort vk) => (uint)(vk == MouseButtonKey.XButton2 ? 2 : 1) << 16;

    public static void SettingsPageCapturesMouseButtons()
    {
        var root = FindProjectRoot();
        var page = File.ReadAllText(Path.Combine(root, "Tools", "Settings", "SettingsPage.xaml.cs"));
        var markup = File.ReadAllText(Path.Combine(root, "Tools", "Settings", "SettingsPage.xaml"));
        var keyLoopPage = File.ReadAllText(Path.Combine(root, "Tools", "KeyLoop", "KeyLoopPage.xaml.cs"));
        var keyLoopMarkup = File.ReadAllText(Path.Combine(root, "Tools", "KeyLoop", "KeyLoopPage.xaml"));

        Require(markup.Contains("PreviewMouseDown=\"Page_PreviewMouseDown\"", StringComparison.Ordinal),
            "设置页必须接线鼠标侧键捕获");
        Require(keyLoopMarkup.Contains("PreviewMouseDown=\"Page_PreviewMouseDown\"", StringComparison.Ordinal),
            "按键循环槽位必须接线鼠标侧键捕获");
        Require(page.Contains("FormatMouseHotkey", StringComparison.Ordinal) &&
                page.Contains("MouseButton.XButton1", StringComparison.Ordinal),
            "捕获必须同时支持 4/5 号侧键并复用同一格式化入口");
        Require(page.Contains("F13", StringComparison.Ordinal),
            "捕获提示必须说明多侧键鼠标的驱动映射方式");
        Require(keyLoopPage.Contains("MouseButtonKey.KeyName", StringComparison.Ordinal),
            "槽位捕获必须写入规范键名，不能依赖界面文字");
    }

    public static void KeyLoopSlotKeysConflictWithHotkeys()
    {
        HotkeyEntry Hotkey(string name, string key, HotkeyMode mode = HotkeyMode.Toggle)
            => new(string.Empty, name, key, mode);

        var hotkeys = new List<HotkeyEntry>
        {
            Hotkey("连点器按住", "Mouse4", HotkeyMode.Hold),
            Hotkey("按键循环", "F9"),
        };

        var errors = SettingsValidation.ValidateSlotKeyConflicts(hotkeys, [("槽位 1", "Mouse4")]);
        Require(errors.Count == 1 && errors[0].Contains("冲突", StringComparison.Ordinal),
            "槽位键与全局热键同键必须判为冲突");

        Require(SettingsValidation.ValidateSlotKeyConflicts(hotkeys, [("槽位 1", "Ctrl+Mouse4")]).Count == 0,
            "修饰键不同的侧键不应与纯侧键热键冲突");
        Require(SettingsValidation.ValidateSlotKeyConflicts(hotkeys, [("槽位 1", "F9")]).Count == 1,
            "槽位键与按键循环自身热键同键也必须判为冲突");
        Require(SettingsValidation.ValidateSlotKeyConflicts(hotkeys, [("槽位 1", "F1"), ("槽位 2", "F1")]).Count == 1,
            "两个槽位同键必须判为冲突");
        Require(SettingsValidation.ValidateSlotKeyConflicts(hotkeys, [("槽位 1", "Mouse9")]).Count == 1,
            "未定义的侧键必须报无法识别");
        Require(SettingsValidation.ValidateSlotKeyConflicts(hotkeys, [("槽位 1", ""), ("槽位 2", "w")]).Count == 0,
            "空槽位不参与冲突判定");
    }

    /// <summary>
    /// 端到端契约：真实宿主必须把侧键槽位判为可发送，并把原始键名交给输入层。
    /// 只替换“按下并释放”的动作，不安装钩子、不发送真实输入。
    /// </summary>
    public static void KeyLoopEngineRunsMouseSlot()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ToolHost? host = null;
            KeyLoopEngine? engine = null;
            try
            {
                host = new ToolHost(new ToolRegistry());
                var environment = (IKeyLoopEnvironment)host;
                Require(environment.IsSupportedKey("Mouse4"), "宿主必须把 Mouse4 判为可发送的槽位键");
                Require(environment.IsSupportedKey("Mouse5"), "宿主必须把 Mouse5 判为可发送的槽位键");
                Require(!environment.IsSupportedKey("Mouse9"), "未定义的侧键仍必须拒绝");
                Require(environment.IsSupportedKey("F1"), "键盘槽位键的行为不得回退");

                var pressed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                engine = new KeyLoopEngine(host, () => true, (key, _) =>
                {
                    pressed.TrySetResult(key);
                    return Task.CompletedTask;
                });
                Require(engine.Start([new KeyLoopSlot { Enabled = true, Key = "Mouse4", DelaySeconds = 0.1 }]),
                    "侧键槽位必须能启动按键循环");
                Require(pressed.Task.Wait(TimeSpan.FromSeconds(5)), "侧键槽位必须派发到输入层");
                Equal("Mouse4", pressed.Task.Result, "槽位必须把规范键名原样交给输入层");
                Require(engine.Stop(), "侧键槽位必须能停止");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try { engine?.Shutdown(TimeSpan.FromSeconds(2)); } catch (Exception) { }
                if (host is not null)
                {
                    host.Hotkeys.UnregisterAll();
                    host.Sound.Dispose();
                    host.Notification.Dispose();
                    host.Network.Dispose();
                }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(20)), "侧键槽位运行检查超时");
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    /// <summary>仅建立托管绑定；不注册原生热键、不安装钩子。</summary>
    private static HotkeyManager CreateMouseManager(string key, HotkeyMode mode, Func<bool> foreground,
        Action handler, Action? releaseHandler)
        => CreateMouseManager(foreground, (key, mode, handler, releaseHandler));

    /// <summary>同一个侧键上挂多条绑定（不同修饰键），用于验证叠加派发与修饰键释放。</summary>
    private static HotkeyManager CreateMouseManager(Func<bool> foreground,
        params (string Key, HotkeyMode Mode, Action Handler, Action? ReleaseHandler)[] entries)
    {
        var manager = new HotkeyManager(foreground, callback => callback());
        var bindingType = typeof(HotkeyManager).GetNestedType("HoldBinding", BindingFlags.NonPublic)!;
        var map = (IDictionary)typeof(HotkeyManager)
            .GetField("_mouseByVk", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        foreach (var entry in entries)
        {
            var request = new HotkeyRequest
            {
                Key = entry.Key,
                DisplayName = "受控鼠标热键",
                CheckForeground = true,
                Mode = entry.Mode,
                Handler = entry.Handler,
                ReleaseHandler = entry.ReleaseHandler,
            };
            var parsed = HotkeyParser.ParseHold(entry.Key) ?? throw new InvalidOperationException($"{entry.Key} 应可解析");
            var binding = Activator.CreateInstance(bindingType)!;
            bindingType.GetProperty("Request")!.SetValue(binding, request);
            bindingType.GetProperty("Hotkey")!.SetValue(binding, parsed);
            if (map[parsed.Vk] is not IList bindings)
            {
                bindings = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(bindingType))!;
                map[parsed.Vk] = bindings;
            }
            bindings.Add(binding);
        }
        return manager;
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "拾刻.csproj")))
                return directory.FullName;
            var sourceDirectory = Path.Combine(directory.FullName, "源码");
            if (File.Exists(Path.Combine(sourceDirectory, "拾刻.csproj")))
                return sourceDirectory;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("无法定位拾刻.csproj");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
