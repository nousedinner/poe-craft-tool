using ShiKe.Tools.Craft;
using ShiKe.Tools.Clicker;
using ShiKe.Tools.KeyLoop;
using ShiKe.Services;
using ShiKe.Tools.Hideout;
using ShiKe.Tools.Settings;
using ShiKe.Host;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Xml.Linq;

var tests = new (string Name, Action Run)[]
{
    ("装备名参与匹配但不计入词缀数", ItemNameMatchesWithoutIncreasingAffixCount),
    ("一条显式词缀准确计数", OneExplicitAffixIsCounted),
    ("实机弓样本识别为一条基础物理前缀", RealBowSampleIsOnePhysicalPrefix),
    ("Mode3 稀有弓样本识别为主2次1", RealMode3RareBowMatchesTwoPrimaryOneSecondary),
    ("两条显式词缀准确计数", TwoExplicitAffixesAreCounted),
    ("括号说明行不参与匹配", ParentheticalNotesAreIgnored),
    ("同一显式词缀在同一池内不重复计数", OneAffixCannotSatisfyTwoRulesInSamePool),
    ("魔法名称与显式正文不会重复计算同一词缀", MagicNameDoesNotDuplicateExplicitAffix),
    ("同一候选不能同时计入主次池", OneCandidateCannotSatisfyPrimaryAndSecondary),
    ("三个词缀池禁止重复规则", DuplicateRuleAcrossPoolsIsRejected),
    ("旧设置迁移保留原始备份", LegacySettingsMigrationKeepsOriginalBackup),
    ("仅 Hideout 分节不会被误迁移", HideoutOnlySectionIsRecognized),
    ("分节更新不会丢失其他设置", SectionUpdatePreservesOtherSections),
    ("预设名称限制在预设目录内", PresetNamesStayInsidePresetDirectory),
    ("坐标读取失败不保存零坐标", CursorReadFailureDoesNotSaveZeroCoordinate),
    ("Craft 和 Hideout 设置可由统一生命周期加载", ToolSettingsAreLoaded),
    ("Craft 设置保存后可完整往返加载", CraftSettingsRoundTrip),
    ("Clicker 设置保存后可完整往返加载", ClickerSettingsRoundTrip),
    ("Clicker 声明独立 Toggle 和 Hold 热键", ClickerDeclaresBothHotkeys),
    ("KeyLoop 设置兼容新旧延时字段", KeyLoopSettingsRoundTrip),
    ("KeyLoop 启动前校验槽位", KeyLoopValidatesEnabledSlots),
    ("Hideout 命令设置可完整往返", HideoutCommandRoundTrip),
    ("Mode3 改造阶段判定矩阵", Mode3AlterationDecisionMatrix),
    ("Mode3 增幅达标后必须进入富豪", Mode3AugmentationAlwaysProceedsToRegal),
    ("Mode3 启动稀有度判定矩阵", Mode3StartDecisionMatrix),
    ("Mode3 只记录两词缀未达阈值样本", Mode3DiagnosticCapturePolicy),
    ("非法命中数恢复上一次合法选择", InvalidHitCountRestoresPreviousSelection),
    ("剪贴板只接受合法物品文本头", ClipboardItemHeaderValidation),
    ("输入层点击间隔计算", CraftClickIntervalSemantics),
    ("Mode1 完整物品状态变化判定", Mode1ItemStateChangeDetection),
    ("Mode1 状态同步超时有安全下限", Mode1StateSyncTimeoutPolicy),
    ("Mode2/3 通货状态转换矩阵", CurrencyTransitionMatrix),
    ("Hold 组合热键保留修饰键", HoldHotkeyKeepsModifiers),
    ("Hold 支持独立修饰键", HoldSupportsStandaloneModifier),
    ("修饰键状态判定支持左右按键", ModifierStateSupportsLeftAndRightKeys),
    ("公共热键层统一执行前台门禁", HotkeyManagerEnforcesForegroundPolicy),
    ("设置页按语义检测组合热键冲突", SettingsDetectsSemanticHotkeyConflicts),
    ("设置页拒绝路径和控制字符", SettingsRejectsUnsafeValues),
    ("热键重注册失败恢复旧配置", HotkeyTransactionRollsBackOnFailure),
    ("SettingsTool 使用 host 分节完整往返", SettingsToolHostSectionRoundTrip),
    ("宿主汇总七个统一热键请求", ToolHostBuildsCompleteHotkeySet),
    ("音效扫描和路径解析限制在 sounds 目录", SoundFilesStayInsideSoundDirectory),
    ("网络版本比较使用 Version 语义", NetworkVersionComparisonIsNumeric),
    ("匿名统计载荷不包含本机数据", PingPayloadContainsOnlyFixedFields),
    ("单文件发布配置保留 WPF 和资源安全选项", PublishProfileKeepsSafeWpfOptions),
    ("空目标进程采用 fail-closed", EmptyTargetProcessIsNotForeground),
    ("程序集版本与本次修复版本一致", AssemblyVersionIsCurrent),
    ("Craft 常驻任务可以正式关闭", CraftEngineCanShutdownWhileIdle),
    ("Clicker 常驻任务可暂停停止并关闭", ClickerEngineLifecycleIsSafe),
    ("KeyLoop 独立槽位可暂停停止并关闭", KeyLoopEngineLifecycleIsSafe),
    ("Craft 运行配置使用深拷贝快照", CraftRulesSnapshotIsIndependent),
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT total={tests.Length}, passed={tests.Length - failed}, failed={failed}");
return failed == 0 ? 0 : 1;

static void ItemNameMatchesWithoutIncreasingAffixCount()
{
    var parsed = AffixEngine.ParseItem(MagicItemWithOneAffix());
    Equal(1, parsed.ExplicitAffixCount, "装备名/基础类型不得增加词缀数");

    var rules = Rules("霜语");
    var result = AffixEngine.CheckAffixes(parsed, rules);
    Equal(1, result.PrimaryHits, "装备名必须保留宽松匹配能力");
}

static void OneExplicitAffixIsCounted()
{
    var parsed = AffixEngine.ParseItem(MagicItemWithOneAffix());
    Equal(1, parsed.ExplicitAffixCount, "一条前缀应计为 1");
    Equal(1, AffixEngine.GetAffixCount(MagicItemWithOneAffix()), "兼容计数 API 应返回真实词缀数");
}

static void RealBowSampleIsOnePhysicalPrefix()
{
    const string text = """
        物品类别: 弓
        稀 有 度: 魔法
        锻炼的脊弓
        -----
        弓
        物理伤害: 78-188 (augmented)
        攻击暴击率: 6.50%
        每秒攻击次数: 1.40
        ------------
        需求:
        等级: 64
        敏捷: 212 (unmet)
        ---------------
        ## 插槽: W
        ## 物品等级: 86
        { 前缀属性 "锻炼的" (等阶：2) — 伤害, 物理, 攻击 }
        该装备附加 40(30-40) - 73(63-73) 基础物理伤害
        ----------------------------------
        出售获得通货:非绑定
        """;

    var parsed = AffixEngine.ParseItem(text);
    Equal(1, parsed.ExplicitAffixCount, "实机样本应识别为恰好一条显式词缀");
    var result = AffixEngine.CheckAffixes(parsed, Rules("基础物理"));
    Equal(1, result.PrimaryHits, "基础物理规则应命中显式词缀描述");
}

static void RealMode3RareBowMatchesTwoPrimaryOneSecondary()
{
    const string text = """
        物品类别: 弓
        稀 有 度: 稀有
        硫石 禁域
        脊弓
        --
        弓
        物理伤害: 38-115
        火焰，冰霜，闪电伤害: 93-191 (augmented), 11-208 (augmented)
        攻击暴击率: 6.50%
        每秒攻击次数: 1.50 (augmented)
        ------------------------
        需求:
        等级: 64
        敏捷: 212 (unmet)
        ---------------
        ## 插槽: W
        ## 物品等级: 86
        { 前缀属性 "焚烧的" (等阶：4) — 伤害, 元素, 火焰, 攻击 }
        该装备附加 93(85-115) - 191(172-200) 基础火焰伤害
        { 前缀属性 "注入的" (等阶：5) — 伤害, 元素, 攻击 }
        攻击技能的元素伤害提高 45(36-51)%
        { 前缀属性 "电弧的" (等阶：5) — 伤害, 元素, 闪电, 攻击 }
        该装备附加 11(11-14) - 208(208-242) 基础闪电伤害
        { 后缀属性 "技巧之" (等阶：5) — 攻击, 速度 }
        攻击速度加快 7(5-7)%
        --------------
        出售获得通货:非绑定
        """;

    var parsed = AffixEngine.ParseItem(text);
    Equal(ItemRarity.Rare, parsed.Rarity, "实机样本稀有度应为稀有");
    Equal(4, parsed.ExplicitAffixCount, "实机样本应有三前缀一后缀");

    var rules = new CraftRules { PrimaryHitCount = 2, SecondaryHitCount = 1 };
    foreach (var rule in new[] { "无情", "独裁", "迸出", "晶化", "汽化", "焦化", "基础物理", "基础火", "基础电" })
        rules.PrimaryAffixes.Add(new AffixRule { Text = rule });
    foreach (var rule in new[] { "速度", "暴", "命中", "敏捷" })
        rules.SecondaryAffixes.Add(new AffixRule { Text = rule });

    var result = AffixEngine.CheckAffixes(parsed, rules);
    Equal(2, result.PrimaryHits, "基础火和基础电应各命中一个实际前缀");
    Equal(1, result.SecondaryHits, "速度应命中实际后缀");
    True(result.MeetsFinalRules(rules), "主2次1样本必须通过终检");
}

static void TwoExplicitAffixesAreCounted()
{
    const string text = """
        物品类别: 单手剑
        稀 有 度: 魔法
        霜语
        皇家短剑
        --------
        { 前缀属性 "急冻的" (等阶：6) — 伤害, 元素, 冰霜, 攻击 }
        该装备附加 52 - 81 基础冰霜伤害
        { 后缀属性 "技艺之" (等阶：3) — 攻击, 速度 }
        增加 12% 攻击速度
        --------
        物品等级: 84
        """;

    var parsed = AffixEngine.ParseItem(text);
    Equal(2, parsed.ExplicitAffixCount, "一前缀一后缀应计为 2");
}

static void ParentheticalNotesAreIgnored()
{
    var parsed = AffixEngine.ParseItem(MagicItemWithOneAffix());
    False(parsed.MatchLines.Any(line => line.Contains("吸取", StringComparison.Ordinal)), "ASCII 括号说明不应进入匹配语料");
    False(parsed.MatchLines.Any(line => line.Contains("中文说明", StringComparison.Ordinal)), "中文括号说明不应进入匹配语料");
}

static void OneAffixCannotSatisfyTwoRulesInSamePool()
{
    var parsed = AffixEngine.ParseItem(MagicItemWithOneAffix());
    var rules = new CraftRules { PrimaryHitCount = 2 };
    rules.PrimaryAffixes.Add(new AffixRule { Text = "急冻的" });
    rules.PrimaryAffixes.Add(new AffixRule { Text = "冰霜伤害" });

    var result = AffixEngine.CheckAffixes(parsed, rules);
    Equal(1, result.PrimaryHits, "同一词缀的属性头和描述必须作为一个逻辑候选");
}

static void MagicNameDoesNotDuplicateExplicitAffix()
{
    const string text = """
        物品类别: 弓
        稀 有 度: 魔法
        迸出的 脊弓
        --------
        { 前缀属性 "迸出的" (等阶：3) — 伤害, 物理, 攻击 }
        物理伤害提高 55%
        该装备附加 8 - 14 基础物理伤害
        { 后缀属性 "壁垒之" (等阶：7) — 防御 }
        +12 点护甲
        --------
        物品等级: 86
        """;

    var parsed = AffixEngine.ParseItem(text);
    Equal(2, parsed.ExplicitAffixCount, "样本应包含两条真实词缀");

    var rules = new CraftRules { PrimaryHitCount = 2 };
    rules.PrimaryAffixes.Add(new AffixRule { Text = "迸出" });
    rules.PrimaryAffixes.Add(new AffixRule { Text = "基础物理" });

    var result = AffixEngine.CheckAffixes(parsed, rules);
    Equal(1, result.PrimaryHits, "魔法名称和显式正文描述的是同一物理词缀，不得算成两次命中");
    Equal("迸出", result.MatchedPrimaryRules.Single(), "应优先把规则绑定到显式词缀块");
}

static void OneCandidateCannotSatisfyPrimaryAndSecondary()
{
    var parsed = AffixEngine.ParseItem(MagicItemWithOneAffix());
    var rules = new CraftRules { PrimaryHitCount = 1, SecondaryHitCount = 1 };
    rules.PrimaryAffixes.Add(new AffixRule { Text = "冰霜伤害" });
    rules.SecondaryAffixes.Add(new AffixRule { Text = "急冻的" });

    var result = AffixEngine.CheckAffixes(parsed, rules);
    Equal(1, result.PrimaryHits, "主池应先消费该显式词缀");
    Equal(0, result.SecondaryHits, "次池不得重复消费同一显式词缀");
}

static void DuplicateRuleAcrossPoolsIsRejected()
{
    var rules = new CraftRules { PrimaryHitCount = 1 };
    rules.PrimaryAffixes.Add(new AffixRule { Text = "基础物理" });
    rules.ExcludeAffixes.Add(new AffixRule { Text = "基础物理" });

    var (ok, message) = rules.Validate();
    False(ok, "同一规则不得同时存在于命中池和排除池");
    True(message.Contains("基础物理", StringComparison.Ordinal), "校验错误应指出冲突规则");
}

static void LegacySettingsMigrationKeepsOriginalBackup()
{
    WithTempDirectory(directory =>
    {
        const string original = """
            {
              "hotkey_start": "F3",
              "delay_ms": 77,
              "clicker_hotkey": "F10",
              "clicker_hold_hotkey": "F12",
              "clicker_interval_ms": 44,
              "clicker_button": "right",
              "hideout_enabled": true,
              "target_process": "PathOfExile_x64.exe"
            }
            """;
        var settingsPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(settingsPath, original);

        var storage = new StorageService(directory);
        var migrated = storage.LoadSettings();

        True(File.Exists(settingsPath), "迁移后 settings.json 必须存在");
        Equal(original, File.ReadAllText(settingsPath + ".bak"), "备份必须保留迁移前原始字节");
        Equal("F3", migrated["host"]?["hotkeys"]?["start"]?.GetValue<string>() ?? "", "启动热键迁移错误");
        Equal(77, migrated["craft"]?["delay_ms"]?.GetValue<int>() ?? 0, "Craft 延迟迁移错误");
        Equal("F10", migrated["clicker"]?["hotkey"]?.GetValue<string>() ?? "", "Clicker 切换热键迁移错误");
        Equal("F12", migrated["clicker"]?["hold_hotkey"]?.GetValue<string>() ?? "", "Clicker Hold 热键迁移错误");
        Equal(44, migrated["clicker"]?["interval_ms"]?.GetValue<int>() ?? 0, "Clicker 间隔迁移错误");
        Equal("right", migrated["clicker"]?["button"]?.GetValue<string>() ?? "", "Clicker 鼠标键迁移错误");
        Equal(true, migrated["hideout"]?["enabled"]?.GetValue<bool>() ?? false, "Hideout 开关迁移错误");

        var secondLoad = storage.LoadSettings();
        Equal(77, secondLoad["craft"]?["delay_ms"]?.GetValue<int>() ?? 0, "第二次加载应保持迁移结果");
    });
}

static void HideoutOnlySectionIsRecognized()
{
    WithTempDirectory(directory =>
    {
        var settingsPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(settingsPath, "{\"hideout\":{\"enabled\":true,\"hotkey\":\"F4\"}}");

        var storage = new StorageService(directory);
        var settings = storage.LoadSettings();

        Equal(true, settings["hideout"]?["enabled"]?.GetValue<bool>() ?? false, "Hideout 分节应原样加载");
        False(File.Exists(settingsPath + ".bak"), "合法分节文件不应触发迁移备份");
    });
}

static void SectionUpdatePreservesOtherSections()
{
    WithTempDirectory(directory =>
    {
        var storage = new StorageService(directory);
        storage.SaveSettings(new JsonObject
        {
            ["craft"] = new JsonObject { ["delay_ms"] = 66 },
            ["hideout"] = new JsonObject { ["enabled"] = false },
        });

        storage.UpdateSettings(settings => settings["host"] = new JsonObject { ["target_process"] = "PathOfExile_x64.exe" });
        var loaded = storage.LoadSettings();

        Equal(66, loaded["craft"]?["delay_ms"]?.GetValue<int>() ?? 0, "更新 host 不得丢失 craft");
        Equal(false, loaded["hideout"]?["enabled"]?.GetValue<bool>() ?? true, "更新 host 不得丢失 hideout");
        Equal("PathOfExile_x64.exe", loaded["host"]?["target_process"]?.GetValue<string>() ?? "", "host 更新未保存");
    });
}

static void PresetNamesStayInsidePresetDirectory()
{
    WithTempDirectory(directory =>
    {
        var storage = new StorageService(directory);
        True(StorageService.TryValidatePresetName("测试 词缀", out _), "合法中文预设名应通过");
        storage.SavePreset("测试 词缀", new JsonObject { ["primary_hit_count"] = 1 });
        True(storage.LoadPreset("测试 词缀") is not null, "合法预设应可保存并读取");

        False(StorageService.TryValidatePresetName("../escape", out _), "目录穿越名称必须被拒绝");
        False(StorageService.TryValidatePresetName("CON", out _), "Windows 保留设备名必须被拒绝");
        False(StorageService.TryValidatePresetName("尾点.", out _), "尾点名称必须被拒绝");
        Throws<ArgumentException>(() => storage.SavePreset("../escape", new JsonObject()),
            "服务层必须独立阻止目录穿越");
        False(File.Exists(Path.Combine(directory, "escape.json")), "预设目录外不得产生文件");
    });
}

static void CursorReadFailureDoesNotSaveZeroCoordinate()
{
    WithTempDirectory(directory =>
    {
        Point? cursor = null;
        var storage = new StorageService(directory);
        var recorder = new CoordinateRecorder(storage, () => cursor);
        var slot = new CoordinateSlot { SlotId = "item", DisplayName = "装备位置" };
        var failures = 0;
        recorder.RecordingFailed += (_, _) => failures++;

        recorder.StartRecording(slot);
        recorder.OnRecordHotkey();
        True(recorder.IsRecording, "读取失败后应保留录制状态供重试");
        Equal(1, failures, "读取失败应发出一次明确事件");
        Equal(0, storage.LoadCoordinates().Count, "读取失败不得写入 (0,0) 或其他坐标");

        cursor = new Point(123, 456);
        recorder.OnRecordHotkey();
        False(recorder.IsRecording, "成功重试后应退出录制状态");
        var saved = storage.LoadCoordinates()["item"];
        Equal(new Point(123, 456), saved, "成功重试应保存真实坐标");
    });
}

static void ToolSettingsAreLoaded()
{
    var craft = new CraftTool();
    craft.LoadSettings(JsonSerializer.SerializeToElement(new
    {
        delay_ms = 77,
        sound_enabled = false,
        popup_enabled = false,
        clipboard_unchanged_threshold = 12,
        selected_sound = "custom.mp3",
        mode2_scour_alch = true,
        use_exalt = true,
    }));
    Equal(77, craft.DelayMs, "Craft delay 未加载");
    Equal(false, craft.SoundEnabled, "Craft sound 未加载");
    Equal(false, craft.PopupEnabled, "Craft popup 未加载");
    Equal(12, craft.ExhaustionThreshold, "Craft 耗尽阈值未加载");
    Equal("custom.mp3", craft.SelectedSound, "Craft 音效未加载");
    Equal(true, craft.Mode2ScourAlch, "Craft Mode2 子模式未加载");
    Equal(true, craft.UseExalt, "Craft 崇高开关未加载");

    var hideout = new HideoutTool();
    hideout.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = false, hotkey = "F4", command = "/menagerie" }));
    Equal(false, hideout.IsEnabled, "Hideout false 设置未加载");
    Equal("F4", hideout.Hotkey, "Hideout 热键未加载");
    Equal("/menagerie", hideout.Command, "Hideout 命令未加载");
}

