# 拾刻 C# 重构 — 实施计划

> 基于重构方案 v5 + ARCHITECTURE.md
> 每阶段结束可运行验证，不等最后

---

## 阶段 1：骨架

**目标**：宿主 + ITool 接口 + 主窗口 + 一个空抽屉跑通，验证架构可工作。

### 1.1 项目初始化
- 创建 .NET 10 WPF 项目 `拾刻.csproj`
- 配置：`<OutputType>WinExe</OutputType>`, `<TargetFramework>net10.0-windows</TargetFramework>`
- 添加 WinForms 互操作支持（托盘用）

**输出**：空窗口能编译运行

### 1.2 接口定义
- `Host/ITool.cs`：完整接口（身份+生命周期+热键声明+存储）
- `Host/ICoordinateProvider.cs`：可选坐标能力
- `Host/HotkeyRequest.cs`, `Host/CoordinateSlot.cs`

**输出**：接口文件存在，编译通过

### 1.3 ToolHost + ToolRegistry
- `Host/ToolHost.cs`：门面类，暂不注入真实服务（用 null 占位）
- `Host/ToolRegistry.cs`：注册表，`Register(ITool)` + `Tools` 列表

**输出**：能注册工具实例

### 1.4 MainWindow + 导航
- 左侧 ListBox（绑定 ToolRegistry.Tools）
- 右侧 ContentPresenter（选中时显示 ITool.CreatePage()）
- 懒加载：首次选中时创建页面

**输出**：启动后看到左侧导航列表，点击切换右侧内容区

### 1.5 空抽屉验证
- 创建 `Tools/Hideout/HideoutTool.cs`：实现 ITool，CreatePage 返回简单 TextBlock
- 注册到 ToolRegistry

**输出**：导航出现"一键回城"，点击显示页面

### 验收标准
- [ ] 编译运行，窗口显示
- [ ] 左侧导航有"一键回城"
- [ ] 点击切换右侧内容
- [ ] 关闭窗口程序退出

---

## 阶段 2：公共设施

**目标**：9 个服务全部实现，工具可用公共服务。

按依赖顺序实施，每完成一个即可验证。

### 2.1 InputSimulator
- P/Invoke：`SendInput`, `SetCursorPos`, `GetCursorPos`
- 实现：Click, RightClick, ShiftClick, MoveTo, KeyDown, KeyUp, PressAndRelease
- ShiftClick 内部：±10px 随机偏移 + mouseDown/20ms/mouseUp 拆分
- `ReleaseAllKeys()`：shift/ctrl/alt 全释放
- `CtrlAltC()`：国服复制快捷键
- 时序：MoveTo 后 0.03s，右键后 delay×3，CtrlAltC 后 max(delay×5, 0.15)
- 紧急停止：每次操作前 `CheckEmergencyStop()`（光标 ≤(1,1) → EmergencyCts.Cancel()）
- finally 块：`ReleaseAllKeys()`

**验证**：写测试程序，点击桌面指定位置，移动鼠标，紧急停止生效

### 2.2 HotkeyManager
- Toggle 模式：`RegisterHotKey` + 窗口消息 `WM_HOTKEY`
- Hold 模式：`SetWindowsHookEx(WH_KEYBOARD_LL, ...)` + key down/up 回调
- 冲突检测：注册前检查 Key 是否重复
- 回调经 `Dispatcher.Invoke` 到 UI 线程
- `UnregisterAll()`：两类 ID 分开清理
- `ReRegister()`：先注销再注册

**验证**：注册 F5 Toggle，按 F5 触发回调，注销后不再触发

### 2.3 ForegroundDetector
- P/Invoke：`GetForegroundWindow`, `QueryFullProcessImageNameW`
- `IsTargetForeground()`：比较进程名
- `GetRunningProcesses()`：`Process.GetProcesses()` 枚举
- `AutoDetectPoe()`：启动 0.5s 后扫描 5 个变体

**验证**：启动后自动检测 POE 进程，切换窗口时 IsTargetForeground 返回正确值

