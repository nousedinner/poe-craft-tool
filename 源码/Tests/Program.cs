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
using System.Text;

// 重定向日志与控制台使用同一编码，不能由 Windows 当前代码页决定中文用例名称。
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var tests = new (string Name, Action Run)[]
{
    ("装备名参与匹配但不计入词缀数", ItemNameMatchesWithoutIncreasingAffixCount),
    ("一条显式词缀准确计数", OneExplicitAffixIsCounted),
    ("实机弓样本识别为一条基础物理前缀", RealBowSampleIsOnePhysicalPrefix),
    ("Mode3 稀有弓样本识别为主2次1", RealMode3RareBowMatchesTwoPrimaryOneSecondary),
    ("两条显式词缀准确计数", TwoExplicitAffixesAreCounted),
    ("插槽开头的保留效用词缀参与匹配", ItemParsingChecks.SocketAffixIsMatched),
    ("元数据字段边界不会吞掉词缀正文或装备名", ItemParsingChecks.MetadataRequiresFieldBoundary),
    ("繁体元数据不会造成主次或排除误命中", ItemParsingChecks.TraditionalMetadataIsFiltered),
    ("简繁英文显式词缀头准确计数", ItemParsingChecks.ExplicitHeadersAreCounted),
    ("非显式词缀头不能因包含前后缀字样被计数", ItemParsingChecks.OtherHeadersAreNotCounted),
    ("繁体词缀头正文和装备名不会重复命中", ItemParsingChecks.TraditionalAffixIsConsumedOnce),
    ("繁体物品剪贴板头通过读取门禁", ItemParsingChecks.TraditionalClipboardHeadersAreAccepted),
    ("简繁英文稀有度与中文冒号兼容", ItemParsingChecks.RarityFormatsAreParsed),
    ("繁体样本贯通Mode3判定和通货转换", ItemParsingChecks.TraditionalCurrencyFlow),
    ("繁体插槽词缀保持排除优先", ItemParsingChecks.TraditionalExclusionTakesPriority),
    ("括号说明行不参与匹配", ParentheticalNotesAreIgnored),
    ("同一显式词缀在同一池内不重复计数", OneAffixCannotSatisfyTwoRulesInSamePool),
    ("魔法名称与显式正文不会重复计算同一词缀", MagicNameDoesNotDuplicateExplicitAffix),
    ("同一候选不能同时计入主次池", OneCandidateCannotSatisfyPrimaryAndSecondary),
    ("三个词缀池禁止重复规则", DuplicateRuleAcrossPoolsIsRejected),
    ("未知模式与负数量拒绝且单通货要求目标", CraftRejectsInvalidRuleBounds),
    ("旧设置迁移保留原始备份", LegacySettingsMigrationKeepsOriginalBackup),
    ("仅 Hideout 分节不会被误迁移", HideoutOnlySectionIsRecognized),
    ("分节更新不会丢失其他设置", SectionUpdatePreservesOtherSections),
    ("缺少数据文件仍使用正常默认值", MissingDataFilesRemainSupported),
    ("损坏设置不迁移也不覆盖", DamagedSettingsAreNotOverwritten),
    ("错误坐标不返回部分结果也不覆盖", InvalidCoordinatesAreNotOverwritten),
    ("损坏规则和预设不覆盖并兼容字典词缀", InvalidRulesAndPresetsAreProtected),
    ("文件占用和只读保存失败保留原件", InaccessibleFilesRemainIntact),
    ("坐标保存失败保留录制状态供重试", CoordinateSaveFailureCanRetry),
    ("删除预设只影响选中原件且失败可重试", PresetDeletionPreservesOtherData),
    ("整组清空坐标失败保留原件与录制状态", CoordinateBatchClearIsAtomic),
    ("抽屉标识不能重复或为空", ToolRegistryRejectsInvalidIdentity),
    ("整组序列化失败不写入任何分节", ToolSettingsPersistenceRejectsPartialData),
    ("多工具状态独立保存且全局活动标记准确", ToolStatusesRemainIndependent),
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
    ("Mode3 蜕变产物进入魔法阶段判定", Mode3TransmutationDecisionMatrix),
    ("Mode3 增幅达标后必须进入富豪", Mode3AugmentationAlwaysProceedsToRegal),
    ("Mode3 启动稀有度判定矩阵", Mode3StartDecisionMatrix),
    ("Mode3 只记录两词缀未达阈值样本", Mode3DiagnosticCapturePolicy),
    ("单通货旧数量统一为主1次0且保留排除规则", SingleCurrencyUsesOnePrimaryHit),
    ("剪贴板只接受合法物品文本头", ClipboardItemHeaderValidation),
    ("输入层点击间隔计算", CraftClickIntervalSemantics),
    ("连点器左上角安全区判定", ClickerTopLeftSafetyZone),
    ("Clicker 和 KeyLoop 仅保留热键运行入口", ToolPagesUseHotkeysForRuntimeStart),
    ("滑块开关包含缓动动画", SwitchStyleContainsMotionAnimation),
    ("设置热键捕获成对暂停和恢复注册", SettingsHotkeyCaptureIsSymmetric),
    ("启动性能覆盖首帧和 Craft 初始化", StartupTimingMarkersCoverFirstRender),
    ("启动测量不能在日常目录或未标记副本绕过单实例", StartupProfilesRequireOwnedCopy),
    ("延迟通知支持首次显示隐藏及丢弃退出后的排队请求", LazyNotificationLifecycleIsSafe),
    ("Mode1 完整物品状态变化判定", Mode1ItemStateChangeDetection),
    ("Mode1 状态同步超时有安全下限", Mode1StateSyncTimeoutPolicy),
    ("Mode2/3 通货状态转换矩阵", CurrencyTransitionMatrix),
    ("Hold 组合热键保留修饰键", HoldHotkeyKeepsModifiers),
    ("Hold 支持独立修饰键", HoldSupportsStandaloneModifier),
    ("修饰键状态判定支持左右按键", ModifierStateSupportsLeftAndRightKeys),
    ("公共热键层统一执行前台门禁", HotkeyManagerEnforcesForegroundPolicy),
    ("热键注销阻止排队旧回调且保留新事件顺序", UnregisteredHotkeysDiscardQueuedCallbacks),
    ("游戏内运行异常统一进入置顶提示", RuntimeFailuresReachTopmostNotification),
    ("设置页按语义检测组合热键冲突", SettingsDetectsSemanticHotkeyConflicts),
    ("新热键优先并清空所有语义冲突项", NewHotkeyClearsSemanticConflicts),
    ("七项热键互相覆盖与单项清空", HotkeysSupportReassignmentAndClearing),
    ("空热键可校验、保存并完整往返", EmptyHotkeysRoundTrip),
    ("洗装停止热键缺失或非法时拒绝启动", CraftRequiresValidStopHotkey),
    ("设置页拒绝路径和控制字符", SettingsRejectsUnsafeValues),
    ("Ctrl 按住连点与 Craft 启用状态互斥", CtrlHoldClickerConflictsWithCraft),
    ("热键重注册失败恢复旧配置", HotkeyTransactionRollsBackOnFailure),
    ("保存失败恢复完整设置与旧热键", SettingsSaveFailureRestoresFullDraft),
    ("保存失败且热键恢复异常时明确提示", SettingsRollbackFailureIsReported),
    ("工具开关保存失败恢复启用状态和热键且可重试", ToolEnablementSaveFailureRollsBack),
    ("工具开关校验注册和停止失败均不写入配置", ToolEnablementFailuresDoNotPersist),
    ("工具开关恢复异常不误报恢复成功", ToolEnablementRollbackFailureIsReported),
    ("回城命令拒绝控制字符且保存失败保留原命令", HideoutCommandFailuresPreservePrevious),
    ("自动检测目标保存失败恢复原前台保护", AutoDetectedTargetSaveFailureRollsBack),
    ("SettingsTool 使用 host 分节完整往返", SettingsToolHostSectionRoundTrip),
    ("流动速度完整往返且保存失败保持原值", GradientSpeedSettingsRoundTrip),
    ("宿主只汇总已启用工具的热键", ToolHostBuildsEnabledHotkeySet),
    ("宿主跳过全部未绑定热键", ToolHostSkipsUnassignedHotkeys),
    ("音效扫描和路径解析限制在 sounds 目录", SoundFilesStayInsideSoundDirectory),
    ("网络版本比较使用 Version 语义", NetworkVersionComparisonIsNumeric),
    ("网页入口仅接受合法 HTTP(S) 地址", BrowserLinksRequireWebAddresses),
    ("新版信息验证版本号和下载地址", NetworkValidatesVersionData),
    ("广告跳过坏条目并保留其他内容", NetworkKeepsValidAds),
    ("网络故障和过大响应留日志且不强退", NetworkFailuresAreDiagnosable),
    ("应用取消终止网络请求且不当作故障", NetworkCancellationIsRespected),
    ("统计请求保留固定载荷和版本请求头", NetworkPingUsesFixedPayload),
    ("匿名统计载荷不包含本机数据", PingPayloadContainsOnlyFixedFields),
    ("单文件发布配置保留 WPF 和资源安全选项", PublishProfileKeepsSafeWpfOptions),
    ("空目标进程采用 fail-closed", EmptyTargetProcessIsNotForeground),
    ("程序集版本与本次修复版本一致", AssemblyVersionIsCurrent),
    ("Craft 常驻任务可以正式关闭", CraftEngineCanShutdownWhileIdle),
    ("Clicker 常驻任务可暂停停止并关闭", ClickerEngineLifecycleIsSafe),
    ("KeyLoop 独立槽位可暂停停止并关闭", KeyLoopEngineLifecycleIsSafe),
    ("Craft 运行配置使用深拷贝快照", CraftRulesSnapshotIsIndependent),
    ("淡彩弹窗保持按钮、模态、长文与自动消失行为", PromptChecks.Run),
    ("T11 实际窗口布局、切页保存与渐变生命周期", AppearanceChecks.Run),
    ("Mode2/3 任意编辑后启动校验上限", MultiCurrencyLimitsAreCheckedAtStart),
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

    var rules = new CraftRules { Mode = CraftMode.AltAugRegal, PrimaryHitCount = 2, SecondaryHitCount = 1 };
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

static void CraftRejectsInvalidRuleBounds()
{
    foreach (var mode in new[] { CraftMode.Single, CraftMode.AltAug, CraftMode.AltAugRegal })
    {
        var rules = new CraftRules { Mode = mode, PrimaryHitCount = 0, SecondaryHitCount = 0 };
        Equal(mode != CraftMode.Single, rules.Validate().Ok, "只有多通货模式保留零命中，单通货必须有主池目标");
        rules.PrimaryHitCount = -1;
        False(rules.Validate().Ok, "负主命中数不得放宽规则");
        rules.PrimaryHitCount = 0;
        rules.SecondaryHitCount = -1;
        False(rules.Validate().Ok, "负次命中数不得放宽规则");
    }
    var unknown = new CraftRules { Mode = (CraftMode)99, PrimaryHitCount = 0, SecondaryHitCount = 0 };
    False(unknown.Validate().Ok, "未知模式不能进入任何通货流程");
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

static void MissingDataFilesRemainSupported()
{
    WithTempDirectory(directory =>
    {
        var storage = new StorageService(directory);
        Equal(0, storage.LoadSettings().Count, "首次使用缺少设置应返回空分节");
        Equal(0, storage.LoadCoordinates().Count, "缺少坐标应返回空字典");
        True(storage.LoadRules() is null, "缺少规则应保持未配置状态");
        True(storage.LoadPreset("未创建") is null, "缺少预设应返回未找到");
        storage.SaveRules(new JsonObject { ["primary_affixes"] = new JsonArray("物理伤害") });
        storage.SaveCoordinates(new Dictionary<string, Point> { ["item"] = new(-20, 30) });
        storage.UpdateSettings(root => root["host"] = new JsonObject());
        Equal(new Point(-20, 30), storage.LoadCoordinates()["item"], "合法负坐标必须保持兼容");
    });
}

static void DamagedSettingsAreNotOverwritten()
{
    WithTempDirectory(directory =>
    {
        var path = Path.Combine(directory, "settings.json");
        var storage = new StorageService(directory);
        foreach (var (text, kind) in new (string, StorageFailureKind)[]
        {
            ("{", StorageFailureKind.InvalidJson),
            ("[]", StorageFailureKind.InvalidData),
            ("null", StorageFailureKind.InvalidData),
            ("{\"host\":[]}", StorageFailureKind.InvalidData),
            ("{\"host\":null}", StorageFailureKind.InvalidData),
            ("{\"host\":{\"hotkeys\":null}}", StorageFailureKind.InvalidData),
            ("{\"host\":{\"hotkeys\":{\"stop\":6}}}", StorageFailureKind.InvalidData),
            ("{\"host\":{\"auto_detect_poe\":\"true\"}}", StorageFailureKind.InvalidData),
            ("{\"craft\":{\"delay_ms\":\"33\"}}", StorageFailureKind.InvalidData),
            ("{\"craft\":{\"sound_enabled\":null}}", StorageFailureKind.InvalidData),
            ("{\"clicker\":{\"hold_hotkey\":11}}", StorageFailureKind.InvalidData),
            ("{\"hideout\":{\"command\":false}}", StorageFailureKind.InvalidData),
            ("{\"keyloop\":{\"slots\":{}}}", StorageFailureKind.InvalidData),
            ("{\"keyloop\":{\"slots\":[null]}}", StorageFailureKind.InvalidData),
            ("{\"keyloop\":{\"slots\":[{\"enabled\":1}]}}", StorageFailureKind.InvalidData),
            ("{\"keyloop\":{\"slots\":[{\"delay_s\":\"1\"}]}}", StorageFailureKind.InvalidData),
            ("{\"delay_ms\":null}", StorageFailureKind.InvalidData),
            ("{\"key_loop_slots\":[false]}", StorageFailureKind.InvalidData),
            ("{\"clicker_interval_ms\":\"wrong\"}", StorageFailureKind.InvalidData),
        })
        {
            File.WriteAllText(path, text);
            ExpectStorageFailure(() => storage.LoadSettings(), kind, path);
            ExpectStorageFailure(() => storage.UpdateSettings(root => root["host"] = new JsonObject()), kind, path);
            ExpectStorageFailure(() => storage.SaveSettings(new JsonObject()), kind, path);
            Equal(text, File.ReadAllText(path), "读取和后续保存均不得覆盖原始损坏设置");
            False(File.Exists(path + ".bak"), "未成功解析的内容不得触发迁移");
        }
    });
}

static void InvalidCoordinatesAreNotOverwritten()
{
    WithTempDirectory(directory =>
    {
        var path = Path.Combine(directory, "coordinates.json");
        const string text = "{\"item\":[10,20],\"alteration\":[\"bad\",30]}";
        File.WriteAllText(path, text);
        var storage = new StorageService(directory);
        ExpectStorageFailure(() => storage.LoadCoordinates(), StorageFailureKind.InvalidData, path);
        ExpectStorageFailure(() => storage.SaveCoordinates(new Dictionary<string, Point> { ["item"] = new(1, 2) }),
            StorageFailureKind.InvalidData, path);
        Equal(text, File.ReadAllText(path), "不得把部分读取成功的坐标写回原文件");
    });
}

static void InvalidRulesAndPresetsAreProtected()
{
    WithTempDirectory(directory =>
    {
        var storage = new StorageService(directory);
        var rulesPath = Path.Combine(directory, "rules.json");
        Directory.CreateDirectory(Path.Combine(directory, "presets"));
        var presetPath = Path.Combine(directory, "presets", "损坏.json");
        var replacement = new JsonObject { ["primary_hit_count"] = 1 };
        foreach (var text in new[]
        {
            "{\"primary_affixes\":\"bad\"}", "{\"primary_affixes\":null}",
            "{\"mode\":\"unknown\"}", "{\"mode\":0}", "{\"mode\":null}",
            "{\"primary_hit_count\":-1}", "{\"secondary_hit_count\":-1}",
        })
        {
            File.WriteAllText(rulesPath, text);
            File.WriteAllText(presetPath, text);
            ExpectStorageFailure(() => storage.LoadRules(), StorageFailureKind.InvalidData, rulesPath);
            ExpectStorageFailure(() => storage.SaveRules(replacement), StorageFailureKind.InvalidData, rulesPath);
            ExpectStorageFailure(() => storage.LoadPreset("损坏"), StorageFailureKind.InvalidData, presetPath);
            ExpectStorageFailure(() => storage.SavePreset("损坏", replacement), StorageFailureKind.InvalidData, presetPath);
            Equal(text, File.ReadAllText(rulesPath), "规则原件必须保留");
            Equal(text, File.ReadAllText(presetPath), "预设原件必须保留");
        }

        storage.SavePreset("旧字典词缀", JsonNode.Parse("{\"primary_affixes\":[{\"text\":\"物理伤害\"}]}")!.AsObject());
        True(storage.LoadPreset("旧字典词缀")?["primary_affixes"] is JsonArray,
            "旧字符串和字典词缀格式均应保持可加载");
    });
}

static void InaccessibleFilesRemainIntact()
{
    WithTempDirectory(directory =>
    {
        var path = Path.Combine(directory, "rules.json");
        const string text = "{\"primary_hit_count\":1}";
        File.WriteAllText(path, text);
        var storage = new StorageService(directory);
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ExpectStorageFailure(() => storage.LoadRules(), StorageFailureKind.FileAccess, path);
            ExpectStorageFailure(() => storage.SaveRules(new JsonObject()), StorageFailureKind.FileAccess, path);
        }
        Equal(text, File.ReadAllText(path), "占用失败后必须保留原件");

        var attributes = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
            ExpectStorageFailure(() => storage.SaveRules(new JsonObject()), StorageFailureKind.FileAccess, path);
            Equal(text, File.ReadAllText(path), "只读保存失败后必须保留原件");
        }
        finally { File.SetAttributes(path, attributes); }
        Equal(0, Directory.GetFiles(directory, "*.tmp").Length, "失败的临时写入应得到清理");
    });
}