static void CraftSettingsRoundTrip()
{
    var source = new CraftTool
    {
        DelayMs = 123,
        SoundEnabled = false,
        PopupEnabled = false,
        ExhaustionThreshold = 17,
        SelectedSound = "roundtrip.wav",
        Mode2ScourAlch = true,
        UseExalt = true,
    };

    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
        source.SaveSettings(writer);

    var restored = new CraftTool();
    using var document = JsonDocument.Parse(stream.ToArray());
    restored.LoadSettings(document.RootElement);

    Equal(123, restored.DelayMs, "延迟往返失败");
    Equal(false, restored.SoundEnabled, "声音开关往返失败");
    Equal(false, restored.PopupEnabled, "弹窗开关往返失败");
    Equal(17, restored.ExhaustionThreshold, "耗尽阈值往返失败");
    Equal("roundtrip.wav", restored.SelectedSound, "音效文件往返失败");
    Equal(true, restored.Mode2ScourAlch, "Mode2 子模式往返失败");
    Equal(true, restored.UseExalt, "崇高开关往返失败");
}

static void ClickerSettingsRoundTrip()
{
    var source = new ClickerTool
    {
        Hotkey = "Ctrl+F8",
        HoldHotkey = "Shift+F11",
        IntervalMs = 47,
        MouseButton = ClickerMouseButton.Right,
        NotificationsEnabled = true,
    };

    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
        source.SaveSettings(writer);

    var restored = new ClickerTool();
    using var document = JsonDocument.Parse(stream.ToArray());
    restored.LoadSettings(document.RootElement);

    Equal("Ctrl+F8", restored.Hotkey, "Clicker 切换热键往返失败");
    Equal("Shift+F11", restored.HoldHotkey, "Clicker 按住热键往返失败");
    Equal(47, restored.IntervalMs, "Clicker 间隔往返失败");
    Equal(ClickerMouseButton.Right, restored.MouseButton, "Clicker 鼠标键往返失败");
    Equal(true, restored.NotificationsEnabled, "Clicker 通知开关往返失败");

    restored.LoadSettings(JsonSerializer.SerializeToElement(new { interval_ms = -5, button = "unknown" }));
    Equal(10, restored.IntervalMs, "Clicker 非法低间隔必须收敛到 10ms");
    Equal(ClickerMouseButton.Left, restored.MouseButton, "Clicker 未知按键必须回退左键");
}