### 2.4 StorageService
- `data/` 目录：exe 旁自动创建
- `LoadSettings()` / `SaveSettings()`：settings.json 按工具分节
- `LoadCoordinates()` / `SaveCoordinates()`：coordinates.json 兼容旧版
- `LoadRules()` / `SaveRules()`：rules.json 兼容旧版
- 预设：`ListPresets()`, `LoadPreset()`, `SavePreset()`, `DeletePreset()`

**验证**：写入 settings.json → 读出 → 数据一致；读旧版 coordinates.json → 数据正确

### 2.5 NotificationService
- 无边框透明 Window + Topmost + 不抢焦点
- `Show(message)`：居中显示 + 2s 自动消失
- 防重：2200ms 内不重复弹
- Null Object：永不置空

**验证**：调用 Show()，弹窗出现 2s 后消失，快速连续调用不重复弹

### 2.6 TrayService
- `NotifyIcon`（WinForms 互操作）
- 右键菜单：显示 / 退出
- 双击：显示主窗口
- `OnWindowClosing()`：洗装运行中 → 最小化到托盘

**验证**：关闭窗口 → 最小化到托盘 → 双击恢复 → 右键退出

### 2.7 SoundService
- `MediaPlayer` 播放 wav/mp3/wma
- `Play(filename)`：查找 sounds/ → stem 回退 → 默认文件
- `ScanSounds()`：扫描 sounds/ 目录

**验证**：播放 default_ding.mp3，测试 stem 回退

### 2.8 CoordinateRecorder
- 录制流程：StartRecording(slot) → 等 F7 → OnRecordHotkey() → 取鼠标位置 → 存储
- 三态按钮状态管理
- 与 HotkeyManager 集成（F7 触发录制）

**验证**：点击"设定坐标" → 按钮变蓝 → 移动鼠标 → 按 F7 → 按钮变绿+显示坐标

### 2.9 NetworkService
- `HttpClient`，启动时后台 `Task.Run`
- `CheckVersionAsync()`：GET version.json
- `SendPingAsync()`：POST api/send，静默失败
- `FetchAdsAsync()`：GET ads.json，解析广告数据结构

**验证**：启动后后台请求，版本检查返回结果，签到静默执行

### 2.10 ToolHost 组装
- 注入所有真实服务
- MainWindow 启动时：注册工具 → Initialize → 注册热键
- MainWindow 关闭时：OnShutdown → 注销热键 → 保存设置
- 紧急停止广播：EmergencyCts 挂在 ToolHost 上

**验证**：启动 → 所有服务可用 → 关闭 → 热键注销 → 设置保存

### 验收标准
- [ ] 9 个服务全部可独立调用
- [ ] 热键注册/注销正常
- [ ] 前台检测自动识别 POE
- [ ] 存储读写正常，兼容旧版 JSON
- [ ] 通知弹窗 2s 消失
- [ ] 托盘最小化/恢复正常
- [ ] 音效播放正常
- [ ] 坐标录制三态正常
- [ ] 网络请求后台执行
- [ ] 紧急停止全局生效

---

## 阶段 3：洗装抽屉

**目标**：3 种洗装模式完整可用，词缀引擎 + 洗装引擎 + 预设。

这是最复杂的阶段，拆成 5 个子步骤。

### 3.1 词缀引擎（AffixEngine）
- `ParseItemText(clipboard)`：
  - 按 "--------" 分段
  - 过滤属性行（中英文 skip patterns，完整列表见 §6）
  - ⚠️ 不过滤装备名字（铁律）
- `CheckAffixes(affixLines, rules)`：
  - 排除词缀优先 → 主词缀 → 次级词缀
  - 每条规则最多匹配一行（break）
  - 子字符串匹配 + 可选数值范围（取行内所有数字）
- `MeetsFinalRules(result, rules)`：终检

**验证**：用真实剪贴板文本测试解析和匹配