static void CoordinateSaveFailureCanRetry()
{
    WithTempDirectory(directory =>
    {
        var path = Path.Combine(directory, "coordinates.json");
        const string text = "{\"item\":[1,2]}";
        File.WriteAllText(path, text);
        var storage = new StorageService(directory);
        var recorder = new CoordinateRecorder(storage, () => new Point(20, 30));
        var completed = 0;
        recorder.RecordingCompleted += (_, _) => completed++;
        recorder.StartRecording(new CoordinateSlot { SlotId = "item", DisplayName = "装备" });
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            ExpectStorageFailure(recorder.OnRecordHotkey, StorageFailureKind.FileAccess, path);
        True(recorder.IsRecording, "保存失败不得清除待录制槽位");
        Equal(0, completed, "保存失败不得发布录制成功事件");
        Equal(text, File.ReadAllText(path), "失败时旧坐标必须保留");

        recorder.OnRecordHotkey();
        False(recorder.IsRecording, "重试成功后应结束录制");
        Equal(1, completed, "重试成功只发布一次录制完成");
        Equal(new Point(20, 30), storage.LoadCoordinates()["item"], "重试应保存新位置");
    });
}

static void PresetDeletionPreservesOtherData()
{
    WithTempDirectory(directory =>
    {
        var storage = new StorageService(directory);
        var data = new JsonObject { ["primary_affixes"] = new JsonArray("生命") };
        storage.SaveRules(data);
        storage.SavePreset("待删除", data);
        storage.SavePreset("保留", data);
        var path = Path.Combine(directory, "presets", "待删除.json");
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            ExpectStorageFailure(() => storage.DeletePreset("待删除"), StorageFailureKind.FileAccess, path);
        True(storage.LoadPreset("待删除") is not null, "删除失败应保留原件");
        storage.DeletePreset("待删除");
        True(storage.LoadPreset("待删除") is null, "只应删除选中预设");
        True(JsonNode.DeepEquals(data, storage.LoadPreset("保留")), "其他预设必须保留");
        True(JsonNode.DeepEquals(data, storage.LoadRules()), "当前规则文件必须保留");
    });
}