static void ClickerDeclaresBothHotkeys()
{
    var tool = new ClickerTool { Hotkey = "Ctrl+F8", HoldHotkey = "Shift+F11" };
    var requests = tool.GetHotkeyRequests();
    Equal(2, requests.Count, "Clicker 必须始终声明两个独立热键");
    Equal("Ctrl+F8", requests[0].Key, "Clicker Toggle 热键错误");
    Equal(HotkeyMode.Toggle, requests[0].Mode, "Clicker 第一个热键必须是 Toggle");
    Equal(true, requests[0].CheckForeground, "Clicker Toggle 必须检查游戏前台");
    Equal("Shift+F11", requests[1].Key, "Clicker Hold 热键错误");
    Equal(HotkeyMode.Hold, requests[1].Mode, "Clicker 第二个热键必须是 Hold");
    Equal(true, requests[1].CheckForeground, "Clicker Hold 必须检查游戏前台");
    True(requests[1].ReleaseHandler is not null, "Clicker Hold 必须提供松开停止回调");
}

static void KeyLoopSettingsRoundTrip()
{
    var source = new KeyLoopTool { Hotkey = "Ctrl+F9", NotificationsEnabled = true };
    source.Slots[0].Enabled = true;
    source.Slots[0].Key = "q";
    source.Slots[0].DelaySeconds = 0.4;
    source.Slots[6].Enabled = true;
    source.Slots[6].Key = "F3";
    source.Slots[6].DelaySeconds = 2.5;

    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
        source.SaveSettings(writer);

    var restored = new KeyLoopTool();
    using var document = JsonDocument.Parse(stream.ToArray());
    restored.LoadSettings(document.RootElement);
    Equal("Ctrl+F9", restored.Hotkey, "KeyLoop 热键往返失败");
    Equal(true, restored.NotificationsEnabled, "KeyLoop 通知设置往返失败");
    Equal(true, restored.Slots[0].Enabled, "KeyLoop 槽位启用状态往返失败");
    Equal("q", restored.Slots[0].Key, "KeyLoop 槽位按键往返失败");
    Equal(0.4, restored.Slots[0].DelaySeconds, "KeyLoop 新 delay_s 字段往返失败");
    Equal("F3", restored.Slots[6].Key, "KeyLoop 后五个槽位必须保存");

    var legacy = new KeyLoopTool();
    legacy.LoadSettings(JsonSerializer.SerializeToElement(new
    {
        hotkey = "F10",
        slots = new[] { new { enabled = true, key = "w", delay = 1.7 } },
    }));
    Equal("w", legacy.Slots[0].Key, "KeyLoop 旧槽位按键读取失败");
    Equal(1.7, legacy.Slots[0].DelaySeconds, "KeyLoop 旧 delay 字段读取失败");

    var request = restored.GetHotkeyRequests().Single();
    Equal(HotkeyMode.Toggle, request.Mode, "KeyLoop 热键必须是 Toggle");
    Equal(true, request.CheckForeground, "KeyLoop 热键必须检查游戏前台");
}