### 3.2 洗装引擎（CraftEngine）— Mode 1
- `Mode1_SingleCurrency(token, progress)`：
  - 右键通货 → 等 delay×3（默认≈0.1s）→ Shift 按住
  - 循环：ShiftClick 物品 → CtrlAltC → 读剪贴板 → CheckAffixes
  - 命中 → 停止 + 通知 + 音效
  - 耗尽 → 停止 + 通知（连续 10 次剪贴板不变）
  - 状态更新：250ms 节流

**验证**：配置混沌石坐标 + 物品坐标 + 词缀规则 → F5 启动 → 自动洗 → 命中停止

### 3.3 洗装引擎 — Mode 2（含子模式）
- `Mode2_AltAug(token, progress)`：
  - 子模式 A（改造+增幅）：右键改造 → Shift 循环 → 1 词缀时增幅 → 终检
  - 子模式 B（重铸+点金）：右键点金 → 终检 → 重铸 → 循环
  - 增幅石只在恰好 1 词缀时使用
  - 排除词缀在循环中 = 跳过继续 roll
  - 坐标检查按子模式正确验证

**验证**：两种子模式分别测试

### 3.4 洗装引擎 — Mode 3（含可选崇高）
- `Mode3_AltAugRegal(token, progress)`：
  - 蜕变（白→蓝）→ 改造循环 → 增幅（1 词缀时）→ 阈值达标 → 富豪
  - 阈值 = 总需求 - 1
  - 富豪后可选崇高（checkbox）
  - 终检失败 → 重铸（黄→白）→ 回蜕变
  - Alt 循环效率：2 词缀未达标 → 继续 ShiftClick 不释放 Shift
  - 验证规则：主+次 ≤3，primary 可到 3（3+0 合法）

**验证**：用 DEV_GUIDE §158-167 的判定示例做验收用例

### 3.5 CraftTool 组装 + 预设 + UI
- `CraftTool.cs`：实现 ITool + ICoordinateProvider
  - Initialize：读设置、初始化引擎
  - CreatePage：返回 CraftPage
  - GetHotkeyRequests：F5 启动 + F6 停止
  - GetCoordinateSlots：物品 + 8 种通货
- `CraftPage.xaml`：
  - 模式选择（3 个 RadioButton）
  - 通货网格（4 列，按 Mode 显示/隐藏）
  - 词缀规则配置（主/次词缀池 + 命中数 + 排除）
  - 预设管理（下拉 + 保存/加载/删除）
  - 操作延迟滑块（10-200ms）
  - 启动/停止按钮 + 状态显示