static void CoordinateBatchClearIsAtomic()
{
    WithTempDirectory(directory =>
    {
        var storage = new StorageService(directory);
        var path = Path.Combine(directory, "coordinates.json");
        storage.SaveCoordinates(new Dictionary<string, Point> { ["item"] = new(1, 2), ["alteration"] = new(3, 4), ["reserved"] = new(9, 9) });
        var original = File.ReadAllText(path);
        var recorder = new CoordinateRecorder(storage, () => null);
        recorder.StartRecording(new CoordinateSlot { SlotId = "item", DisplayName = "装备" });
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            ExpectStorageFailure(() => recorder.ClearCoordinates(["item", "alteration"]), StorageFailureKind.FileAccess, path);
        Equal(original, File.ReadAllText(path), "整组清空失败不能部分清空文件");
        True(recorder.IsRecording, "保存失败应保留录制槽位");
        recorder.ClearCoordinates(["item", "alteration"]);
        False(recorder.IsRecording, "清空成功应取消相关录制");
        var result = storage.LoadCoordinates();
        Equal(1, result.Count, "只清空指定坐标");
        Equal(new Point(9, 9), result["reserved"], "未知保留坐标不能丢失");
    });
}

static void ToolRegistryRejectsInvalidIdentity()
{
    var registry = new ToolRegistry();
    registry.Register(new StubTool("sample", new JsonObject()));
    Throws<InvalidOperationException>(() => registry.Register(new StubTool("SAMPLE", new JsonObject())), "重复标识不能覆盖已有工具");
    Throws<ArgumentException>(() => registry.Register(new StubTool(" ", new JsonObject())), "空标识不能进入存储");
    Equal(1, registry.Tools.Count, "失败注册不得改变已有抽屉");
}

static void ToolSettingsPersistenceRejectsPartialData()
{
    WithTempDirectory(directory =>
    {
        var storage = new StorageService(directory);
        storage.SaveSettings(new JsonObject { ["host"] = new JsonObject(), ["future"] = new JsonObject { ["keep"] = true } });
        var path = Path.Combine(directory, "settings.json");
        var original = File.ReadAllText(path);
        var valid = new StubTool("sample", new JsonObject { ["value"] = 1 });
        Throws<InvalidDataException>(() => ToolSettingsPersistence.SaveSections(storage, [valid, new StubTool("bad", null)]), "非法工具输出应中止整组保存");
        Equal(original, File.ReadAllText(path), "候选序列化失败不能写入部分分节");
        ToolSettingsPersistence.SaveSections(storage, [valid]);
        var saved = storage.LoadSettings();
        True(saved["future"]?["keep"]?.GetValue<bool>() == true, "其他工具分节必须保留");
        Equal(1, saved["sample"]!["value"]!.GetValue<int>(), "有效工具应走统一保存入口");
    });
}

static void ToolStatusesRemainIndependent()
{
    var statuses = new ToolStatusStore();
    var notifications = 0;
    statuses.Changed += () => notifications++;
    statuses.Report("craft", "已完成", false, 8);
    statuses.Report("clicker", "连点中", true);
    statuses.Report("clicker", "连点中", true);
    Equal(2, notifications, "重复状态不应重复刷新界面");
    var craft = statuses.Read("craft");
    True(craft.AnyRunning, "当前抽屉停止不能遮蔽其他运行工具");
    Equal("已完成", craft.Selected!.Text, "其他工具不得覆盖当前抽屉状态");
    Equal(8, craft.Selected.UseCount!.Value, "其他工具不得覆盖洗装计数");
    statuses.Report("clicker", "已停止", false);
    False(statuses.Read("host").AnyRunning, "所有工具停止后标记应空闲");
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
    var source = new CraftTool();
    source.LoadSettings(JsonSerializer.SerializeToElement(new
    {
        enabled = false,
        mode3_debug_delay_ms = 2000,
    }));
    source.DelayMs = 123;
    source.SoundEnabled = false;
    source.PopupEnabled = false;
    source.ExhaustionThreshold = 17;
    source.SelectedSound = "roundtrip.wav";
    source.Mode2ScourAlch = true;
    source.UseExalt = true;

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
    Equal(false, restored.IsEnabled, "Craft 启用状态往返失败");
    False(document.RootElement.TryGetProperty("mode3_debug_delay_ms", out _),
        "正式设置不应继续保存临时 Mode3 调试间隔");
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
    source.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = true }));
    source.Hotkey = "Ctrl+F8";
    source.HoldHotkey = "Shift+F11";
    source.IntervalMs = 47;
    source.MouseButton = ClickerMouseButton.Right;
    source.NotificationsEnabled = true;

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
    Equal(true, restored.IsEnabled, "Clicker 工具启用状态往返失败");

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
    source.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = true }));
    source.Hotkey = "Ctrl+F9";
    source.NotificationsEnabled = true;
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
    Equal(true, restored.IsEnabled, "KeyLoop 工具启用状态往返失败");
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

