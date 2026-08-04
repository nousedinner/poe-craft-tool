# Changelog

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
