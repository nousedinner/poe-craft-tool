# Changelog

## v1.0.9 — 2026-08-05

### SendInput 全面修复 + 音效/通知/停止键修复（7 项事故级修复）

**根因：INPUT 结构体 64 位对齐错误 + 缺少扫描码 + 多个行为未对齐 Python**

- **① INPUT 结构体 64 位对齐**（CRITICAL）：`FieldOffset(4)` 硬编码 union 偏移在 64 位上错误（应为 8），导致所有 `SendInput` 调用发送垃圾数据。改为 `LayoutKind.Sequential` 让 CLR 自动处理对齐
- **② 键盘事件加扫描码**：游戏用 DirectInput 只认硬件扫描码，`SendKey` 加 `MapVirtualKey(vk, 0)` 转换扫描码。F2 回城 / F5 洗装 / CtrlAltC 复制全部修复
- **③ PressAndRelease 加 10ms 延迟**：确保游戏能收到按下事件
- **④ F6 停止键 GetAsyncKeyState 轮询兜底**：`RegisterHotKey` 在游戏前台可能被吞，`PollStopKeyAsync` 每 50ms 轮询物理按键状态（对齐 Python `_check_stop_key`）
- **⑤ ReleaseAllKeysAsync 只释放键盘修饰键**：对齐 Python `_release_all`（只释放 shift/ctrl/alt），不释放鼠标按键。多余的 `LEFTUP` 会导致物品被拿起
- **⑥ 音效修复**：WPF `MediaPlayer` 需 UI 线程，改为 `Dispatcher.Invoke` 同步调用；`sounds/` 目录加 csproj `CopyToOutputDirectory` 复制到输出目录；mciSendString 对中文路径返回 error 266 已弃用
- **⑦ 通知修复**：去掉 2200ms 防重逻辑（F5 后 F6 通知被吞）；`Stopped` 事件统一弹悬浮窗（覆盖 PollStopKey 停止路径）；剪贴板为空/通货耗尽停止时加音效 + 悬浮窗
- **⑧ NotificationService WS_EX_TRANSPARENT 穿透**：`IsHitTestVisible` 对 WPF 分层窗口无效，改用 Win32 `WS_EX_TRANSPARENT` 扩展样式
- **⑨ ForegroundDetector 托管 API 兜底**：Win32 `OpenProcess` 失败时用 `Process.GetProcessById` 兜底
- **⑩ 诊断日志系统**：`Diag.cs` 写入 `data/debug.log`，全链路关键节点日志

## v1.0.8 — 2026-08-04

### C# 重构 阶段4：一键回城（HideoutTool 实装，编译通过 + 冒烟验证，实机待测）
- HideoutTool：F2 热键（Toggle, CheckForeground=true 抽屉自查）+ 行为对齐 Python `_execute_hideout`（main.py:26-35）：Enter → 0.1s → 输入 `/hideout` → Enter；失败静默（对齐 except: pass）
- 设置：hideout 节 {enabled, hotkey}，默认 false / F2（对齐 storage.py:123-124）；页面开关立即保存
- HideoutPage：启用开关 + 热键显示 + 使用说明（含管理员权限提示）
- InputSimulator：新增 TypeTextAsync（KEYEVENTF_UNICODE 逐字符输入，对齐 keyboard.write，每字符 5ms）+ KEYEVENTF_UNICODE 常量
- 前台检查：目标进程不在前台 → 静默忽略（对齐 Python _check_foreground，不弹窗）

### 🔍 第三方 AI 审查 7 条结论核实修复（详见 v1.0.7 小节）+ 新问题搁置
- 审查 A-G 全部修复（CheckForeground 自查 / 输入前等前台 / 原子化 / 动态 CTS / _runId 保护 / 通知线程安全 / finally 不可取消）
- 🆕 新问题：F5 前台检查弹窗"当前前台"空白（GetForegroundProcessName 返回 null/空），已改分场景诊断仍复现 → **用户决定搁置洗词缀，先推进阶段4，后续专项排查**