static void Mode3TransmutationDecisionMatrix()
{
    Equal(Mode3MagicDecision.ProceedToRegal,
        CraftDecisions.AfterTransmutation(2, 2, 2, false),
        "蜕变直接得到两条达标词缀时必须跳过改造并进入富豪");
    Equal(Mode3MagicDecision.UseAugmentation,
        CraftDecisions.AfterTransmutation(1, 1, 2, false),
        "蜕变直接得到单条命中词缀时必须跳过改造并进入增幅");
    Equal(Mode3MagicDecision.ContinueAlteration,
        CraftDecisions.AfterTransmutation(2, 1, 2, false),
        "蜕变得到两条但命中不足时才应进入改造");
    Equal(Mode3MagicDecision.ContinueAlteration,
        CraftDecisions.AfterTransmutation(2, 2, 2, true),
        "蜕变命中排除词缀时必须进入改造，不能直接富豪");
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

static void SingleCurrencyUsesOnePrimaryHit()
{
    foreach (var (primary, secondary) in new[] { (0, 2), (3, 3), (1, 0) })
    {
        var rules = Rules("生命");
        rules.PrimaryHitCount = primary; rules.SecondaryHitCount = secondary;
        rules.SecondaryAffixes.Add(new AffixRule { Text = "抗性" });
        True(rules.Validate().Ok, "旧单通货数量不能改变新的一命中规则");
        var snapshot = rules.CreateSnapshot();
        Equal(1, snapshot.PrimaryHitCount, "实际执行快照必须固定主1");
        Equal(0, snapshot.SecondaryHitCount, "实际执行快照必须固定次0");
        True(new AffixCheckResult { PrimaryHits = 1 }.MeetsFinalRules(rules), "主池任意一条应成功");
        False(new AffixCheckResult { PrimaryHits = 0, SecondaryHits = 2 }.MeetsFinalRules(rules), "次池不能代替主池目标");
        False(new AffixCheckResult { PrimaryHits = 1, HasExclude = true }.MeetsFinalRules(rules), "排除仍必须使终检失败");
    }
}

static void MultiCurrencyLimitsAreCheckedAtStart()
{
    var rules = new CraftRules();
    foreach (var text in new[] { "生命", "护甲", "闪避" }) rules.PrimaryAffixes.Add(new AffixRule { Text = text });
    foreach (var text in new[] { "火抗", "冰抗", "电抗" }) rules.SecondaryAffixes.Add(new AffixRule { Text = text });
    foreach (var mode in new[] { CraftMode.AltAug, CraftMode.AltAugRegal })
    {
        rules.Mode = mode;
        rules.PrimaryHitCount = 3; rules.SecondaryHitCount = 3;
        False(rules.Validate().Ok, "3+3 必须在启动校验拒绝");
        rules.PrimaryHitCount = 1; rules.SecondaryHitCount = 1;
        True(rules.Validate().Ok, "1+1 仍能通过原启动校验");
        rules.PrimaryHitCount = 3; rules.SecondaryHitCount = 0;
        Equal(mode == CraftMode.AltAugRegal, rules.Validate().Ok, "Mode2 上限2、Mode3 上限3保持");
    }
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

static void ClickerTopLeftSafetyZone()
{
    True(InputSimulator.IsInClickerSafetyZone(0, 0), "主屏左上角必须触发安全停止");
    True(InputSimulator.IsInClickerSafetyZone(100, 200), "安全区边界必须触发安全停止");
    False(InputSimulator.IsInClickerSafetyZone(101, 200), "安全区右侧不得误停");
    False(InputSimulator.IsInClickerSafetyZone(100, 201), "安全区下方不得误停");
    False(InputSimulator.IsInClickerSafetyZone(-1, 0), "左侧副屏坐标不得视为主屏左上角");
}

static void ToolPagesUseHotkeysForRuntimeStart()
{
    var root = FindProjectRoot();
    var clicker = File.ReadAllText(Path.Combine(root, "Tools", "Clicker", "ClickerPage.xaml"));
    var keyLoop = File.ReadAllText(Path.Combine(root, "Tools", "KeyLoop", "KeyLoopPage.xaml"));
    False(clicker.Contains("x:Name=\"ToggleButton\"", StringComparison.Ordinal),
        "Clicker 页面不应保留底部启停按钮");
    False(keyLoop.Contains("x:Name=\"ToggleButton\"", StringComparison.Ordinal),
        "KeyLoop 页面不应保留底部启停按钮");
    True(clicker.Contains("x:Name=\"HotkeyHint\"", StringComparison.Ordinal),
        "Clicker 页面必须保留当前切换/按住热键值");
    True(keyLoop.Contains("x:Name=\"HotkeyText\"", StringComparison.Ordinal),
        "KeyLoop 页面必须保留当前启停热键值");
}

static void SwitchStyleContainsMotionAnimation()
{
    var root = FindProjectRoot();
    var theme = File.ReadAllText(Path.Combine(root, "Themes", "DefaultTheme.xaml"));
    True(theme.Contains("ThumbTransform", StringComparison.Ordinal), "滑块圆点必须使用可动画位移");
    True(theme.Contains("DoubleAnimation", StringComparison.Ordinal), "滑块必须包含位置动画");
    True(theme.Contains("ColorAnimation", StringComparison.Ordinal), "滑块必须包含轨道颜色动画");
    True(theme.Contains("CubicEase", StringComparison.Ordinal), "滑块移动必须使用缓动而非线性跳变");
}

static void SettingsHotkeyCaptureIsSymmetric()
{
    var root = FindProjectRoot();
    var tool = File.ReadAllText(Path.Combine(root, "Tools", "Settings", "SettingsTool.cs"));
    var page = File.ReadAllText(Path.Combine(root, "Tools", "Settings", "SettingsPage.xaml.cs"));
    True(tool.Contains("BeginHotkeyCapture", StringComparison.Ordinal) &&
         tool.Contains("host.Hotkeys.UnregisterAll()", StringComparison.Ordinal),
        "开始捕获时必须暂停已注册热键");
    True(tool.Contains("EndHotkeyCapture", StringComparison.Ordinal) &&
         tool.Contains("host.Hotkeys.ReRegister(host.BuildHotkeyRequests())", StringComparison.Ordinal),
        "结束或取消捕获时必须恢复当前热键集合");
    True(page.Contains("CancelCaptureBecauseWindowInactive", StringComparison.Ordinal) &&
         page.Contains("DeactivatePage", StringComparison.Ordinal),
        "切页、隐藏或失焦时必须结束捕获并恢复热键");
    True(page.Contains("ShowCaptureStatus", StringComparison.Ordinal) &&
         page.Contains("HideResult", StringComparison.Ordinal),
        "捕获状态与历史结果必须有明确生命周期");
}

static void StartupTimingMarkersCoverFirstRender()
{
    var root = FindProjectRoot();
    var app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
    var craftPage = File.ReadAllText(Path.Combine(root, "Tools", "Craft", "CraftPage.xaml.cs"));
    True(app.Contains("主窗口首帧完成", StringComparison.Ordinal), "启动日志必须覆盖主窗口首帧");
    True(app.Contains("热键注册完成", StringComparison.Ordinal), "启动日志必须覆盖热键注册");
    True(craftPage.Contains("Craft 首次页面初始化完成", StringComparison.Ordinal),
        "启动日志必须单独记录 Craft 首次页面耗时");
}

static void StartupProfilesRequireOwnedCopy()
{
    var root = Path.Combine(AppContext.BaseDirectory, "profile-guard-fixture");
    try
    {
        var allowed = Path.Combine(root, "验证记录", "启动性能", "副本");
        Directory.CreateDirectory(allowed);
        False(StartupPerformance.IsIsolatedProfileDirectory(allowed), "没有标记的副本不得测量");
        var markerPath = Path.Combine(allowed, ".shike-output.json");
        File.WriteAllText(markerPath, "{\"schemaVersion\":1,\"purpose\":\"启动性能副本\"}", new UTF8Encoding(true));
        True(StartupPerformance.IsIsolatedProfileDirectory(allowed), "正确用途的隔离副本应允许测量");
        File.WriteAllText(markerPath, "{\"schemaVersion\":1,\"purpose\":\"发布\"}");
        False(StartupPerformance.IsIsolatedProfileDirectory(allowed), "发布目录用途不允许测量");
        File.WriteAllText(markerPath, "not-json");
        False(StartupPerformance.IsIsolatedProfileDirectory(allowed), "损坏标记不允许测量");
        File.WriteAllText(Path.Combine(root, ".shike-output.json"), "{\"schemaVersion\":1,\"purpose\":\"启动性能副本\"}");
        False(StartupPerformance.IsIsolatedProfileDirectory(root), "仅复制标记不能在日常目录开启测量");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void LazyNotificationLifecycleIsSafe()
{
    RunInSta(() =>
    {
        var before = PresentationSource.CurrentSources.Cast<PresentationSource>().ToHashSet();
        var notification = new NotificationService();
        notification.Hide(); // 从未显示也可以隐藏和清理。
        notification.Show("首次通知");
        var overlay = PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Where(source => !before.Contains(source)).Select(source => source.RootVisual).OfType<Window>().Single();
        True(overlay.IsVisible, "首次通知必须真正显示");
        notification.Hide();
        False(overlay.IsVisible, "隐藏后不能继续显示通知");
        notification.Show("第二次通知");
        True(overlay.IsVisible, "隐藏后仍能显示下一条通知");
        True(overlay.ShowActivated == false && overlay.ShowInTaskbar == false && overlay.Topmost,
            "通知不能抢焦点或进入任务栏，仍须置顶");
        True(Task.Run(() => notification.Show("退出前排队的通知")).Wait(TimeSpan.FromSeconds(1)),
            "后台通知不得等待 UI 线程");
        notification.Dispose();
        notification.Dispose();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        False(overlay.IsVisible, "退出后的排队通知不能复活窗口");
        notification.Show("退出后的通知");
        False(PresentationSource.CurrentSources.Cast<PresentationSource>().Any(source => !before.Contains(source)),
            "退出必须释放通知窗口句柄");
        using var neverShown = new NotificationService();
        neverShown.Hide();
    });
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

static void UnregisteredHotkeysDiscardQueuedCallbacks()
{
    var pending = new Queue<Action>();
    var events = new List<string>();
    var manager = new HotkeyManager(() => true, pending.Enqueue);
    manager.QueueHookCallback(() => events.Add("旧按下"));
    manager.QueueHookCallback(() => events.Add("旧释放"));
    manager.UnregisterAll(); // 未注册原生热键，不调用键盘钩子或发送输入。
    manager.QueueHookCallback(() => events.Add("新按下"));
    manager.QueueHookCallback(() => events.Add("新释放"));
    while (pending.TryDequeue(out var callback)) callback();
    Equal(2, events.Count, "旧回调不能在重注册或捕获阶段重新触发功能");
    Equal("新按下", events[0], "有效按下顺序不能丢失");
    Equal("新释放", events[1], "有效释放必须跟随有效按下");

    manager.QueueHookCallback(() => events.Add("退出后旧事件"));
    manager.UnregisterAll();
    manager.UnregisterAll();
    while (pending.TryDequeue(out var callback)) callback();
    Equal(2, events.Count, "连续注销同样不能恢复旧事件");

    var holdRunning = false;
    var releases = 0;
    var holdRequest = new HotkeyRequest
    {
        Key = "F11",
        DisplayName = "虚拟按住操作",
        CheckForeground = false,
        Mode = HotkeyMode.Hold,
        Handler = () => holdRunning = true,
        ReleaseHandler = () => { holdRunning = false; releases++; },
    };
    manager.QueueHoldCallback(holdRequest, start: true);
    pending.Dequeue()();
    True(holdRunning, "有效按下已经执行时必须记住待释放操作");
    manager.QueueHoldCallback(holdRequest, start: false);
    manager.UnregisterAll();
    False(holdRunning, "松开已排队但尚未执行时，注销仍须同步释放实际运行操作");
    Equal(1, releases, "注销应释放一次");
    while (pending.TryDequeue(out var callback)) callback();
    Equal(1, releases, "失效的松开事件不得再次释放");

    manager.QueueHoldCallback(holdRequest, start: true);
    manager.QueueHoldCallback(holdRequest, start: false);
    while (pending.TryDequeue(out var callback)) callback();
    False(holdRunning, "同一批次有效按下与松开必须依次执行");
    Equal(2, releases, "新批次有效松开应保留");
}

static void RuntimeFailuresReachTopmostNotification()
{
    var messages = new List<string>();
    var manager = new HotkeyManager(() => true);
    manager.RuntimeErrorOccurred += messages.Add;
    var throwingRequest = new HotkeyRequest
    {
        Key = "F5",
        DisplayName = "测试运行操作",
        CheckForeground = true,
        Mode = HotkeyMode.Toggle,
        Handler = () => throw new InvalidOperationException("模拟执行失败"),
    };

    False(manager.TryInvoke(throwingRequest), "热键回调异常必须转换为安全失败");
    Equal(1, messages.Count, "热键回调异常必须上报一次");
    True(messages[0].Contains("测试运行操作执行失败", StringComparison.Ordinal),
        "运行错误必须指出失败功能和阶段");

    var detectorMessages = new List<string>();
    var failingDetector = new HotkeyManager(() => throw new InvalidOperationException("模拟前台检测失败"));
    failingDetector.RuntimeErrorOccurred += detectorMessages.Add;
    False(failingDetector.TryInvoke(throwingRequest), "前台检测异常必须阻止功能运行");
    Equal(1, detectorMessages.Count, "前台检测异常必须上报一次");
    True(detectorMessages[0].Contains("前台检查失败", StringComparison.Ordinal),
        "前台检测错误必须与普通非游戏前台静默拦截区分");

    var root = FindProjectRoot();
    var host = File.ReadAllText(Path.Combine(root, "Host", "ToolHost.cs"));
    var hideout = File.ReadAllText(Path.Combine(root, "Tools", "Hideout", "HideoutTool.cs"));
    var notification = File.ReadAllText(Path.Combine(root, "Services", "NotificationService.cs"));
    True(host.Contains("Hotkeys.RuntimeErrorOccurred += Notification.ShowError", StringComparison.Ordinal),
        "宿主必须把热键运行异常接到置顶错误服务");
    True(hideout.Contains("Notification.ShowError", StringComparison.Ordinal),
        "一键回城输入异常不得继续静默吞掉");
    True(notification.Contains("MB_TOPMOST", StringComparison.Ordinal) &&
         notification.Contains("MB_SETFOREGROUND", StringComparison.Ordinal),
        "运行错误服务必须请求置顶和前台显示");
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

static void NewHotkeyClearsSemanticConflicts()
{
    var requested = new HotkeySettings(
        "Control+Alt+F5", "Alt+Ctrl+F5", "ctrl+alt+f5", "F8", "F11", "F9", "F2");
    var result = HotkeyConflictResolver.Resolve(requested, "coordinate");
    Equal("ctrl+alt+f5", result.Hotkeys.Coordinate, "必须保留当前录入项");
    Equal(string.Empty, result.Hotkeys.CraftStart, "不同别名的相同触发器必须清空");
    Equal(string.Empty, result.Hotkeys.CraftStop, "同一触发器的所有旧绑定都必须清空");
    Equal(2, result.ClearedNames.Count, "必须反馈全部被清空的旧项");
    True(result.ClearedNames.Contains("启动洗装") && result.ClearedNames.Contains("停止洗装"),
        "反馈必须指出受影响功能");
    Equal("F11", result.Hotkeys.ClickerHold, "无冲突绑定不得被清空");
    Equal("Control+Alt+F5", requested.CraftStart, "解析候选不得修改原始快照");
    Equal(0, SettingsValidation.ValidateHotkeys(result.Hotkeys).Count, "清空后必须是合法热键集合");
}

static void HotkeysSupportReassignmentAndClearing()
{
    var defaults = new HotkeySettings("F5", "F6", "F7", "F8", "F11", "F9", "F2");
    foreach (var target in defaults.GetEntries())
    {
        foreach (var owner in defaults.GetEntries().Where(entry => entry.Id != target.Id))
        {
            var result = HotkeyConflictResolver.Resolve(defaults.WithKey(target.Id, owner.Key), target.Id);
            Equal(owner.Key, result.Hotkeys.GetEntries().Single(entry => entry.Id == target.Id).Key,
                $"{target.Name}应取得{owner.Name}原有按键");
            Equal(string.Empty, result.Hotkeys.GetEntries().Single(entry => entry.Id == owner.Id).Key,
                $"{owner.Name}旧绑定必须清空");
            Equal(1, result.ClearedNames.Count, "一次普通覆盖应只清空一个旧项");
            foreach (var unchanged in defaults.GetEntries().Where(entry => entry.Id != target.Id && entry.Id != owner.Id))
                Equal(unchanged.Key, result.Hotkeys.GetEntries().Single(entry => entry.Id == unchanged.Id).Key,
                    $"{unchanged.Name}必须保持不变");
            Equal(0, SettingsValidation.ValidateHotkeys(result.Hotkeys).Count, "Toggle 与 Hold 覆盖后都应合法");
        }

        var cleared = HotkeyConflictResolver.Resolve(defaults.WithKey(target.Id, string.Empty), target.Id);
        Equal(0, cleared.ClearedNames.Count, "主动清空不得影响其他绑定");
        Equal(defaults.WithKey(target.Id, string.Empty), cleared.Hotkeys, "主动清空只能改变当前项");
    }
}

static void EmptyHotkeysRoundTrip()
{
    RunInSta(() =>
    {
        var source = CreateSettingsFixture();
        source.Settings.LoadSettings(JsonSerializer.SerializeToElement(new
        {
            hotkeys = new { start = "", stop = "  ", coordinate = "" },
        }));
        source.Clicker.LoadSettings(JsonSerializer.SerializeToElement(new { hotkey = "", hold_hotkey = "  " }));
        source.KeyLoop.LoadSettings(JsonSerializer.SerializeToElement(new { hotkey = "" }));
        source.Hideout.ApplySharedSettings(true, "", "/menagerie");
        var empty = new HotkeySettings("", "", "", "", "", "", "");
        Equal(empty, source.Settings.CaptureDraft().Hotkeys, "运行时必须接受全部显式空绑定");
        Equal(0, SettingsValidation.ValidateHotkeys(empty).Count, "空值不是非法热键或重复触发器");
        True(SettingsValidation.ValidateHotkeys(empty with { CraftStart = "not-a-key" }).Count > 0,
            "允许空值后仍须拒绝无法识别的非空按键");

        var restored = CreateSettingsFixture();
        restored.Settings.LoadSettings(SerializeToolSettings(source.Settings));
        restored.Clicker.LoadSettings(SerializeToolSettings(source.Clicker));
        restored.KeyLoop.LoadSettings(SerializeToolSettings(source.KeyLoop));
        restored.Hideout.LoadSettings(SerializeToolSettings(source.Hideout));
        Equal(empty, restored.Settings.CaptureDraft().Hotkeys, "模拟重启后七项都不得恢复默认热键");
        Equal("/menagerie", restored.Hideout.Command, "空热键不能改变回城命令");
        True(restored.Hideout.IsEnabled, "空热键不能改变工具启用状态");

        using var invalid = JsonDocument.Parse("{\"missing_type\":null}");
        Equal("F5", HotkeySetting.Read(invalid.RootElement, "missing", "F5"), "缺少字段仍需默认值");
        Equal("F6", HotkeySetting.Read(invalid.RootElement, "missing_type", "F6"), "非法字段类型仍需默认值");
    });
}

static void CraftRequiresValidStopHotkey()
{
    var craft = new CraftTool();
    foreach (var key in new[] { "", "  ", "Ctrl", "not-a-key" })
    {
        craft.HotkeyStop = key;
        True(craft.ValidateStartHotkeys() is not null, "停止键缺失或非法时启动检查必须失败");
    }
    foreach (var key in new[] { "F6", "Ctrl+F6" })
    {
        craft.HotkeyStop = key;
        True(craft.ValidateStartHotkeys() is null, "有效停止键应通过启动检查");
    }
}

static void SettingsRejectsUnsafeValues()
{
    var draft = new SettingsDraft(
        new HotkeySettings("F5", "F6", "F7", "F8", "F11", "F9", "F2"),
        "..\\PathOfExile.exe", true, true, true, "..\\outside.mp3", false, false,
        true, "/hideout\n/exit", true, false, false);
    var errors = SettingsValidation.Validate(draft);
    True(errors.Any(error => error.Contains("目标进程", StringComparison.Ordinal)),
        "目标进程路径必须被拒绝");
    True(errors.Any(error => error.Contains("音效", StringComparison.Ordinal)),
        "音效目录穿越必须被拒绝");
    True(errors.Any(error => error.Contains("控制字符", StringComparison.Ordinal)),
        "回城命令换行必须被拒绝");
}

static void CtrlHoldClickerConflictsWithCraft()
{
    var safe = new SettingsDraft(
        new HotkeySettings("F5", "F6", "F7", "F8", "Ctrl", "F9", "F2"),
        "PathOfExile_x64.exe", true, true, true, "default_ding.wav", false, false,
        false, "/hideout", true, false, false);
    False(SettingsValidation.Validate(safe).Any(error => error.Contains("不能同时启用", StringComparison.Ordinal)),
        "连点器停用时允许预先配置 Ctrl 按住热键");

    var conflict = safe with { ClickerEnabled = true };
    True(SettingsValidation.Validate(conflict).Any(error => error.Contains("不能同时启用", StringComparison.Ordinal)),
        "Craft 与 Ctrl 按住连点同时启用时必须拒绝");

    var craft = new CraftTool();
    var clicker = new ClickerTool();
    clicker.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = true, hold_hotkey = "Ctrl" }));
    var registry = new ToolRegistry();
    registry.Register(craft);
    registry.Register(clicker);
    True(ToolEnablement.ValidateCompatibility(registry.Tools)?.Contains("不能同时启用", StringComparison.Ordinal) == true,
        "运行时启用事务必须执行同一安全约束");

    var clickerOnly = new ToolRegistry();
    clickerOnly.Register(clicker);
    True(ToolEnablement.ValidateCompatibility(clickerOnly.Tools) is null,
        "未注册 Craft 的独立宿主不得产生虚假冲突");
}

static void HotkeyTransactionRollsBackOnFailure()
{
    var old = new HotkeySettings("F5", "F6", "F7", "F8", "F11", "F9", "F2");
    var candidate = HotkeyConflictResolver.Resolve(old with { KeyLoop = "F6" }, "keyloop").Hotkeys;
    var current = old;
    var attempts = 0;
    var result = HotkeySettingsTransaction.TryApply(
        old,
        candidate,
        value => current = value,
        () =>
        {
            attempts++;
            return current == candidate ? ["F6 被其他程序占用"] : [];
        });

    False(result.Success, "候选热键注册失败时事务不得成功");
    Equal(old, current, "注册失败后必须恢复全部旧热键字段");
    Equal(2, attempts, "失败后必须再次注册恢复的旧热键集合");
}

static void SettingsSaveFailureRestoresFullDraft()
{
    RunInSta(() =>
    {
        var fixture = CreateSettingsFixture();
        var previous = fixture.Settings.CaptureDraft();
        var resolution = HotkeyConflictResolver.Resolve(previous.Hotkeys with { KeyLoop = "F6" }, "keyloop");
        var candidate = previous with
        {
            Hotkeys = resolution.Hotkeys,
            TargetProcess = "PathOfExile_x64.exe",
            CraftSoundEnabled = false,
            HideoutCommand = "/menagerie",
        };
        var registerAttempts = 0;
        var saveAttempts = 0;
        SettingsDraft? persisted = null;
        var result = fixture.Settings.ApplyDraft(candidate,
            () => { registerAttempts++; return []; },
            () =>
            {
                saveAttempts++;
                if (saveAttempts == 1) throw new IOException("模拟新配置写入失败");
                persisted = fixture.Settings.CaptureDraft();
            });

        False(result.Success, "保存失败不得返回成功");
        Equal(previous, fixture.Settings.CaptureDraft(), "必须恢复包括被清空项在内的完整运行时设置");
        Equal(previous, persisted!, "恢复写入必须使用完整旧配置");
        Equal(2, registerAttempts, "保存失败后必须恢复旧热键注册");
        Equal(2, saveAttempts, "保存失败后必须尝试回写旧设置");
    });
}

static void SettingsRollbackFailureIsReported()
{
    RunInSta(() =>
    {
        var fixture = CreateSettingsFixture();
        var previous = fixture.Settings.CaptureDraft();
        var candidate = previous with { Hotkeys = previous.Hotkeys with { CraftStart = "F10" } };
        var registerAttempts = 0;
        var result = fixture.Settings.ApplyDraft(candidate,
            () =>
            {
                if (++registerAttempts > 1) throw new InvalidOperationException("模拟旧热键恢复异常");
                return [];
            },
            () => throw new IOException("模拟持续写入失败"));

        False(result.Success, "恢复异常必须作为设置失败返回");
        Equal(previous, fixture.Settings.CaptureDraft(), "注册和保存异常不能妨碍恢复旧字段");
        True(result.Message.Contains("旧热键恢复异常", StringComparison.Ordinal), "必须报告热键恢复异常");
        True(result.Message.Contains("旧设置回写失败", StringComparison.Ordinal), "必须报告持久化恢复失败");
    });
}

static void ToolEnablementSaveFailureRollsBack()
{
    foreach (var previous in new[] { false, true })
    {
        WithTempDirectory(directory =>
        {
            var tool = new ClickerTool();
            void Apply(bool value) => tool.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = value }));
            Apply(previous);
            var storage = new StorageService(directory);
            void Save() => storage.UpdateSettings(root => root["clicker"] = new JsonObject { ["enabled"] = tool.IsEnabled });
            Save();
            var path = Path.Combine(directory, "settings.json");
            var original = File.ReadAllText(path);
            var attributes = File.GetAttributes(path);
            var registered = previous;
            var registrationCalls = 0;
            var stopped = 0;
            IReadOnlyList<string> Register() { registered = tool.IsEnabled; registrationCalls++; return []; }
            try
            {
                File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                var result = ToolEnablement.TryApply(tool, !previous, Apply, () => stopped++, Save, () => null, Register);
                False(result.Success, "只读保存失败不能报告开关修改成功");
                Equal(previous, tool.IsEnabled, "保存失败必须恢复原启用状态");
                Equal(previous, registered, "恢复热键必须使用原启用状态");
                Equal(2, registrationCalls, "保存失败必须恢复旧热键集合");
                Equal(original, File.ReadAllText(path), "只读原文件必须保持原内容");
                Equal(previous ? 1 : 0, stopped, "已执行的停用不得自动恢复运行");
            }
            finally { File.SetAttributes(path, attributes); }
            var retry = ToolEnablement.TryApply(tool, !previous, Apply, () => stopped++, Save, () => null, Register);
            True(retry.Success, "解除只读后必须能再次保存开关");
            Equal(!previous, storage.LoadSettings()["clicker"]!["enabled"]!.GetValue<bool>(), "重试结果必须真实落盘");
        });
    }
}