static void KeyLoopValidatesEnabledSlots()
{
    var tool = new KeyLoopTool();
    True(tool.ValidateSlots() is not null, "没有启用槽位时必须拒绝启动");
    tool.Slots[0].Enabled = true;
    True(tool.ValidateSlots()?.Contains("尚未设置", StringComparison.Ordinal) == true,
        "启用但未设置按键时必须指出槽位错误");
    tool.Slots[0].Key = "not-a-key";
    True(tool.ValidateSlots()?.Contains("无法识别", StringComparison.Ordinal) == true,
        "无法识别的按键必须拒绝启动");
    tool.Slots[0].Key = "q";
    tool.Slots[0].DelaySeconds = 0.05;
    True(tool.ValidateSlots()?.Contains("延时", StringComparison.Ordinal) == true,
        "越界延时必须拒绝启动");
    tool.Slots[0].DelaySeconds = 0.1;
    True(tool.ValidateSlots() is null, "合法槽位配置应允许启动");
}

static void HideoutCommandRoundTrip()
{
    var source = new HideoutTool();
    source.LoadSettings(JsonSerializer.SerializeToElement(new
    {
        enabled = true,
        hotkey = "F4",
        command = "/menagerie",
    }));

    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
        source.SaveSettings(writer);
    var restored = new HideoutTool();
    using var document = JsonDocument.Parse(stream.ToArray());
    restored.LoadSettings(document.RootElement);

    Equal(true, restored.IsEnabled, "Hideout 启用状态往返失败");
    Equal("F4", restored.Hotkey, "Hideout 热键往返失败");
    Equal("/menagerie", restored.Command, "Hideout 自定义命令往返失败");
    restored.SetCommand("   ");
    Equal(SettingsDefaults.HideoutCommand, restored.Command, "空命令必须回退 /hideout");
}

