# 拾刻 C# 重构 — 技术架构文档

> 基于重构方案 v5（2026-08-04 定稿）
> 本文档定义接口、服务、数据流、线程模型——编码前的契约

---

## 1. 整体架构

```
┌─────────────────────────────────────────────────────┐
│                    MainWindow.xaml                     │
│  ┌──────────────────────────────────────────────────┐│
│  │              顶部广告区（50px，可多条）            ││
│  └──────────────────────────────────────────────────┘│
│  ┌──────────┐  ┌──────────────────────────────────┐  │
│  │  导航栏   │  │          内容区（工具页面）        │  │
│  │  (ListBox)│  │  ┌────────────────────────────┐  │  │
│  │           │  │  │   CraftPage / ClickerPage  │  │  │
│  │  洗词缀   │  │  │   / KeyLoopPage / ...      │  │  │
│  │  连点器   │  │  └────────────────────────────┘  │  │
│  │  按键循环 │  │  ┌────────────────────────────┐  │  │
│  │  一键回城 │  │  │     状态栏 + 广告位         │  │  │
│  └──────────┘  │  └────────────────────────────┘  │  │
│                 └──────────────────────────────────┘  │
└─────────────────────────────────────────────────────┘
         │                    │
    ┌────▼────┐         ┌────▼────┐
    │ToolHost │         │ToolHost │
    │ (门面)  │◄────────│ (门面)  │
    └────┬────┘         └────┬────┘
         │                   │
    ┌────▼────────────────────▼────┐
    │        Services (9个)         │
    │ InputSimulator  HotkeyManager │
    │ ForegroundDetector            │
    │ StorageService  CoordinateRec │
    │ NotificationService           │
    │ TrayService  SoundService     │
    │ NetworkService                │
    └──────────────────────────────┘
```

**数据流（以热键触发洗装为例）：**

```
用户按 F5
  → RegisterHotKey 发送 WM_HOTKEY
  → HotkeyManager 收到（UI 线程 Dispatcher）
  → 查找对应 HotkeyRequest，检查 CheckForeground
  → 调用 ForegroundDetector.IsTargetForeground()
  → 通过 → 调用 HotkeyRequest.Handler（UI 线程）
  → CraftTool.OnStartHotkey()
  → Task.Run 启动后台洗装循环
  → 循环内：InputSimulator 操作 → 读剪贴板 → 匹配词缀
  → 状态更新：IProgress<CraftProgress> → UI 线程更新状态栏
  → 命中：SoundService.Play() + NotificationService.Show()
  → 停止：CancellationToken 取消 → Task 结束
```

---

## 2. 接口定义

### 2.1 ITool（核心扩展点）

```csharp
public interface ITool
{
    // ── 身份 ──
    string Id { get; }            // "craft", "clicker", "keyloop", "hideout"
    string Name { get; }          // "洗词缀", "连点器", "按键循环", "一键回城"
    string IconKey { get; }       // 资源字典 key，先占位

    // ── 生命周期 ──
    void Initialize(ToolHost host);     // 保存 host 引用，读取设置
    FrameworkElement CreatePage();      // 创建并返回工具页面（首次调用时创建，后续返回缓存）
    void OnActivate();                  // 导航切到此工具时调用
    void OnDeactivate();                // 导航切走时调用
    void OnShutdown();                  // 程序退出时调用：停线程、保存设置、注销热键

    // ── 热键声明 ──
    IReadOnlyList<HotkeyRequest> GetHotkeyRequests();

    // ── 存储 ──
    void LoadSettings(JsonElement section);   // 宿主从 settings.json 的 [id] 节加载
    void SaveSettings(Utf8JsonWriter writer); // 宿主保存时调用，写入 [id] 节
}
```

### 2.2 ICoordinateProvider（可选能力）