static void ToolEnablementFailuresDoNotPersist()
{
    foreach (var stage in new[] { "compatibility", "registration", "stop" })
    {
        var tool = new ClickerTool();
        var previous = stage == "stop";
        void Apply(bool value) => tool.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = value }));
        Apply(previous);
        var registrations = 0;
        var saves = 0;
        var result = ToolEnablement.TryApply(tool, !previous, Apply,
            () => throw new InvalidOperationException("模拟停止失败"),
            () => saves++,
            () => stage == "compatibility" ? "模拟安全冲突" : null,
            () => ++registrations == 1 && stage == "registration" ? ["模拟系统占用"] : []);
        False(result.Success, $"{stage}失败不得返回成功");
        Equal(previous, tool.IsEnabled, $"{stage}失败必须恢复原开关");
        Equal(0, saves, "前置操作失败不能写入新设置");
        Equal(stage == "compatibility" ? 0 : 2, registrations, "只恢复已尝试变更的热键注册");
    }
}

static void ToolEnablementRollbackFailureIsReported()
{
    var tool = new ClickerTool();
    var registrations = 0;
    var result = ToolEnablement.TryApply(tool, true,
        value => tool.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = value })),
        () => { }, () => throw new IOException("模拟保存失败"), () => null,
        () => ++registrations > 1 ? ["模拟旧热键恢复失败"] : []);
    False(result.Success, "恢复失败不能返回成功");
    False(tool.IsEnabled, "恢复注册失败仍须恢复运行时开关字段");
    True(result.Message.Contains("已尝试恢复", StringComparison.Ordinal) &&
         result.Message.Contains("旧热键恢复失败", StringComparison.Ordinal), "必须明确恢复失败");
    False(result.Message.Contains("已恢复原启用配置", StringComparison.Ordinal), "不能误报完整恢复成功");
}