static void Mode3AlterationDecisionMatrix()
{
    Equal(Mode3MagicDecision.ProceedToRegal, CraftDecisions.AfterAlteration(2, 2, 2), "两词缀达阈值应去富豪");
    Equal(Mode3MagicDecision.ContinueAlteration, CraftDecisions.AfterAlteration(2, 1, 2), "两词缀不足阈值应继续改造");
    Equal(Mode3MagicDecision.UseAugmentation, CraftDecisions.AfterAlteration(1, 1, 2), "一词缀有命中应增幅");
    Equal(Mode3MagicDecision.ContinueAlteration, CraftDecisions.AfterAlteration(1, 0, 2), "一词缀无命中应继续改造");
}

static void Mode3AugmentationAlwaysProceedsToRegal()
{
    Equal(Mode3MagicDecision.ProceedToRegal, CraftDecisions.AfterAugmentation(2, 2), "增幅达阈值必须去富豪");
    Equal(Mode3MagicDecision.ProceedToRegal, CraftDecisions.AfterAugmentation(3, 2), "即使已满足最终命中也不能在富豪前成功");
    Equal(Mode3MagicDecision.ContinueAlteration, CraftDecisions.AfterAugmentation(1, 2), "增幅不足阈值应继续改造");
}

static void Mode3StartDecisionMatrix()
{
    Equal(Mode3StartDecision.ContinueFromNormal, CraftDecisions.BeforeMode3(ItemRarity.Normal),
        "普通物品应直接进入蜕变阶段");
    Equal(Mode3StartDecision.ScourFirst, CraftDecisions.BeforeMode3(ItemRarity.Magic),
        "魔法物品应先重铸");
    Equal(Mode3StartDecision.ScourFirst, CraftDecisions.BeforeMode3(ItemRarity.Rare),
        "稀有物品应先重铸");
    Equal(Mode3StartDecision.StopUnsupported, CraftDecisions.BeforeMode3(ItemRarity.Unique),
        "传奇物品必须停止");
    Equal(Mode3StartDecision.StopUnsupported, CraftDecisions.BeforeMode3(ItemRarity.Unknown),
        "未知稀有度必须停止");
}

static void Mode3DiagnosticCapturePolicy()
{
    True(CraftDecisions.ShouldCaptureMode3Miss(2, 1, 2, false),
        "两词缀但命中不足时应保存诊断样本");
    False(CraftDecisions.ShouldCaptureMode3Miss(2, 2, 2, false),
        "达到阈值时不得保存误报样本");
    False(CraftDecisions.ShouldCaptureMode3Miss(1, 1, 2, false),
        "一词缀进入增幅分支，不属于本次漏判诊断范围");
    False(CraftDecisions.ShouldCaptureMode3Miss(2, 1, 2, true),
        "排除词缀已有明确继续原因，不属于疑似漏判");
}

static void InvalidHitCountRestoresPreviousSelection()
{
    Equal(2, CraftDecisions.ResolveHitCountSelection(CraftMode.AltAug, 2, 3, 0),
        "Mode2 非法选择 3 时应恢复上一次合法值 2");
    Equal(1, CraftDecisions.ResolveHitCountSelection(CraftMode.AltAugRegal, 1, 2, 2),
        "Mode3 总数超过 3 时应恢复当前池上一次合法值");
    Equal(2, CraftDecisions.ResolveHitCountSelection(CraftMode.AltAugRegal, 1, 2, 1),
        "Mode3 总数等于 3 时应接受新选择");
    Equal(3, CraftDecisions.ResolveHitCountSelection(CraftMode.Single, 1, 3, 3),
        "Mode1 不应套用 Mode2/3 总命中数限制");
}

static void ClipboardItemHeaderValidation()
{
    True(ClipboardHelper.IsItemText("物品类别: 弓\n稀 有 度: 稀有"), "中文物品头应被接受");
    True(ClipboardHelper.IsItemText("\r\nItem Class: Bows\nRarity: Rare"), "英文物品头应被接受");
    False(ClipboardHelper.IsItemText("这是一段来自其他应用的旧文本"), "普通文本不得进入物品判定");
}

static void CraftClickIntervalSemantics()
{
    Equal(150, InputSimulator.CalculateRemainingClickDelay(200, 50), "已过 50ms 时应再等 150ms");
    Equal(0, InputSimulator.CalculateRemainingClickDelay(200, 250), "已超过间隔时不应额外等待");
    Equal(0, InputSimulator.CalculateRemainingClickDelay(0, 0), "零间隔不得产生负等待");
}

static void Mode1ItemStateChangeDetection()
{
    const string baseline = "物品类别: 弓\r\n稀 有 度: 魔法\r\n物理伤害: 10-20\r\n";
    const string sameWithDifferentLineEndings = "  物品类别: 弓\n稀 有 度: 魔法\n物理伤害: 10-20  ";
    const string changed = "物品类别: 弓\n稀 有 度: 魔法\n物理伤害: 11-21";

    False(CraftStateSync.HasItemStateChanged(baseline, sameWithDifferentLineEndings),
        "仅换行和首尾空白不应被误认为服务器状态变化");
    True(CraftStateSync.HasItemStateChanged(baseline, changed),
        "任一物品字段改变都应解除 Mode1 状态门禁");
    False(CraftStateSync.HasItemStateChanged(baseline, ""),
        "空剪贴板不得解除 Mode1 状态门禁");
}

static void Mode1StateSyncTimeoutPolicy()
{
    Equal(900, CraftStateSync.CalculateChangeTimeoutMs(33), "33ms 正常配置应保留 900ms 异常重试窗口");
    Equal(900, CraftStateSync.CalculateChangeTimeoutMs(-1), "异常负数配置不得缩短安全窗口");
    Equal(1300, CraftStateSync.CalculateChangeTimeoutMs(800), "较高延迟应得到额外同步窗口");
}

static void CurrencyTransitionMatrix()
{
    var normal = new CraftItemState(ItemRarity.Normal, 0);
    var magic1 = new CraftItemState(ItemRarity.Magic, 1);
    var magic2 = new CraftItemState(ItemRarity.Magic, 2);
    var rare3 = new CraftItemState(ItemRarity.Rare, 3);
    var rare4 = new CraftItemState(ItemRarity.Rare, 4);

    True(CraftStateSync.CheckTransition(CraftCurrencyOperation.Alteration, magic1, magic2, true).Accepted,
        "改造应接受魔法→魔法且文本已变化");
    False(CraftStateSync.CheckTransition(CraftCurrencyOperation.Alteration, magic1, magic1, false).Accepted,
        "文本未变时不得确认改造成功");
    True(CraftStateSync.CheckTransition(CraftCurrencyOperation.Augmentation, magic1, magic2, true).Accepted,
        "增幅必须把一条魔法词缀变为两条");
    False(CraftStateSync.CheckTransition(CraftCurrencyOperation.Augmentation, magic2, magic2, true).Accepted,
        "已有两条词缀时不得误认为增幅成功");
    True(CraftStateSync.CheckTransition(CraftCurrencyOperation.Scouring, rare4, normal, true).Accepted,
        "重铸必须转为零显式词缀的普通物品");
    True(CraftStateSync.CheckTransition(CraftCurrencyOperation.Transmutation, normal, magic1, true).Accepted,
        "蜕变必须把普通物品转为魔法物品");
    True(CraftStateSync.CheckTransition(CraftCurrencyOperation.Alchemy, normal, rare4, true).Accepted,
        "点金必须把普通物品转为稀有物品");
    True(CraftStateSync.CheckTransition(CraftCurrencyOperation.Regal, magic2, rare3, true).Accepted,
        "富豪必须把魔法物品转为稀有并增加一条词缀");
    False(CraftStateSync.CheckTransition(CraftCurrencyOperation.Regal, magic2, rare4, true).Accepted,
        "富豪词缀数跳变异常不得通过");
    True(CraftStateSync.CheckTransition(CraftCurrencyOperation.Exalted, rare3, rare4, true).Accepted,
        "崇高必须在稀有物品上增加一条词缀");
}