### ⚠️ 阶段4 实机反馈（2026-08-04，已记录，待排查——用户休息中）
- 现象：一键回城按 F2 **无任何响应**（无聊天框、无输入）
- 可能原因（未验证）：① hideout 节 enabled 默认 false，需在"一键回城"页打开启用开关；② **前台检查失败（GetForegroundProcessName 空白——与洗词缀问题同源）→ 回城静默忽略（对齐 Python 不弹窗）→ 用户感知"无响应"**
- 重点：回城与洗词缀共用 IsTargetForeground/GetForegroundProcessName——**"当前前台空白"很可能是两个功能同时失灵的公共根因**（前台进程名获取失败），优先级应高于洗词缀引擎本身
- 待查：GetForegroundProcessName 各失败路径（OpenProcess 权限 / GetForegroundWindow / QueryFullProcessImageNameW）逐一打日志定位

## v1.0.7 — 2026-08-04

### C# 重构 阶段3：洗装抽屉（实现完成；⚠️ 实机验证发现 2 个未解决问题，待第三方审查）
- Models：CraftMode / Currency（10 种+每模式显示子集）/ AffixRule / CraftRules(validate) / AffixCheckResult(终检)
- AffixEngine：**用户拍板行为 B**——分段 + 中英文 skip patterns 完整（铁律#3）+ **装备名字参与匹配**（铁律#1，宽松匹配）；排除优先 → 主 → 次，每条规则最多匹配一行
- CraftEngine：Mode1 单通货 / Mode2 改造+增幅（含重铸+点金子模式）/ Mode3 改+增+富（可选崇高）；单常驻 Task + 唤醒/取消分离（坑#5）；250ms 节流；耗尽检测（默认10次）；紧急停止（光标≤(1,1)）；时序逐行对齐 auto_operator.py（右键后 Mode2/3 额外 0.15s、ShiftClick 拆分、CtrlAltC 后 max(delay×5,0.15)）
- CraftTool：ITool + ICoordinateProvider；F5 启动 / F6 停止热键；craft 节设置；rules.json/coordinates.json 兼容旧版（mode int、affix str|dict）
- CraftPage：模式选择 / Mode2 子模式 / Mode3 崇高开关 / 延迟滑块(10-200ms) / 4 列通货网格（按模式显示子集）/ 三态坐标录制（F7，信号分离坑#10）/ 词缀池（主次排除 + 命中数实时验证 Mode2≤2 Mode3≤3）/ 预设管理（保存覆盖确认 + 加载 dirty 确认）/ 启停按钮 + 状态
- ClipboardHelper：Win32 剪贴板读取（后台 MTA 线程，WPF Clipboard 需 STA）
- App：注册 CraftTool、F7 坐标录制宿主级热键、IsCraftRunning 关闭窗口拦截
- 修复：Mode2 坐标检查按子模式（C# 修复 Python 固定查改造/增幅的错位 bug）

### ⚠️ 实机验证反馈（2026-08-04，问题未解决，已搁置——先推进阶段4，后续专项排查）
- 问题1：Mode1 改造洗 F5 启动后鼠标只移动到改造石坐标即停，无后续操作、无错误提示（游戏始终前台）
- 问题2：F6 停止后右键未释放（推断）：任务栏无法唤起窗口、桌面空白处点击自动弹右键菜单，手动点一下右键才恢复
- 已尝试修复（均未解决）：① EmergencyCts 永久取消（CTS 不可重置 vs Python Event.clear()）→ Start 前重置；② Loop finally 兜底释放不可取消（ReleaseAllKeys 含鼠标左右键）；③ 点击原子化（DOWN/UP 间 Task.Delay 改 CancellationToken.None）；④ 等待前台加节流状态提示；⑤ 空剪贴板停止分支（对齐 Python）；⑥ 全局错误订阅（页面未开也弹）
- 怀疑焦点：Python `_interruptible_sleep` 停止时返回 False 不抛异常（操作链完整执行完）vs C# `Task.Delay(ms, token)` 取消时抛 OCE（可能中断在任意 await 处）——取消语义差异
- 环境注意：游戏以管理员运行时拾刻须同权限（UIPI：热键收不到、SendInput 无效），用户已确认管理员运行后 F5 可收到