static void HideoutCommandFailuresPreservePrevious()
{
    var tool = new HideoutTool();
    tool.SetCommand("/menagerie");
    var saves = 0;
    foreach (var invalid in new[] { "/hideout\n/exit", "/hideout\t", new string('a', 201) })
    {
        Throws<ArgumentException>(() => tool.SetCommand(invalid, () => saves++), "非法命令必须在持久化前拒绝");
        Equal("/menagerie", tool.Command, "非法输入不能污染运行时命令");
    }
    Equal(0, saves, "非法命令不得执行任何保存");
    Throws<IOException>(() => tool.SetCommand("/hideout", () => throw new IOException("模拟保存失败")),
        "保存失败须交给页面显示");
    Equal("/menagerie", tool.Command, "持久化失败必须恢复原命令");
    tool.SetCommand(" /hideout ", () => saves++);
    Equal("/hideout", tool.Command, "合法命令仍按原约定去除首尾空格");
    Equal(1, saves, "合法重试应能保存一次");
}

static void AutoDetectedTargetSaveFailureRollsBack()
{
    RunInSta(() =>
    {
        var fixture = CreateSettingsFixture();
        var previous = fixture.Settings.CaptureDraft().TargetProcess;
        Throws<IOException>(() => fixture.Settings.ApplyDetectedTarget("PathOfExile_x64.exe",
            () => throw new IOException("模拟自动检测保存失败")), "保存失败必须上报");
        Equal(previous, fixture.Settings.CaptureDraft().TargetProcess, "保存失败不能提前改变前台目标");
        string? persisted = null;
        fixture.Settings.ApplyDetectedTarget("PathOfExile_x64.exe", () => persisted = fixture.Settings.CaptureDraft().TargetProcess);
        Equal("PathOfExile_x64.exe", persisted!, "合法重试应同步目标设置");
    });
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

static void GradientSpeedSettingsRoundTrip()
{
    RunInSta(() =>
    {
        var fixture = CreateSettingsFixture();
        var host = fixture.Host;
        var settings = fixture.Settings;
        True(host.Storage.DataDir.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase) &&
             AppContext.BaseDirectory.Contains("内部用例", StringComparison.Ordinal), "速度验证必须使用隔离目录");
        var path = Path.Combine(host.Storage.DataDir, "settings.json");
        var original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            foreach (var json in new[] { "{}", "{\"gradient_speed\":-1}", "{\"gradient_speed\":6}",
                "{\"gradient_speed\":3.5}", "{\"gradient_speed\":true}", "{\"gradient_speed\":\"5\"}" })
            {
                using var invalid = JsonDocument.Parse(json);
                settings.LoadSettings(invalid.RootElement);
                Equal(SettingsDefaults.GradientSpeed, host.GradientSpeed, "缺失或非法速度应兼容默认值");
            }
            host.Storage.SaveSettings(JsonNode.Parse("""
                {"host":{"auto_detect_poe":false,"host_marker":"保留"},
                 "craft":{"delay_ms":77},"other_marker":"保留"}
                """)!.AsObject());
            foreach (var speed in new[] { 0, 5, 1 })
            {
                host.SaveGradientSpeed(speed);
                var stored = host.Storage.LoadSettings();
                Equal(speed, stored["host"]!["gradient_speed"]!.GetValue<int>(), "改动速度必须立即落盘，包括 0");
                Equal("保留", stored["host"]!["host_marker"]!.GetValue<string>(), "速度单项保存不能覆盖宿主其他字段");
                Equal(77, stored["craft"]!["delay_ms"]!.GetValue<int>(), "速度不能改变洗词缀设置");
                Equal("保留", stored["other_marker"]!.GetValue<string>(), "速度不能覆盖其他配置");
                host.GradientSpeed = SettingsDefaults.GradientSpeed;
                settings.LoadSettings(JsonSerializer.SerializeToElement(stored["host"]));
                Equal(speed, host.GradientSpeed, "重新加载必须恢复速度，不能重置成 2");
                ToolSettingsPersistence.SaveSections(host.Storage, [settings]);
                Equal(speed, host.Storage.LoadSettings()["host"]!["gradient_speed"]!.GetValue<int>(),
                      "设置页/退出整节保存不能丢失速度");
                // 恢复标记，继续检查下一次单项写入不会覆盖同节其他字段。
                host.Storage.UpdateSettings(root => root["host"]!["host_marker"] = "保留");
            }
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                ExpectStorageFailure(() => host.SaveGradientSpeed(4), StorageFailureKind.FileAccess, path);
                Equal(1, host.GradientSpeed, "写入失败不能改变内存中的已保存速度");
            }
            Equal(1, host.Storage.LoadSettings()["host"]!["gradient_speed"]!.GetValue<int>(), "失败后原文件速度必须保持");
        }
        finally
        {
            foreach (var tool in host.RegisteredTools) tool.OnShutdown();
            host.Hotkeys.UnregisterAll(); host.Sound.Dispose(); host.Notification.Dispose(); host.Network.Dispose();
            if (original is null) File.Delete(path); else File.WriteAllBytes(path, original);
        }
    });
}