```csharp
public interface ICoordinateProvider
{
    IReadOnlyList<CoordinateSlot> GetCoordinateSlots();
}

public sealed class CoordinateSlot
{
    public string SlotId { get; init; }       // "currency.chaos", "item"
    public string DisplayName { get; init; }  // "混沌石位置"
    public Point? DefaultPosition { get; init; }
}
```

**实现此接口的工具**：Craft（物品+8种通货=9个槽位）、Clicker（点击目标=1个槽位）

### 2.3 HotkeyRequest

```csharp
public sealed class HotkeyRequest
{
    public required string Key { get; init; }           // "F5", "F8"
    public required string DisplayName { get; init; }   // "启动洗装"
    public required bool CheckForeground { get; init; } // 停止类=false
    public required HotkeyMode Mode { get; init; }      // Toggle | Hold
    public required Action Handler { get; init; }       // 宿主保证 UI 线程调用
}

public enum HotkeyMode
{
    Toggle,  // RegisterHotKey：按下触发，再按停止
    Hold     // WH_KEYBOARD_LL：按下开始，松开停止
}
```

---

## 3. 服务定义

### 3.1 ToolHost（门面）

所有工具通过 ToolHost 访问公共服务，不直接引用 Service 实例。

```csharp
public sealed class ToolHost
{
    // 服务访问
    public InputSimulator Input { get; }
    public HotkeyManager Hotkeys { get; }
    public ForegroundDetector Foreground { get; }
    public StorageService Storage { get; }
    public NotificationService Notification { get; }
    public SoundService Sound { get; }
    public CoordinateRecorder Coordinates { get; }
    public NetworkService Network { get; }

    // 全局控制
    public CancellationTokenSource EmergencyCts { get; }
    public void TriggerEmergencyStop();

    // 工具间通信（极少用）
    public IReadOnlyList<ITool> RegisteredTools { get; }
}
```

### 3.2 InputSimulator

```csharp
public sealed class InputSimulator
{
    // 基础操作（每次操作前检查 EmergencyCts + 释放残留键）
    public void Click(int x, int y, int delayMs = 33);
    public void RightClick(int x, int y, int delayMs = 33);
    public void ShiftClick(int x, int y, int delayMs = 33);
    // ShiftClick 内部：±10px 随机偏移 + mouseDown/20ms/mouseUp 拆分
    public void MoveTo(int x, int y, int delayMs = 33);
    public void KeyDown(string key);
    public void KeyUp(string key);
    public void PressAndRelease(string key);
    public void ReleaseAllKeys();       // shift/ctrl/alt 全释放
    public void CtrlAltC();             // 国服复制物品文本

    // 内部
    private void CheckEmergencyStop();  // 检查光标 ≤(1,1) → EmergencyCts.Cancel()
    private void SafeMove(int x, int y, int delayMs);  // 坐标转 int + moveTo + 等待
}
```

**时序细节（来自 Python 版）：**
- `MoveTo` 后等 0.03s
- 右键通货后等 `delay × 3`
- `CtrlAltC` 后等 `max(delay × 5, 0.15)` 再读剪贴板
- `ShiftClick` 内部：releaseAllKeys → 0.05s → keyDown shift → delay → safeMove → delay → mouseDown → 0.02s → mouseUp → delay → keyUp shift → delay×2

### 3.3 HotkeyManager

```csharp
public sealed class HotkeyManager
{
    private readonly Dictionary<int, HotkeyRequest> _registered;  // hotkeyId → request
    private readonly List<nint> _hookIds;                         // WH_KEYBOARD_LL hook IDs

    public void RegisterAll(IReadOnlyList<HotkeyRequest> requests);
    // 1. 冲突检测：检查 Key 是否重复 → 重复则弹窗警告
    // 2. Toggle 模式：RegisterHotKey(hwnd, id, 0, vk) → 记录 hotkeyId
    // 3. Hold 模式：SetWindowsHookEx(WH_KEYBOARD_LL, ...) → 记录 hookId
    // 4. 回调统一经 Dispatcher.Invoke 调到 UI 线程

    public void UnregisterAll();
    // Toggle：UnregisterHotKey(hwnd, id)
    // Hold：UnhookWindowsHookEx(hook)
    // 两类 ID 分开清理

    public void ReRegister(IReadOnlyList<HotkeyRequest> requests);
    // UnregisterAll() → RegisterAll()，设置页改热键后调用
}
```