static void HoldHotkeyKeepsModifiers()
{
    var parsed = HotkeyParser.ParseHold("Ctrl+F11") ?? throw new InvalidOperationException("Ctrl+F11 应可解析");
    Equal(HotkeyParser.MOD_CONTROL, parsed.Modifiers, "Hold 不得丢失 Ctrl 修饰键");
    Equal((ushort)0x7A, parsed.Vk, "F11 VK 错误");
    True(HotkeyParser.ParseHold("A+B") is null, "两个主键组成的 chord 应被拒绝");
}

static void HoldSupportsStandaloneModifier()
{
    var control = HotkeyParser.ParseHold("Ctrl") ?? throw new InvalidOperationException("独立 Ctrl 应可解析为 Hold");
    Equal((uint)0, control.Modifiers, "独立 Ctrl 不应同时要求自身作为修饰键");
    Equal((ushort)0x11, control.Vk, "独立 Ctrl 应映射到通用 VK_CONTROL");
    True(HotkeyParser.Parse("Ctrl") is null, "Toggle 独立修饰键仍应拒绝");
}

static void ModifierStateSupportsLeftAndRightKeys()
{
    True(HotkeyParser.AreModifiersPressed(HotkeyParser.MOD_CONTROL, vk => vk == 0xA2), "左 Ctrl 应满足 Control");
    True(HotkeyParser.AreModifiersPressed(HotkeyParser.MOD_CONTROL, vk => vk == 0xA3), "右 Ctrl 应满足 Control");
    False(HotkeyParser.AreModifiersPressed(HotkeyParser.MOD_CONTROL | HotkeyParser.MOD_SHIFT, vk => vk == 0xA2),
        "缺少 Shift 时组合修饰键不得满足");
}

static void HotkeyManagerEnforcesForegroundPolicy()
{
    var foreground = false;
    var invoked = 0;
    var rejected = 0;
    var manager = new HotkeyManager(() => foreground);
    var guarded = new HotkeyRequest
    {
        Key = "F5",
        DisplayName = "受保护操作",
        CheckForeground = true,
        Mode = HotkeyMode.Toggle,
        Handler = () => invoked++,
        ForegroundRejectedHandler = () => rejected++,
    };

    False(manager.TryInvoke(guarded), "非目标前台必须拦截受保护热键");
    Equal(0, invoked, "被拦截热键不得执行主回调");
    Equal(1, rejected, "被拦截热键应执行可选拒绝回调");

    foreground = true;
    True(manager.TryInvoke(guarded), "目标前台应放行受保护热键");
    Equal(1, invoked, "放行后应执行一次主回调");

    foreground = false;
    var unguarded = new HotkeyRequest
    {
        Key = "F6",
        DisplayName = "停止操作",
        CheckForeground = false,
        Mode = HotkeyMode.Toggle,
        Handler = () => invoked++,
    };
    True(manager.TryInvoke(unguarded), "停止类热键不得受前台限制");
    Equal(2, invoked, "停止类热键应在非目标前台执行");

    var failingDetector = new HotkeyManager(() => throw new InvalidOperationException("模拟检测失败"));
    False(failingDetector.TryInvoke(guarded), "前台检测异常必须 fail-closed");
}

static void EmptyTargetProcessIsNotForeground()
{
    var detector = new ForegroundDetector { TargetProcess = "" };
    False(detector.IsTargetForeground(), "空目标不得允许危险输入");
}

static void SettingsDetectsSemanticHotkeyConflicts()
{
    var hotkeys = new HotkeySettings(
        "Ctrl+Alt+F5", "Alt+Ctrl+F5", "F7", "F8", "F11", "F9", "F2");
    var errors = SettingsValidation.ValidateHotkeys(hotkeys);
    True(errors.Any(error => error.Contains("冲突", StringComparison.Ordinal)),
        "修饰键顺序不同的同一触发器必须判为冲突");

    var standaloneToggle = hotkeys with { CraftStart = "Ctrl", CraftStop = "F6" };
    True(SettingsValidation.ValidateHotkeys(standaloneToggle)
            .Any(error => error.Contains("启动洗装", StringComparison.Ordinal)),
        "Toggle 不得接受独立修饰键");

    var standaloneHold = hotkeys with
    {
        CraftStart = "F5",
        CraftStop = "F6",
        ClickerHold = "Ctrl",
    };
    Equal(0, SettingsValidation.ValidateHotkeys(standaloneHold).Count,
        "连点器 Hold 应允许独立修饰键");
}

static void SettingsRejectsUnsafeValues()
{
    var draft = new SettingsDraft(
        new HotkeySettings("F5", "F6", "F7", "F8", "F11", "F9", "F2"),
        "..\\PathOfExile.exe", true, true, true, "..\\outside.mp3", false, false,
        true, "/hideout\n/exit");
    var errors = SettingsValidation.Validate(draft);
    True(errors.Any(error => error.Contains("目标进程", StringComparison.Ordinal)),
        "目标进程路径必须被拒绝");
    True(errors.Any(error => error.Contains("音效", StringComparison.Ordinal)),
        "音效目录穿越必须被拒绝");
    True(errors.Any(error => error.Contains("控制字符", StringComparison.Ordinal)),
        "回城命令换行必须被拒绝");
}

static void HotkeyTransactionRollsBackOnFailure()
{
    var old = new HotkeySettings("F5", "F6", "F7", "F8", "F11", "F9", "F2");
    var candidate = old with { CraftStart = "F10" };
    var current = old;
    var attempts = 0;
    var result = HotkeySettingsTransaction.TryApply(
        old,
        candidate,
        value => current = value,
        () =>
        {
            attempts++;
            return current == candidate ? ["F10 被其他程序占用"] : [];
        });

    False(result.Success, "候选热键注册失败时事务不得成功");
    Equal(old, current, "注册失败后必须恢复全部旧热键字段");
    Equal(2, attempts, "失败后必须再次注册恢复的旧热键集合");
}