### 🔍 第三方 AI 审查（用户服务器运行，2026-08-04）7 条结论逐条核实与修复
| 审查项 | 核实 | 修复 |
|--------|------|------|
| A. HotkeyManager.WndProc 不检查 CheckForeground（162-170 直接 req.Handler()） | ✅ 属实 | CraftTool.StartFromHotkey 自查前台（对齐 Python _on_start）+ 提示；CheckForeground 标志由抽屉自查 |
| B. Mode1Async 首次右键在 AwaitForegroundAsync 之前 | ✅ 属实 | InputSimulator 注入 ForegroundDetector，所有输入操作（RightClick/ShiftClick/Click/HoldShift/CtrlAltC）开头等前台（对齐 Python 每次 sleep 检查） |
| C. InputSimulator:145 右键 down/up 可取消 | ⚠️ 半属实（坐标版已原子化，仅"当前位置右键"预留方法未改） | 已改 CancellationToken.None |
| D. ResetEmergencyStop() 后 InputSimulator 持有旧 CTS | ✅ 属实 | InputSimulator 改 Func<CancellationTokenSource> 动态取当前 CTS |
| E. finally 不检查 _runId（旧运行 Dispose 新运行 _workCts） | ✅ 属实 | Loop finally 的 _workCts.Dispose/Stopped 全部纳入 _runId 检查 |
| F. NotificationService.Show() 跨线程操作 WPF 控件（引擎线程调用） | ✅ 属实 | Show/ShowError 任意线程可调，自动 Dispatcher 转发 UI 线程 |
| G. CraftEngine:327 Mode1 ReleaseShiftAsync(token) 漏改 | ✅ 属实 | 改 CancellationToken.None（共 11 处全部不可取消） |

### 🆕 审查修复后新问题（2026-08-04，搁置待查）
- 现象：游戏前台按 F5 → 弹窗"请切换到游戏窗口后重试"，**"当前前台"显示空白**（GetForegroundProcessName 返回 null/空字符串）
- 分析：OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) 查前台进程被拒（游戏管理员 + 拾刻普通权限？）或 GetForegroundWindow 失败；已改为分场景诊断（获取失败 → 提示"请以管理员身份运行"；获取成功不匹配 → 显示两个名字），用户实测仍空白
- 待查方向：① 权限一致性验证（拾刻与游戏同权限）；② GetForegroundProcessName 各失败路径打日志；③ 前台检查在"获取失败"时是否应放宽（当前对齐 Python 返回 False 拒绝）
- **用户决定：洗词缀问题搁置，先推进阶段4（一键回城）验证功能链路，回头专项排查**

## v1.0.6 — 2026-08-04

### C# 重构 阶段2：9 个公共设施（编译通过 + 冒烟验证，行为级验收待阶段3/4 抽屉接入）
- InputSimulator：SendInput 封装（MoveTo/RightClick/ShiftClick/HoldShift/CtrlAltC/ReleaseAllKeys/Click）+ 光标 ≤(1,1) 紧急停止；时序对齐 Python auto_operator.py 源码
- HotkeyManager：RegisterHotKey(Toggle) + WH_KEYBOARD_LL(Hold) + 冲突检测收集 + Dispatcher 回 UI 线程 + 两类 ID 分开清理；HotkeyRequest 补 ReleaseHandler 字段（Hold 松开回调）
- ForegroundDetector：GetForegroundWindow + QueryFullProcessImageNameW + AutoDetectPoe(5 变体)
- StorageService：settings.json 分节 + 旧版平铺自动迁移(.bak) + coordinates/rules/presets 兼容旧版
- NotificationService：无边框透明窗（不抢焦点）+ 2s 消失 + 2200ms 防重 + 错误模态防风暴
- TrayService：NotifyIcon + 关闭拦截（洗装运行中最小化到托盘）
- SoundService：MediaPlayer + stem 同名不同后缀回退（default_ding.wav → .mp3）
- CoordinateRecorder：三态录制 + 录制完成事件与选中信号分离（坑 #10）
- NetworkService：版本检查(System.Version 比较) + 签到(嵌套 payload 对齐 Python) + 广告拉取
- ToolHost 组装 8 服务 + App 接线（热键注册/0.5s 自动检测 POE/网络后台/退出保存设置）
- MainWindow：顶部广告区（可多条+占位）+ 完整状态栏（状态点/说明/反馈/底部广告/使用次数）
- 修正 ARCHITECTURE.md 3 处文档-源码不一致：ShiftClick 时序（纯点击非自带 Shift）、签到 payload（嵌套非 Umami 平铺）、rules mode 格式（旧版 int）
- 拾刻.csproj：WPF 隐式 using 不含 System.IO/System.Net.Http，显式补回
- 修复：托盘/窗口图标不显示——poe.ico 嵌入程序集资源（pack URI 加载，原实现依赖 bin 下文件；顺带窗口标题栏/任务栏图标恢复）