**热键默认值（来自 storage.py）：**

| 功能 | 默认键 | 模式 | CheckForeground |
|------|--------|------|-----------------|
| 启动洗装 | F5 | Toggle | true |
| 停止洗装 | F6 | Toggle | **false** |
| 坐标录制 | F7 | Toggle | **false**（用户可能已切到游戏，Python 版不检查前台） |
| 连点器 | F8 | Toggle | true |
| 按键循环 | F9 | Toggle | true |
| 连点按住 | F11 | Hold | true |
| 一键回城 | F2 | Toggle | true |

### 3.4 ForegroundDetector

```csharp
public sealed class ForegroundDetector
{
    public string? TargetProcess { get; set; }  // 空=不检测

    public bool IsTargetForeground();           // GetForegroundWindow + QueryFullProcessImageName
    public List<string> GetRunningProcesses();  // 枚举进程列表（设置页用）

    // 自动检测：启动 0.5s 后扫描 5 个变体
    // PathOfExile.exe / PathOfExile_x64.exe / PathOfExile / PathOfExileSteam.exe / PathOfExile_KG.exe
    public string? AutoDetectPoe();
}
```

### 3.5 StorageService

```csharp
public sealed class StorageService
{
    private readonly string _dataDir;  // exe 旁 data/

    // settings.json（按工具分节）
    public JsonDocument LoadSettings();
    public void SaveSettings(Utf8JsonWriter writer);

    // coordinates.json（兼容旧版）
    public Dictionary<string, Point> LoadCoordinates();
    public void SaveCoordinates(Dictionary<string, Point> coords);

    // rules.json（兼容旧版）
    public JsonDocument LoadRules();
    public void SaveRules(JsonDocument rules);

    // presets
    public List<string> ListPresets();           // data/presets/*.json
    public JsonDocument LoadPreset(string name);
    public void SavePreset(string name, JsonDocument preset);
    public void DeletePreset(string name);
}
```

**⚠️ settings.json 旧版迁移：**
Python 版 storage.py 的 settings 是平铺结构（所有键在顶层），C# 版改为按工具分节。**首次运行时检测旧版 settings.json → 自动迁移**：读旧平铺键 → 映射到新分节 → 写新格式 → 重命名旧文件为 `settings.json.bak`。迁移映射表：

| 旧键（平铺） | 新位置（分节） |
|-------------|---------------|
| `craft_hotkey` | `host.hotkeys.start` |
| `stop_hotkey` | `host.hotkeys.stop` |
| `coordinate_hotkey` | `host.hotkeys.coordinate` |
| `clicker_hotkey` | `clicker.hotkey` |
| `clicker_hold_hotkey` | `clicker.hold_hotkey` |
| `key_loop_hotkey` | `keyloop.hotkey` |
| `delay_ms` | `craft.delay_ms` |
| `target_process` | `host.target_process` |
| `selected_sound` | `craft.selected_sound` |
| （其余键按语义归类） | |

**settings.json 结构：**
```json
{
  "craft": {
    "mode": "single",
    "delay_ms": 33,
    "sound_enabled": true,
    "popup_enabled": true,
    "clipboard_unchanged_threshold": 10,
    "selected_sound": "default_ding.mp3"
  },
  "clicker": {
    "hotkey": "F8",
    "hold_hotkey": "F11",
    "button": "left",
    "interval_ms": 33
  },
  "keyloop": {
    "hotkey": "F9",
    "slots": [
      {"enabled": true, "key": "q", "delay_s": 0.5},
      {"enabled": false, "key": "", "delay_s": 1.0}
    ]
  },
  "hideout": {
    "hotkey": "F2",
    "command": "/hideout"
  },
  "host": {
    "hotkeys": {
      "start": "F5",
      "stop": "F6",
      "coordinate": "F7"
    },
    "target_process": "",
    "auto_detect_poe": true
  }
}
```