static void ToolHostBuildsEnabledHotkeySet()
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
        Equal(3, requests.Count, "默认仅启用 Craft，应注册启动、停止和坐标三个热键");

        clicker.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = true }));
        keyLoop.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = true }));
        hideout.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = true }));
        requests = host.BuildHotkeyRequests();
        Equal(7, requests.Count, "应汇总 Craft 2 + Clicker 2 + KeyLoop 1 + Hideout 1 + 坐标 1");
        Equal(1, requests.Count(request => request.DisplayName == "坐标录制"), "坐标热键必须只注册一次");

        foreach (var tool in registry.Tools) tool.OnShutdown();
    });
}

static void ToolHostSkipsUnassignedHotkeys()
{
    RunInSta(() =>
    {
        var fixture = CreateSettingsFixture();
        fixture.Settings.LoadSettings(JsonSerializer.SerializeToElement(new
        {
            hotkeys = new { start = "", stop = "", coordinate = "" },
        }));
        fixture.Clicker.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = true, hotkey = "", hold_hotkey = "" }));
        fixture.KeyLoop.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = true, hotkey = "" }));
        fixture.Hideout.LoadSettings(JsonSerializer.SerializeToElement(new { enabled = true, hotkey = "" }));
        Equal(0, fixture.Host.BuildHotkeyRequests().Count, "已启用工具的空热键也必须被过滤");

        fixture.Host.CoordinateHotkey = "F12";
        var request = fixture.Host.BuildHotkeyRequests().Single();
        Equal("坐标录制", request.DisplayName, "非空宿主热键不得被空工具热键过滤");
        Equal("F12", request.Key, "必须保留当前有效绑定");
    });
}

