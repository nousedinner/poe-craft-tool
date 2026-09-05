using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Craft;

/// <summary>
/// 洗词缀抽屉（阶段3，最复杂工具）。
/// 实现 ITool + ICoordinateProvider：
/// - 热键声明：F5 启动（CheckForeground=true）/ F6 停止（false）
/// - 坐标槽位：物品 + 10 种通货（divine 占位永不显示，对齐 config_tab.py）
/// - 存储：craft 节 settings + rules.json/coordinates.json 兼容旧版
/// </summary>
public sealed class CraftTool : ITool, ICoordinateProvider
{
    private ToolHost? _host;
    private CraftEngine? _engine;
    private CraftPage? _page;

    public string Id => "craft";
    public string Name => "洗词缀";
    public string IconKey => "craft";

    // ── 运行时状态 ──
    public CraftRules Rules { get; } = new();
    public Dictionary<string, Point> Coordinates { get; } = [];

    // ── 设置（craft 节，默认值以 SettingsDefaults 为准）──
    public int DelayMs { get; set; } = SettingsDefaults.DelayMs;
    public bool SoundEnabled { get; set; } = SettingsDefaults.SoundEnabled;
    public bool PopupEnabled { get; set; } = SettingsDefaults.PopupEnabled;
    public int ExhaustionThreshold { get; set; } = SettingsDefaults.ClipboardUnchangedThreshold;
    public string SelectedSound { get; set; } = SettingsDefaults.SelectedSound;
    public bool Mode2ScourAlch { get; set; }
    public bool UseExalt { get; set; }

    // ── 热键（host 节，默认 F5/F6）──
    public string HotkeyStart { get; set; } = SettingsDefaults.HotkeyStart;
    public string HotkeyStop { get; set; } = SettingsDefaults.HotkeyStop;

    public bool IsRunning => _engine?.IsRunning ?? false;

    /// <summary>引擎引用（CraftPage 订阅事件用）。</summary>
    public CraftEngine? Engine => _engine;

    public void Initialize(ToolHost host)
    {
        _host = host;
        _engine = new CraftEngine(host);

        // 全局错误兜底：洗词缀页未打开时也能看到启动错误（坐标缺失/验证失败等）
        _engine.ErrorOccurred += msg => host.Notification.ShowError(msg);

        // 成功/耗尽/错误由引擎各自通知；只有普通用户停止在这里补一次提示，避免覆盖最终结果。
        _engine.Stopped += reason =>
        {
            if (PopupEnabled && reason == "已停止") host.Notification.Show("⏹ 已停止");
        };

        LoadRules();
        LoadCoordinates();
    }

    /// <summary>从 rules.json 加载规则（兼容旧版：mode 为 int、affix 项为 str 或 dict）。</summary>
    private void LoadRules()
    {
        if (_host is null) return;
        var data = _host.Storage.LoadRules();
        if (data is null) return;

        Rules.Mode = ReadMode(data["mode"]);

        if (data["single_currency"] is JsonValue sc)
            Rules.SingleCurrency = sc.GetValue<string>() ?? Currency.Alteration;

        Rules.PrimaryHitCount = ReadInt(data, "primary_hit_count", 1);
        Rules.SecondaryHitCount = ReadInt(data, "secondary_hit_count", 0);

        ReadAffixes(data, "primary_affixes", Rules.PrimaryAffixes);
        ReadAffixes(data, "secondary_affixes", Rules.SecondaryAffixes);
        ReadAffixes(data, "exclude_affixes", Rules.ExcludeAffixes);
    }

    private void LoadCoordinates()
    {
        if (_host is null) return;
        Coordinates.Clear();
        foreach (var (key, pt) in _host.Storage.LoadCoordinates())
            Coordinates[key] = pt;
    }

    private static CraftMode ReadMode(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => (CraftMode)i,          // 旧版 int
        JsonValue v when v.TryGetValue<string>(out var s) => s switch            // 新版字符串
        {
            "single" => CraftMode.Single,
            "alt_aug" => CraftMode.AltAug,
            "alt_aug_regal" => CraftMode.AltAugRegal,
            _ => CraftMode.Single,
        },
        _ => CraftMode.Single,
    };