**coordinates.json 结构（兼容旧版）：**
```json
{
  "item": [1234, 567],
  "currency.alteration": [1100, 800],
  "currency.augmentation": [1200, 800],
  "currency.chaos": [1300, 800]
}
```

**rules.json / 预设格式（兼容旧版）：**
```json
{
  "mode": "alt_aug_regal",
  "single_currency": "chaos",
  "primary_affixes": ["物理伤害提高", "攻击速度"],
  "primary_hit_count": 2,
  "secondary_affixes": ["最大生命"],
  "secondary_hit_count": 1,
  "exclude_affixes": ["减少魔力保留"]
}
```
- `mode`：当前选择的洗装模式（"single" / "alt_aug" / "alt_aug_regal"）
- `single_currency`：Mode 1 选中的通货类型（"alteration" / "chaos" / "custom"）

### 3.6 NotificationService

```csharp
public sealed class NotificationService
{
    private readonly Window _overlay;  // 无边框 + Topmost + 透明背景 + 不抢焦点
    private readonly DispatcherTimer _dismissTimer;
    private DateTime _lastShowTime;    // 防重：2200ms 内不重复弹
    private bool _errorShown;          // 防风暴：同类错误只弹一次

    public void Show(string message);
    // 1. 检查距上次 Show 是否 < 2200ms → 是则跳过
    // 2. 设置文本、居中、显示
    // 3. 启动 2s 定时器 → 到期 Hide

    public void ShowError(string message);
    // 防风暴：_errorShown 标志，同一轮运行中重复错误不重复弹窗
    // （Python main_window.py:437-444 行为）

    public void ResetErrorFlag();  // 新一轮运行开始时重置

    public void Hide();
}
```

### 3.7 TrayService

```csharp
public sealed class TrayService
{
    private readonly NotifyIcon _icon;  // WinForms 互操作

    public void Initialize(Icon appIcon);
    // 右键菜单：显示主窗口 / 退出
    // 双击：显示主窗口

    public bool OnWindowClosing(CancelEventArgs e, bool isCraftRunning);
    // 洗装运行中 → e.Cancel=true, 最小化到托盘
    // 否则 → 保存设置、OnShutdown、退出
}
```

### 3.8 SoundService

```csharp
public sealed class SoundService
{
    private readonly MediaPlayer _player;

    public void Play(string soundFileName);
    // 1. 在 exe 旁 sounds/ 查找文件
    // 2. 找到 → 播放
    // 3. 未找到 → 按 stem 匹配任意音频后缀（.wav/.mp3/.wma）
    // 4. 仍未找到 → 回退 default_ding.mp3

    public List<string> ScanSounds();  // 扫描 sounds/ 目录
}
```

### 3.9 CoordinateRecorder

```csharp
public sealed class CoordinateRecorder
{
    private CoordinateSlot? _activeSlot;  // 当前正在录制的槽位
    private readonly StorageService _storage;

    public void StartRecording(CoordinateSlot slot);
    // 设置 _activeSlot → 按钮变"录制中"状态

    public void OnRecordHotkey();  // F7 触发
    // 取当前鼠标位置 → 存入 _activeSlot → 持久化 → 清除 _activeSlot

    public void CancelRecording();
    public Point? GetCoordinate(string slotId);
    public void ClearCoordinate(string slotId);
}
```

