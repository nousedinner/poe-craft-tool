# Changelog

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