static void SettingsToolHostSectionRoundTrip()
{
    RunInSta(() =>
    {
        var craft = new CraftTool();
        var clicker = new ClickerTool();
        var keyLoop = new KeyLoopTool();
        var hideout = new HideoutTool();
        var settingsTool = new SettingsTool(craft, clicker, keyLoop, hideout);
        var registry = new ToolRegistry();
        registry.Register(craft);
        registry.Register(clicker);
        registry.Register(keyLoop);
        registry.Register(hideout);
        registry.Register(settingsTool);
        var host = new ToolHost(registry);
        foreach (var tool in registry.Tools) tool.Initialize(host);

        using var document = JsonDocument.Parse("""
            {
              "target_process": "PathOfExile_x64.exe",
              "auto_detect_poe": false,
              "hotkeys": { "start": "Ctrl+F5", "stop": "F6", "coordinate": "F10" }
            }
            """);
        settingsTool.LoadSettings(document.RootElement);

        Equal("PathOfExile_x64.exe", host.Foreground.TargetProcess!, "目标进程未加载");
        False(host.AutoDetectPoe, "自动检测开关未加载");
        Equal("Ctrl+F5", craft.HotkeyStart, "Craft 启动热键未加载");
        Equal("F10", host.CoordinateHotkey, "坐标热键未加载");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) settingsTool.SaveSettings(writer);
        var saved = JsonNode.Parse(stream.ToArray())!.AsObject();
        Equal("PathOfExile_x64.exe", saved["target_process"]!.GetValue<string>(), "目标进程未保存");
        Equal(false, saved["auto_detect_poe"]!.GetValue<bool>(), "自动检测开关未保存");
        Equal("Ctrl+F5", saved["hotkeys"]!["start"]!.GetValue<string>(), "启动热键未保存");
        Equal("F10", saved["hotkeys"]!["coordinate"]!.GetValue<string>(), "坐标热键未保存");

        foreach (var tool in registry.Tools) tool.OnShutdown();
    });
}

static void ToolHostBuildsCompleteHotkeySet()
{
    RunInSta(() =>
    {
        var craft = new CraftTool();
        var clicker = new ClickerTool();
        var keyLoop = new KeyLoopTool();
        var hideout = new HideoutTool();
        var settingsTool = new SettingsTool(craft, clicker, keyLoop, hideout);
        var registry = new ToolRegistry();
        registry.Register(craft);
        registry.Register(clicker);
        registry.Register(keyLoop);
        registry.Register(hideout);
        registry.Register(settingsTool);
        var host = new ToolHost(registry);
        foreach (var tool in registry.Tools) tool.Initialize(host);

        var requests = host.BuildHotkeyRequests();
        Equal(7, requests.Count, "应汇总 Craft 2 + Clicker 2 + KeyLoop 1 + Hideout 1 + 坐标 1");
        Equal(1, requests.Count(request => request.DisplayName == "坐标录制"), "坐标热键必须只注册一次");

        foreach (var tool in registry.Tools) tool.OnShutdown();
    });
}

static void SoundFilesStayInsideSoundDirectory()
{
    WithTempDirectory(directory =>
    {
        File.WriteAllBytes(Path.Combine(directory, "default_ding.mp3"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(directory, "custom.ogg"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(directory, "ignore.txt"), "not audio");
        var sound = new SoundService(directory);

        var files = sound.ScanSounds();
        Equal(2, files.Count, "只应扫描声明的音频扩展名");
        True(files.Contains("default_ding.mp3"), "默认 mp3 未被扫描");
        True(files.Contains("custom.ogg"), "自定义 ogg 未被扫描");

        True(sound.TryResolveSoundFile("default_ding.wav", out var resolved, out _),
            "旧默认 wav 名称应按 stem 回退到随包 mp3");
        Equal("default_ding.mp3", Path.GetFileName(resolved), "默认音效回退文件错误");
        False(sound.TryResolveSoundFile("..\\outside.mp3", out _, out _),
            "音效路径穿越必须被拒绝");
        False(sound.TryResolveSoundFile("unsupported.aac", out _, out _),
            "未声明扩展名必须被拒绝");
    });
}

static void NetworkVersionComparisonIsNumeric()
{
    True(NetworkService.NeedsUpdate("1.0.10", new Version(1, 0, 9)),
        "1.0.10 必须大于 1.0.9");
    False(NetworkService.NeedsUpdate("1.0.9", new Version(1, 0, 10)),
        "旧版本不得触发更新");
    False(NetworkService.NeedsUpdate("invalid", new Version(1, 0, 10)),
        "非法版本文本不得触发强制更新");
}

static void PingPayloadContainsOnlyFixedFields()
{
    var root = NetworkService.CreatePingPayload();
    Equal("event", root["type"]!.GetValue<string>(), "统计载荷类型错误");
    var payload = root["payload"]!.AsObject();
    var expected = new[] { "website", "url", "hostname", "language", "screen", "title", "event" };
    Equal(expected.Length, payload.Count, "匿名载荷字段数量发生变化，必须重新审查隐私文案");
    True(expected.All(payload.ContainsKey), "匿名载荷缺少既定固定字段");
    False(payload.ContainsKey("machine"), "不得上传机器名");
    False(payload.ContainsKey("clipboard"), "不得上传剪贴板");
    False(payload.ContainsKey("rules"), "不得上传词缀规则");
}

static void PublishProfileKeepsSafeWpfOptions()
{
    var root = FindProjectRoot();
    var profile = XDocument.Load(Path.Combine(root, "Properties", "PublishProfiles", "FrameworkDependent.pubxml"));
    var properties = profile.Descendants("PropertyGroup").Elements()
        .ToDictionary(element => element.Name.LocalName, element => element.Value, StringComparer.OrdinalIgnoreCase);
    Equal("win-x64", properties["RuntimeIdentifier"], "发布目标必须固定为 win-x64");
    Equal("false", properties["SelfContained"].ToLowerInvariant(), "本配置必须保持依赖框架发布");
    Equal("true", properties["PublishSingleFile"].ToLowerInvariant(), "必须启用单文件主程序");
    Equal("false", properties["PublishTrimmed"].ToLowerInvariant(), "WPF 发布不得启用裁剪");

    var projectText = File.ReadAllText(Path.Combine(root, "拾刻.csproj"));
    True(projectText.Contains("sounds\\**", StringComparison.Ordinal), "发布项目必须保留 sounds 资源规则");
    True(projectText.Contains("data\\presets\\**", StringComparison.Ordinal), "发布项目必须保留内置预设资源规则");
}

static void AssemblyVersionIsCurrent()
{
    var version = NetworkService.CurrentVersion;
    Equal(1, version.Major, "程序集 Major 错误");
    Equal(0, version.Minor, "程序集 Minor 错误");
    Equal(23, version.Build, "程序集 Build 必须为本次 1.0.23");
}

static void CraftEngineCanShutdownWhileIdle()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var host = new ToolHost(new ToolRegistry());
            var engine = new CraftEngine(host);
            True(engine.Shutdown(TimeSpan.FromSeconds(1)), "空闲常驻任务必须在限定时间内退出");
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    True(thread.Join(TimeSpan.FromSeconds(2)), "STA 测试线程未在限定时间退出");
    if (failure is not null) throw failure;
}

static void ClickerEngineLifecycleIsSafe()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var host = new ToolHost(new ToolRegistry());
            var foreground = false;
            var simulatedClicks = 0;
            var releaseCalls = 0;
            var engine = new ClickerEngine(
                host,
                () => foreground,
                (_, _) =>
                {
                    Interlocked.Increment(ref simulatedClicks);
                    return Task.CompletedTask;
                },
                () => Interlocked.Increment(ref releaseCalls));

            True(engine.Start(ClickerMouseButton.Left, 10), "Clicker 首次启动应成功");
            Thread.Sleep(60);
            Equal(0, simulatedClicks, "非目标前台时不得调用模拟点击动作");

            foreground = true;
            True(SpinWait.SpinUntil(() => engine.ClickCount >= 3, TimeSpan.FromSeconds(1)),
                "回到目标前台后 Clicker 应恢复运行");
            True(engine.Stop(), "运行中的 Clicker 应可停止");
            Thread.Sleep(80);
            var stoppedCount = engine.ClickCount;
            Equal(1, releaseCalls, "Clicker 停止后应执行一次鼠标释放兜底");
            Thread.Sleep(60);
            Equal(stoppedCount, engine.ClickCount, "停止后点击计数不得继续增长");
            Equal(stoppedCount, simulatedClicks, "引擎计数必须与成功模拟点击次数一致");

            True(engine.Start(ClickerMouseButton.Right, 15), "同一常驻任务停止后应能再次启动");
            True(SpinWait.SpinUntil(() => engine.ClickCount >= 2, TimeSpan.FromSeconds(1)),
                "Clicker 第二次运行应正常计数");
            True(engine.Stop(), "Clicker 第二次运行应可停止");
            Thread.Sleep(60);
            True(engine.Shutdown(TimeSpan.FromSeconds(1)), "Clicker 常驻任务必须在限定时间内关闭");
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    True(thread.Join(TimeSpan.FromSeconds(3)), "Clicker STA 测试线程未在限定时间退出");
    if (failure is not null) throw failure;
}