**⚠️ 信号分离铁律（Python 坑 #10 CRITICAL）：**
CoordinateRecorder 的录制触发（F7 → 取坐标 → 存槽位）与工具页面的通货选中逻辑**完全独立**。录制信号 `RecordingCompleted` 和选中信号 `CurrencySelected` 是两个不同的事件，绝不共享。点击"设定坐标"按钮只触发录制流程，不改变当前选中的通货类型。

### 3.10 NetworkService

```csharp
public sealed class NetworkService
{
    private readonly HttpClient _http;

    public async Task<VersionInfo?> CheckVersionAsync();
    // GET open.cancanneed.top/version.json → 比较版本号

    public async Task SendPingAsync(string appName);
    // POST open.cancanneed.top/api/send
    // Payload（Umami 格式）：{
    //   website: "xxx-xxx-xxx",  // website UUID
    //   url: "/app/shike",
    //   hostname: Environment.MachineName,
    //   screen: $"{Screen.PrimaryScreen.Bounds.Width}x{Screen.PrimaryScreen.Bounds.Height}",
    //   title: "拾刻",
    //   name: appName,
    //   language: "zh-CN"
    // }
    // 静默失败，不影响启动

    public async Task<List<AdItem>> FetchAdsAsync();
    // GET open.cancanneed.top/ads.json
    // 数据结构：{ads:[{location:"top"|"bottom", type:"text", text, style, link}]}
}

public sealed class AdItem
{
    public string Location { get; init; }  // "top" | "bottom"
    public string Type { get; init; }      // "text"
    public string Text { get; init; }
    public string? Style { get; init; }
    public string? Link { get; init; }
}
```

---

## 4. 线程模型

### 4.1 原则

| 场景 | 方式 | 说明 |
|------|------|------|
| UI 操作 | UI 线程直接执行 | 所有控件操作 |
| 后台长时间任务 | `Task.Run(action, token)` | 洗装循环、连点循环、按键循环 |
| 后台 → UI 更新 | `IProgress<T>` 或 `Dispatcher.Invoke` | 状态栏、计数器 |
| 可中断等待 | `Task.Delay(ms, token)` | 替代 `Thread.Sleep`，可被 CancellationToken 打断 |
| 空闲等待 | `ManualResetEventSlim.Wait()` 或 `SemaphoreSlim.Wait(token)` | 零 CPU，等待唤醒信号 |
| 紧急停止 | `ToolHost.EmergencyCts.Cancel()` | 全局取消，所有链接此 token 的 Task 自动停止 |

### 4.2 唤醒 vs 取消（对应 Python 坑 #5）

**必须分离两种信号：**

```csharp
// 唤醒信号：从空闲进入工作状态
private readonly ManualResetEventSlim _wakeSignal = new(false);

// 取消信号：停止当前操作
private CancellationTokenSource _workCts;

// 空闲循环
while (true)
{
    _wakeSignal.Wait();           // 零 CPU 等待
    _wakeSignal.Reset();

    if (!_running) continue;

    _workCts = new CancellationTokenSource();
    // 链接全局 EmergencyCts
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
        _workCts.Token, host.EmergencyCts.Token);

    try
    {
        DoWork(linked.Token);
    }
    catch (OperationCanceledException) { }
    finally
    {
        _workCts.Dispose();
    }
}

// 启动
public void Start()
{
    _running = true;
    _wakeSignal.Set();  // 唤醒
}

// 停止
public void Stop()
{
    _running = false;
    _workCts?.Cancel();   // 取消当前工作
    _wakeSignal.Set();    // 唤醒以检查 _running
}
```

### 4.3 状态更新节流

```csharp
private DateTime _lastStatusTime = DateTime.MinValue;

public void ReportStatus(IProgress<CraftProgress> progress, CraftProgress data)
{
    if (DateTime.Now - _lastStatusTime < TimeSpan.FromMilliseconds(250))
        return;
    _lastStatusTime = DateTime.Now;
    progress.Report(data);
}
```

### 4.4 错误上报（对应 Python 坑 #17）