- 预设管理器：data/presets/*.json 兼容旧版

**验证**：完整流程——配置 → 保存预设 → 加载预设 → 启动 → 命中 → 停止

### 验收标准
- [ ] 3 种模式均可正常洗装
- [ ] Mode 2 两种子模式均可用
- [ ] Mode 3 可选崇高正常
- [ ] 词缀匹配正确（装备名字参与、中文 skip patterns）
- [ ] 坐标录制 + 三态按钮正常
- [ ] 预设保存/加载/删除正常
- [ ] 耗尽检测正常
- [ ] 状态栏实时更新
- [ ] 命中通知 + 音效正常

---

## 阶段 4：连点 / 按键循环 / 回城

**目标**：剩余 3 个工具实现。

### 4.1 ClickerTool
- `ClickerTool.cs`：当前位置连点（无坐标录制，Python 版行为）
- `ClickerPage.xaml`：按键选择 + 间隔滑块 + 启停
- 热键：F8 Toggle + F11 Hold
- 后台循环：单 Task 常驻，CTS 控制启停
  - 前台检测 + 紧急停止 + 250ms 节流通知
  - Toggle：F8 按下开始/停止
  - Hold：F11 按住开始/松开停止

**验证**：F8 连点当前鼠标位置，F11 按住连点松开停止

### 4.2 KeyLoopTool
- `KeyLoopTool.cs`
- `KeyLoopPage.xaml`：10 槽位（默认显示 5 + 加载更多）+ 热键绑定
- 热键：F9 Toggle
- 10 个独立 Task，各自由 CTS 控制
  - 每槽位：启用开关 + 按键捕获 + 延迟（0.1-999s）
  - 前台检测

**验证**：配置 2 个槽位，F9 启动，验证按键按间隔循环

### 4.3 HideoutTool（完善阶段 1 的空抽屉）
- `HideoutTool.cs`
- `HideoutPage.xaml`：启用开关 + 热键绑定 + 命令输入框
- 热键：F2 Toggle
- 实现：打开聊天 → 输入 /hideout → 回车

**验证**：F2 触发，游戏内输入 /hideout

### 4.4 通知开关（连点/按键循环）
- 连点器/按键循环的启停通知默认关闭（Python 版行为）
- 保留开关，设置页可开启

### 验收标准
- [ ] 连点器 Toggle/Hold 均正常
- [ ] 按键循环 10 槽位正常
- [ ] 一键回城正常
- [ ] 前台检测在所有工具中生效
- [ ] 紧急停止在所有工具中生效

---

## 阶段 5：设置页 + 网络 + 联调

**目标**：设置页完整、网络功能上线、全流程联调。

### 5.1 设置页
- 热键配置：7 个 KeybindButton（启动/停止/坐标/连点/连点按住/按键循环/回城）
  - 变更即时保存 + HotkeyManager.ReRegister()
  - 捕获机制：控件焦点捕获（PreviewKeyDown）
- 前台进程下拉框 + 刷新按钮
- 音效：启用开关 + 下拉选择 + 刷新 + 打开目录
- 通知开关（弹窗提醒启用/停用）
- 一键回城：启用开关 + 热键绑定
- 隐私声明文案 + 查看统计链接

**验证**：修改热键 → 即时生效 → 重启后保持

### 5.2 NetworkService 接入 UI
- 版本检查：新版 → 强制弹窗 + 打开下载页 + 退出
- 签到上报：启动时后台执行
- 广告拉取：顶部广告区（多条）+ 底部广告位（首条）+ "广告位招租"占位
- 点击广告 → 浏览器打开 link

**验证**：启动 → 后台请求 → 版本弹窗正常 → 广告显示正常

### 5.3 状态栏完善
- 状态点（绿/灰）+ 状态文本
- 已使用次数（洗装模式）
- 使用说明链接 + 反馈链接（腾讯文档）
- 底部广告位

### 5.4 联调
- 完整流程：启动 → 自动检测 POE → 配置 → 启动洗装 → 命中 → 通知 → 停止
- 切换工具 → 热键正确切换
- 关闭窗口 → 托盘 → 恢复 → 退出
- 紧急停止 → 所有工具停止
- 前台检测 → 非 POE 窗口时暂停
- 设置变更 → 即时生效
- 预设保存/加载
- 旧版数据文件兼容

### 5.5 打包验证
- framework-dependent 单文件发布
- sounds/ 目录随包
- data/ 目录自动创建
- poe.ico 嵌入 exe
- 首次运行正常

### 验收标准
- [ ] 设置页所有项可配置
- [ ] 热键变更即时生效
- [ ] 网络三端点正常
- [ ] 广告显示正常
- [ ] 状态栏完整
- [ ] 全流程联调通过
- [ ] 打包后正常运行

---

## 里程碑总结

| 阶段 | 交付物 | 预计复杂度 |
|------|--------|-----------|
| 1 | 骨架 + 空抽屉 | 低 |
| 2 | 9 个公共服务 | 中 |
| 3 | 洗装抽屉（词缀引擎 + 洗装引擎 + 预设） | **高** |
| 4 | 连点 + 按键循环 + 回城 | 中 |
| 5 | 设置页 + 网络 + 联调 + 打包 | 中 |

阶段 3 是核心，占总工作量 40%+。建议阶段 3 完成后做一次完整实机测试（用 POE 游戏），验证输入模拟、剪贴板读取、时序等关键环节。