## v1.0.5 — 2026-08-04

### C# 重构 阶段1：骨架（编译通过，待运行验证）
- 新建 .NET 10 WPF 项目 拾刻.csproj（WinExe / net10.0-windows / WinForms 互操作预留 / poe.ico 嵌入 / 版本单一来源 v1.0.5）
- 抽屉架构：ITool / ICoordinateProvider / HotkeyRequest / CoordinateSlot / ToolRegistry / ToolHost（EmergencyCts 全局紧急停止）
- MainWindow 导航：左侧 ListBox（绑定注册表）+ 右侧 ContentPresenter 懒加载 + 底部状态栏占位
- Tools/Hideout/ 空抽屉验证架构（阶段4 完善）
- Themes/DefaultTheme.xaml：方案D 基础色板
- .gitignore 补 bin/ obj/ *.user

## v1.0.4 — 2026-08-04

### 架构文档审查修正（本地AI审查反馈）
- ARCHITECTURE.md: 连点器还原为当前位置连点（坐标录制为新增功能，后置）
- ARCHITECTURE.md: rules.json 补 mode/single_currency 字段
- ARCHITECTURE.md: 右键通货等待统一为 delay×3（非固定 0.2s）
- ARCHITECTURE.md: 架构图补顶部广告区（50px）
- ARCHITECTURE.md: 补错误弹窗防风暴（_errorShown 标志）
- ARCHITECTURE.md: CoordinateRecorder 补信号分离铁律
- ARCHITECTURE.md: 补 settings.json 旧版平铺→分节迁移映射表
- ARCHITECTURE.md: F7 坐标录制 CheckForeground 改为 false
- ARCHITECTURE.md: 签到 payload 补 Umami 完整字段结构
- IMPLEMENTATION.md: 同步修正连点器描述 + 右键等待值

## v1.0.3 — 2026-08-04

### C# 重构架构文档
- ARCHITECTURE.md：技术架构文档（接口定义、服务清单、数据流、线程模型、工具实现要点）
- IMPLEMENTATION.md：实施计划（5阶段、每步验收标准）

## v1.0.2 — 2026-08-04

### 文档补充（重构准备）
- DEV_GUIDE.md: 明确"装备名字参与匹配"是设计意图（铁律），非容忍的Bug
- DEV_GUIDE.md: 输入框替代方案补充QLineEdit + QIntValidator（C#/WPF适用）
- PITFALLS.md: 新增第15条——装备名字必须参与匹配（铁律）
- PITFALLS.md: 重新编号16→17

## v1.0.1 — 2026-04-27

### 初始发布版
- 洗词缀自动化（3种模式：单通货/改造+增幅/改造+增幅+富豪）
- 连点器（左键/右键切换，热键控制）
- 按键循环（10个独立槽位，自定义间隔）
- 词缀匹配引擎（支持数值范围筛选）
- 前台进程检测（仅在游戏窗口激活时生效）
- 自定义音效提示（命中时弹通知+播放音效）
- 坐标录制系统（F7录制鼠标位置）
- 热键系统（F6启动/F7停止/F9连点/F11按住连点）
- 悬浮通知覆盖层
- PySide6 GUI，浅蓝半透明主题
- PyInstaller 一键打包