static (SettingsTool Settings, CraftTool Craft, ClickerTool Clicker, KeyLoopTool KeyLoop,
    HideoutTool Hideout, ToolHost Host) CreateSettingsFixture()
{
    var craft = new CraftTool();
    var clicker = new ClickerTool();
    var keyLoop = new KeyLoopTool();
    var hideout = new HideoutTool();
    var settings = new SettingsTool(craft, clicker, keyLoop, hideout);
    var registry = new ToolRegistry();
    foreach (var tool in new ITool[] { craft, clicker, keyLoop, hideout, settings }) registry.Register(tool);
    var host = new ToolHost(registry);
    settings.Initialize(host);
    // 不初始化各工具引擎，避免测试启动后台任务、读取用户规则或发送输入。
    return (settings, craft, clicker, keyLoop, hideout, host);
}

static JsonElement SerializeToolSettings(ITool tool)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream)) tool.SaveSettings(writer);
    using var document = JsonDocument.Parse(stream.ToArray());
    return document.RootElement.Clone();
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

static void BrowserLinksRequireWebAddresses()
{
    foreach (var url in new[] { "https://example.com/download", "http://example.com/path?a=1", " https://example.com " })
        True(BrowserLauncher.TryNormalize(url, out _), "普通网页地址必须支持");
    foreach (var url in new[] { "", "file:///C:/Windows/notepad.exe", "javascript:alert(1)", "example.com", "https://user:secret@example.com", "https://example.com\n" })
        False(BrowserLauncher.TryNormalize(url, out _), "无效或非网页地址必须被拒绝");
}

static void NetworkValidatesVersionData()
{
    foreach (var body in new[] { "[]", "{\"latest\":42}", "{\"latest\":\"bad\"}", "{\"latest\":\"99.0\",\"url\":null}", "{\"latest\":\"99.0\",\"url\":\"file:///C:/x.exe\"}" })
    {
        var logs = new List<string>();
        using var service = new NetworkService(new StubHttpMessageHandler((_, _) => Task.FromResult(JsonReply(body))), logs.Add);
        True(service.CheckVersionAsync().GetAwaiter().GetResult() is null, "无效新版信息不能触发强制退出");
        Equal(1, logs.Count, "无效信息必须有可诊断日志");
    }
    using var valid = new NetworkService(new StubHttpMessageHandler((_, _) => Task.FromResult(JsonReply("{\"latest\":\"99.0\",\"url\":\"https://example.com/new\"}"))), _ => { });
    var result = valid.CheckVersionAsync().GetAwaiter().GetResult();
    True(result is not null, "有效新版必须保持更新能力");
    Equal("https://example.com/new", result!.DownloadUrl, "下载地址错误");
}

static void NetworkKeepsValidAds()
{
    var logs = new List<string>();
    const string body = "{\"ads\":[null,{\"location\":\"top\",\"type\":\"text\",\"text\":\"保留\",\"link\":\"file:///C:/x.exe\"},{\"location\":\"bottom\",\"type\":\"text\",\"text\":\"正常\",\"link\":\"https://example.com/\"},{\"location\":\"top\",\"type\":42,\"text\":\"忽略\"}]}";
    using var service = new NetworkService(new StubHttpMessageHandler((_, _) => Task.FromResult(JsonReply(body))), logs.Add);
    var ads = service.FetchAdsAsync().GetAwaiter().GetResult();
    Equal(2, ads.Count, "坏条目不能丢掉全部广告");
    Equal("保留", ads[0].Text, "无效链接不能删除有效文案");
    True(ads[0].Link is null, "非网页链接不得进入界面");
    Equal("https://example.com/", ads[1].Link!, "正常链接须保留");
    Equal(1, logs.Count, "无效部分汇总记录一次");
}

static void NetworkFailuresAreDiagnosable()
{
    var logs = new List<string>();
    using (var failed = new NetworkService(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable))), logs.Add))
        True(failed.CheckVersionAsync().GetAwaiter().GetResult() is null, "HTTP 失败应保留静默跳过");
    True(logs.Any(line => line.Contains("HttpRequestException", StringComparison.Ordinal)), "HTTP 失败缺少日志");
    logs.Clear();
    using var oversized = new NetworkService(new StubHttpMessageHandler((_, _) => Task.FromResult(JsonReply(new string(' ', NetworkService.MaxResponseBytes + 1)))), logs.Add);
    Equal(0, oversized.FetchAdsAsync().GetAwaiter().GetResult().Count, "过大响应应使用缺省广告");
    True(logs.Any(line => line.Contains("超过 1MB", StringComparison.Ordinal)), "响应大小保护未留下原因");
}

static void NetworkCancellationIsRespected()
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var logs = new List<string>();
    using var cancellation = new CancellationTokenSource();
    using var service = new NetworkService(new StubHttpMessageHandler(async (_, token) =>
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return JsonReply("{}");
    }), logs.Add);
    var pending = service.CheckVersionAsync(cancellation.Token);
    True(entered.Task.Wait(TimeSpan.FromSeconds(1)), "虚拟请求未开始");
    cancellation.Cancel();
    Throws<OperationCanceledException>(() => pending.GetAwaiter().GetResult(), "退出取消应传播到请求调用方");
    Throws<OperationCanceledException>(() => service.SendPingAsync(cancellation.Token).GetAwaiter().GetResult(), "取消后不能继续统计请求");
    Throws<OperationCanceledException>(() => service.FetchAdsAsync(cancellation.Token).GetAwaiter().GetResult(), "取消后不能继续广告请求");
    Equal(0, logs.Count, "正常退出取消不应记录为联网故障");
}

static void NetworkPingUsesFixedPayload()
{
    string? body = null;
    string? agent = null;
    using var service = new NetworkService(new StubHttpMessageHandler(async (request, token) =>
    {
        Equal(HttpMethod.Post, request.Method, "统计必须保持 POST");
        Equal("/api/send", request.RequestUri!.AbsolutePath, "统计端点错误");
        body = await request.Content!.ReadAsStringAsync(token);
        agent = request.Headers.UserAgent.ToString();
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
    }), _ => { });
    service.SendPingAsync().GetAwaiter().GetResult();
    True(JsonNode.DeepEquals(NetworkService.CreatePingPayload(), JsonNode.Parse(body!)), "请求不能增加机器或游戏数据");
    True(agent!.Contains(NetworkService.CurrentVersion.ToString(3), StringComparison.Ordinal), "请求头版本必须来自当前程序集");
}

static HttpResponseMessage JsonReply(string body) => new(System.Net.HttpStatusCode.OK)
{
    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
};

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
    var project = System.Xml.Linq.XDocument.Load(Path.Combine(FindProjectRoot(), "拾刻.csproj"));
    var expected = Version.Parse(project.Descendants("Version").Single().Value);
    var version = NetworkService.CurrentVersion;
    Equal(expected.Major, version.Major, "程序集 Major 必须与项目声明一致");
    Equal(expected.Minor, version.Minor, "程序集 Minor 必须与项目声明一致");
    Equal(expected.Build, version.Build, "程序集 Build 必须与项目声明一致");
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

static void ExpectStorageFailure(Action action, StorageFailureKind kind, string path)
{
    try { action(); }
    catch (StorageException error)
    {
        Equal(kind, error.Kind, "存储异常分类错误");
        Equal(path, error.FilePath, "存储异常应保留实际路径");
        True(error.InnerException is not null, "必须保留原始错误供诊断");
        return;
    }
    throw new InvalidOperationException("预期存储失败，但操作成功");
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
        var sourceDirectory = Path.Combine(directory.FullName, "源码");
        if (File.Exists(Path.Combine(sourceDirectory, "拾刻.csproj")))
            return sourceDirectory;
        directory = directory.Parent;
    }
    throw new InvalidOperationException("无法定位拾刻.csproj");
}