```csharp
catch (Exception ex)
{
    // 先停线程，再发错误信号
    _running = false;
    _workCts?.Cancel();

    // 错误信号在 Task 已停止后发出
    Dispatcher.Invoke(() => onError?.Invoke(ex.Message));
}
```

---

## 5. 工具实现要点

### 5.1 CraftTool（洗词缀，最复杂）

**页面结构：**
- 模式选择（3 个 RadioButton）
- 通货网格（4 列，按 Mode 显示子集）
- 通货坐标录制（ICoordinateProvider，9 个槽位：物品 + 8 种通货）
- 词缀规则配置（主词缀池 + 命中数 + 次级词缀池 + 命中数 + 排除词缀）
- 预设管理（下拉框 + 保存/加载/删除）
- 操作延迟滑块（10-200ms，默认 33）
- 启动/停止按钮 + 状态显示

**热键声明：**
- F5 启动洗装（Toggle, CheckForeground=true）
- F6 停止洗装（Toggle, CheckForeground=false）

**引擎核心类：**
```
AffixEngine
  ├── ParseItemText(clipboard) → List<string> affixLines
  │   ├── 按 "--------" 分段
  │   ├── 过滤属性行（中英文 skip patterns）
  │   └── ⚠️ 不过滤装备名字（铁律）
  ├── CheckAffixes(affixLines, rules) → AffixCheckResult
  │   ├── 先查排除词缀 → 命中则 early return
  │   ├── 查主词缀 → 每条规则最多匹配一行（break）
  │   └── 查次级词缀 → 同上
  └── MeetsFinalRules(result, rules) → bool

CraftEngine
  ├── Mode1_SingleCurrency(token, progress)
  ├── Mode2_AltAug(token, progress)  // 含子模式：Alt+Aug / Scour+Alch
  ├── Mode3_AltAugRegal(token, progress)  // 含可选 Exalted
  └── 通用：ShiftClick + CtrlAltC + ReadClipboard + CheckAffixes
```

**通货网格布局（4列）：**
```
all_currencies = [
  ALTERATION, AUGMENTATION, ALCHEMY, SCOURING,   // 行1（Mode2 用）
  CHAOS, CUSTOM,                                  // 行2（Mode1 额外）
  REGAL, TRANSMUTATION, EXALTED,                  // 行3（Mode3 额外）
  DIVINE                                          // 行4（永不显示，占位）
]
Mode1 显示：ALTERATION, CHAOS, CUSTOM
Mode2 显示：ALTERATION, AUGMENTATION, ALCHEMY, SCOURING
Mode3 显示：ALTERATION, AUGMENTATION, REGAL, SCOURING, TRANSMUTATION, EXALTED
```

### 5.2 ClickerTool（连点器）

**页面结构：**
- 按键选择（左键/右键 Radio）
- 点击间隔（SliderInput，10-200ms，默认 33）
- 启动/停止按钮 + 点击计数

> **注意**：Python 版连点器是当前位置连点（鼠标放哪点哪），无坐标录制。ARCHITECTURE 早期版本误加了坐标录制，已还原。坐标版连点器作为后期新增功能。

**热键声明：**
- F8 连点器（Toggle, CheckForeground=true）
- F11 连点按住（Hold, CheckForeground=true）

**后台循环（单 Task 常驻）：**
```
while (true)
{
    wakeSignal.Wait();  // 零 CPU
    wakeSignal.Reset();
    if (!_running) continue;

    while (_running && !token.IsCancellationRequested)
    {
        // 紧急停止：光标 ≤(1,1)
        if (Cursor.Position.X <= 1 && Cursor.Position.Y <= 1) { Stop(); break; }

        // 前台检测
        if (!foreground.IsTargetForeground()) { Task.Delay(100); continue; }

        // 当前鼠标位置连点（Python 版行为：pyautogui.click() 无坐标参数）
        clickFn();
        clickCount++;
        Task.Delay(intervalMs, token);
    }
}
```