static void KeyLoopEngineLifecycleIsSafe()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var host = new ToolHost(new ToolRegistry());
            var foreground = false;
            var simulated = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var simulatedGate = new object();
            var engine = new KeyLoopEngine(
                host,
                () => foreground,
                (key, _) =>
                {
                    lock (simulatedGate)
                        simulated[key] = simulated.GetValueOrDefault(key) + 1;
                    return Task.CompletedTask;
                });
            var slots = new[]
            {
                new KeyLoopSlot { Enabled = true, Key = "q", DelaySeconds = 0.1 },
                new KeyLoopSlot { Enabled = true, Key = "w", DelaySeconds = 0.2 },
            };

            True(engine.Start(slots), "KeyLoop 合法配置应启动");
            Thread.Sleep(60);
            Equal(0, engine.PressCounts.Sum(), "非目标前台时不得调用模拟按键动作");

            foreground = true;
            True(SpinWait.SpinUntil(() =>
            {
                var counts = engine.PressCounts;
                return counts[0] >= 3 && counts[1] >= 2;
            }, TimeSpan.FromSeconds(2)), "两个槽位应按独立间隔运行");
            True(engine.Stop(), "运行中的 KeyLoop 应可停止");
            Thread.Sleep(150);
            var stoppedCounts = engine.PressCounts.ToArray();
            Thread.Sleep(150);
            True(stoppedCounts.SequenceEqual(engine.PressCounts), "停止后各槽位计数不得继续增长");
            lock (simulatedGate)
            {
                Equal(stoppedCounts[0], simulated.GetValueOrDefault("q"), "q 槽位计数应对应模拟按键次数");
                Equal(stoppedCounts[1], simulated.GetValueOrDefault("w"), "w 槽位计数应对应模拟按键次数");
            }
            True(engine.Shutdown(TimeSpan.FromSeconds(1)), "KeyLoop 的 10 个常驻任务必须在限定时间内关闭");
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    True(thread.Join(TimeSpan.FromSeconds(5)), "KeyLoop STA 测试线程未在限定时间退出");
    if (failure is not null) throw failure;
}

static void CraftRulesSnapshotIsIndependent()
{
    var source = new CraftRules
    {
        Mode = CraftMode.AltAugRegal,
        PrimaryHitCount = 2,
        SecondaryHitCount = 1,
    };
    source.PrimaryAffixes.Add(new AffixRule { Text = "生命" });
    source.SecondaryAffixes.Add(new AffixRule { Text = "抗性" });

    var snapshot = source.CreateSnapshot();
    source.Mode = CraftMode.Single;
    source.PrimaryHitCount = 0;
    source.PrimaryAffixes.Clear();
    source.SecondaryAffixes.Clear();

    Equal(CraftMode.AltAugRegal, snapshot.Mode, "快照模式不应随源对象改变");
    Equal(2, snapshot.PrimaryHitCount, "快照命中数不应随源对象改变");
    Equal("生命", snapshot.PrimaryAffixes.Single().Text, "快照主词缀应深拷贝");
    Equal("抗性", snapshot.SecondaryAffixes.Single().Text, "快照次词缀应深拷贝");
}

static CraftRules Rules(string primary)
{
    var rules = new CraftRules { PrimaryHitCount = 1 };
    rules.PrimaryAffixes.Add(new AffixRule { Text = primary });
    return rules;
}

static string MagicItemWithOneAffix() => """
    物品类别: 单手剑
    稀 有 度: 魔法
    霜语
    皇家短剑
    --------
    { 固定基底词缀 — 武器 }
    增加 20% 全域伤害
    --------
    { 前缀属性 "急冻的" (等阶：6) — 伤害, 元素, 冰霜, 攻击 }
    该装备附加 52 - 81 基础冰霜伤害
    (吸取的魔力会随时间逐渐回复)
    （中文说明不属于词缀）
    --------
    物品等级: 84
    需求:
    等级: 60
    """;

static void Equal<T>(T expected, T actual, string message) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}；expected={expected}, actual={actual}");
}

static void False(bool value, string message)
{
    if (value) throw new InvalidOperationException(message);
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Throws<TException>(Action action, string message) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static void WithTempDirectory(Action<string> action)
{
    var baseDirectory = Path.Combine(Path.GetTempPath(), "ShiKe.InternalTests");
    var directory = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        action(directory);
    }
    finally
    {
        var fullDirectory = Path.GetFullPath(directory);
        var fullBase = Path.GetFullPath(baseDirectory) + Path.DirectorySeparatorChar;
        if (!fullDirectory.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝清理测试根目录之外的路径");
        if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, recursive: true);
    }
}

static void RunInSta(Action action)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try { action(); }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    True(thread.Join(TimeSpan.FromSeconds(5)), "STA 测试线程未在限定时间退出");
    if (failure is not null) throw failure;
}

static string FindProjectRoot()
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "拾刻.csproj")))
            return directory.FullName;
        directory = directory.Parent;
    }
    throw new InvalidOperationException("无法定位拾刻.csproj");
}