    private static int ReadInt(JsonObject obj, string key, int fallback)
        => obj[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    private static void ReadAffixes(JsonObject obj, string key, List<AffixRule> target)
    {
        if (obj[key] is not JsonArray arr) return;
        foreach (var item in arr)
        {
            var text = item switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,          // 旧版字符串
                JsonObject o when o["text"] is JsonValue tv && tv.TryGetValue<string>(out var t) => t, // dict 形式
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text))
                target.Add(new AffixRule { Text = text });
        }
    }

    public FrameworkElement CreatePage() => _page ??= new CraftPage(_host!, this);

    /// <summary>Settings 页修改共享热键后，刷新已经创建的 Craft 页面提示。</summary>
    internal void RefreshSettingsPresentation()
        => _page?.RefreshHotkeyHints(HotkeyStart, HotkeyStop, _host?.CoordinateHotkey ?? SettingsDefaults.HotkeySetCoord);

    // ── 热键声明 ──
    public IReadOnlyList<HotkeyRequest> GetHotkeyRequests() =>
    [
        new HotkeyRequest
        {
            Key = HotkeyStart,
            DisplayName = "启动洗装",
            CheckForeground = true,   // 启动类热键检查前台（Python _on_start）
            Mode = HotkeyMode.Toggle,
            Handler = () => StartFromHotkey(),
            ForegroundRejectedHandler = NotifyStartForegroundRejected,
        },
        new HotkeyRequest
        {
            Key = HotkeyStop,
            DisplayName = "停止洗装",
            CheckForeground = false,  // 停止类不检查（Python _on_stop）
            Mode = HotkeyMode.Toggle,
            Handler = Stop,
        },
    ];

    // ── 坐标槽位（物品 + 10 通货；divine 占位）──
    public IReadOnlyList<CoordinateSlot> GetCoordinateSlots()
    {
        var slots = new List<CoordinateSlot> { new() { SlotId = "item", DisplayName = "装备位置" } };
        foreach (var key in Currency.All)
            slots.Add(new CoordinateSlot { SlotId = key, DisplayName = $"{Currency.Label(key)}位置" });
        return slots;
    }

    // ── 启停（CraftPage 与热键共用）──

    /// <summary>把当前 Rules 序列化写入 rules.json（对齐 storage.py save_rules：mode 存 int）。</summary>
    public void SaveRulesToStorage()
    {
        if (_host is null) return;
        var obj = new JsonObject
        {
            ["mode"] = (int)Rules.Mode,
            ["single_currency"] = Rules.SingleCurrency,
            ["primary_affixes"] = new JsonArray(Rules.PrimaryAffixes.Select(a => (JsonNode)a.Text).ToArray()),
            ["primary_hit_count"] = Rules.PrimaryHitCount,
            ["secondary_affixes"] = new JsonArray(Rules.SecondaryAffixes.Select(a => (JsonNode)a.Text).ToArray()),
            ["secondary_hit_count"] = Rules.SecondaryHitCount,
            ["exclude_affixes"] = new JsonArray(Rules.ExcludeAffixes.Select(a => (JsonNode)a.Text).ToArray()),
        };
        _host.Storage.SaveRules(obj);
    }

    public void Start()
    {
        if (_host is null || _engine is null) return;
        if (string.IsNullOrWhiteSpace(_host.Foreground.TargetProcess))
        {
            _host.Notification.ShowError("尚未锁定游戏进程，请先启动游戏或在设置中选择目标进程");
            return;
        }

        var (ok, msg) = Rules.Validate();
        if (!ok)
        {
            _host.Notification.ShowError(msg);
            return;
        }

        _engine.SetStopKey(HotkeyStop); // 设置停止键 VK（GetAsyncKeyState 轮询用）
        _engine.Start(Rules, Coordinates, DelayMs, SoundEnabled, PopupEnabled,
            SelectedSound, ExhaustionThreshold, Mode2ScourAlch, UseExalt);
        if (_engine.IsRunning && PopupEnabled)
            _host.Notification.Show("▶ 洗词缀 启动");
    }

    private void StartFromHotkey()
    {
        if (_host is null) return;
        if (string.IsNullOrWhiteSpace(_host.Foreground.TargetProcess) ||
            !_host.Foreground.IsTargetForeground())
        {
            // 公共热键层已执行同一检查；这里保留二次防护，覆盖非热键调用和前台瞬时切换。
            NotifyStartForegroundRejected();
            return;
        }
        Diag.Log("[洗装] StartFromHotkey: 前台检查通过");
        // 启动前从 UI 收集最新规则（用户在页面配置后切到游戏按 F5，规则必须是最新的）
        _page?.CollectRulesFromUi();
        Start();
    }

    private void NotifyStartForegroundRejected()
    {
        if (_host is null) return;
        if (string.IsNullOrWhiteSpace(_host.Foreground.TargetProcess))
        {
            Diag.Log("[洗装] 启动热键: 未配置目标进程，拒绝启动");
            _host.Notification.ShowError("尚未锁定游戏进程，请先启动游戏或在设置中选择目标进程");
            return;
        }

        var current = ForegroundDetector.GetForegroundProcessName();
        Diag.Log($"[洗装] 启动热键: 前台检查失败, TargetProcess={_host.Foreground.TargetProcess}, 当前前台={current ?? "(null)"}");
        if (string.IsNullOrEmpty(current))
        {
            // 前台进程获取失败：OpenProcess 被拒（游戏管理员 + 拾刻普通权限）
            _host.Notification.ShowError(
                $"无法获取前台进程（目标: {_host.Foreground.TargetProcess}）\n\n" +
                "游戏可能以管理员身份运行，而拾刻不是。\n" +
                "请关闭拾刻后，右键「以管理员身份运行」再试。");
        }
        else
        {
            _host.Notification.ShowError(
                $"请切换到游戏窗口后重试\n\n当前前台: {current}\n目标: {_host.Foreground.TargetProcess}");
        }
    }

    public void Stop()
    {
        _engine?.Stop();
        // 通知由 _engine.Stopped 事件统一处理（覆盖所有停止路径）
    }

    public void OnActivate() { }

    public void OnDeactivate()
    {
        // 切换到其他抽屉时也保存当前页面，不能只依赖 F5 或最终退出。
        _page?.CollectRulesFromUi();
    }

    public void OnShutdown()
    {
        // 退出前收集页面最新值；CollectRulesFromUi 只更新/保存配置，不会启动输入。
        _page?.CollectRulesFromUi();
        if (_engine is not null && !_engine.Shutdown(TimeSpan.FromSeconds(2)))
            Diag.Log("[引擎] Shutdown: 2 秒内未完成关闭");
    }

    // ── 存储（craft 节）──

    public void LoadSettings(JsonElement section)
    {
        DelayMs = GetInt(section, "delay_ms", SettingsDefaults.DelayMs);
        SoundEnabled = GetBool(section, "sound_enabled", SettingsDefaults.SoundEnabled);
        PopupEnabled = GetBool(section, "popup_enabled", SettingsDefaults.PopupEnabled);
        ExhaustionThreshold = GetInt(section, "clipboard_unchanged_threshold", SettingsDefaults.ClipboardUnchangedThreshold);
        SelectedSound = GetString(section, "selected_sound", SettingsDefaults.SelectedSound);
        Mode2ScourAlch = GetBool(section, "mode2_scour_alch", false);
        UseExalt = GetBool(section, "use_exalt", false);
        Diag.Log($"[设置] Craft 已加载: delay={DelayMs}, sound={SoundEnabled}, popup={PopupEnabled}, " +
                 $"mode2ScourAlch={Mode2ScourAlch}, useExalt={UseExalt}");
    }

    public void SaveSettings(Utf8JsonWriter writer)
    {
        CreateSettingsSection().WriteTo(writer);
    }

    /// <summary>立即持久化当前 Craft 设置；规则仍写入兼容旧版的 rules.json。</summary>
    public void SaveSettingsToStorage()
    {
        if (_host is null) return;
        _host.Storage.UpdateSettings(root => root[Id] = CreateSettingsSection());
        Diag.Log($"[设置] Craft 已保存: mode={Rules.Mode}, currency={Rules.SingleCurrency}, " +
                 $"primary={Rules.PrimaryAffixes.Count}/{Rules.PrimaryHitCount}, " +
                 $"secondary={Rules.SecondaryAffixes.Count}/{Rules.SecondaryHitCount}, " +
                 $"exclude={Rules.ExcludeAffixes.Count}");
    }

    private JsonObject CreateSettingsSection() => new()
    {
        ["delay_ms"] = DelayMs,
        ["sound_enabled"] = SoundEnabled,
        ["popup_enabled"] = PopupEnabled,
        ["clipboard_unchanged_threshold"] = ExhaustionThreshold,
        ["selected_sound"] = SelectedSound,
        ["mode2_scour_alch"] = Mode2ScourAlch,
        ["use_exalt"] = UseExalt,
    };

    private static int GetInt(JsonElement section, string key, int fallback)
        => section.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static bool GetBool(JsonElement section, string key, bool fallback)
        => section.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False
            ? v.GetBoolean() : fallback;

    private static string GetString(JsonElement section, string key, string fallback)
        => section.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
}