### 5.3 KeyLoopTool（按键循环）

**页面结构：**
- 10 个槽位（默认显示 5 + "加载更多"按钮）
- 每个槽位：启用开关 + 按键捕获 + 延迟输入（0.1-999s）

**热键声明：**
- F9 按键循环（Toggle, CheckForeground=true）

**后台循环（10 个独立 Task，各自由 CTS 控制）：**
```
// 每个槽位一个 Task
for (int i = 0; i < 10; i++)
{
    Task.Run(() => KeyLoopSlot(i, token));
}

void KeyLoopSlot(int index, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        if (!slots[index].Enabled) { Task.Delay(50, token); continue; }
        if (!foreground.IsTargetForeground()) { Task.Delay(100, token); continue; }

        InputSimulator.PressAndRelease(slots[index].Key);
        Task.Delay(TimeSpan.FromSeconds(slots[index].Delay), token);
    }
}
```

### 5.4 HideoutTool（一键回城）

**页面结构：**
- 简单：启用开关 + 热键绑定 + 命令输入框（默认 `/hideout`）

**热键声明：**
- F2 一键回城（Toggle, CheckForeground=true）

**实现：**
- 按热键 → 前台检测 → 打开游戏聊天 → 输入命令 → 回车
- 用 SendInput 模拟按键序列

---

## 6. UI 约定

### 6.1 导航

- 左侧 ListBox，项 = `ITool.Name`
- 选中 → 右侧 ContentPresenter 切换到对应 `ITool.CreatePage()`
- 首次选中时创建页面（懒加载）

### 6.2 状态栏（MainWindow 底部）

- 左侧：状态点（绿/灰）+ 状态文本
- 中间：已使用次数（洗装模式下显示）
- 右侧：使用说明链接 + 反馈链接 + 底部广告位

### 6.3 主题

- 初始糊弄版：纯色背景 + 基础控件
- 后美化参考：方案 D 色板（DEV_GUIDE §813-830）
  - 背景渐变：#c8dff0 → #dae8f5 → #e4eef8
  - 卡片：rgba(255,255,255,0.55), border-radius 10px
  - Accent：#1a6fb5
  - 绿：#1b8a3e，红：#d04555
  - 窗口：720×660 固定
- 样式全部在 `Themes/` 资源字典，美化只改这里

---

## 7. 边界（不做）

- **市集监控**：私有独立工具，不并入聚合工具
- **新增功能方向**：主体完成后讨论
- **游戏内存读取/修改**：只做外部键鼠+剪贴板，不碰游戏进程

---

## 8. 文件清单（编码时创建）

```
拾刻.csproj
App.xaml / App.xaml.cs
Host/
  ITool.cs
  ICoordinateProvider.cs
  ToolRegistry.cs
  ToolHost.cs
  MainWindow.xaml / MainWindow.xaml.cs
Services/
  InputSimulator.cs
  HotkeyManager.cs
  ForegroundDetector.cs
  StorageService.cs
  NotificationService.cs
  TrayService.cs
  SoundService.cs
  CoordinateRecorder.cs
  NetworkService.cs
Tools/
  Craft/
    CraftTool.cs
    AffixEngine.cs
    CraftEngine.cs
    CraftPage.xaml / CraftPage.xaml.cs
  Clicker/
    ClickerTool.cs
    ClickerPage.xaml / ClickerPage.xaml.cs
  KeyLoop/
    KeyLoopTool.cs
    KeyLoopPage.xaml / KeyLoopPage.xaml.cs
  Hideout/
    HideoutTool.cs
    HideoutPage.xaml / HideoutPage.xaml.cs
Themes/
  DefaultTheme.xaml
Resources/
  poe.ico
  sounds/
    default_ding.mp3
    (其他 mp3 文件)
data/
  settings.json (运行时生成)
  coordinates.json (运行时生成)
  presets/
    武器.json
```
