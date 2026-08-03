# POE 洗词缀工具 — 调试指南

**最后更新**: 2026-04-22 凌晨
**状态**: 多个 bug 已修复，连点器已重写，待测试

---

## 一、当前已修复的 bug

### 1. Mode 1 通货选择 bug（严重）
**现象**: 选了混沌石，工具却右键装备
**原因**: `_on_item_coord_click()` 里写死了 `self.selected_currency = "item"`，覆盖了用户选的通货
**修复**: 删除该行；同时把 CurrencyCard 的 `selected` 信号拆成两个：
- `selected` → 只用于选通货（名字点击触发）
- `coord_clicked` → 只用于设坐标（坐标按钮触发）
**文件**: `ui/config_tab.py`（CurrencyCard 类）、`main.py`（信号连接）

### 2. 连点器停不下来（严重，反复修了多轮）
**根因**: 每次 toggle 都新建 Thread，竞态条件导致停不了
**最终方案**: 重写为"一个线程常驻，只翻 flag"模式。程序启动时创建唯一线程，永远不死。toggle 只设 `_running = True/False`。
**文件**: `clicker_operator.py`（完全重写）

### 3. 连点器 GIL 问题
**现象**: click 循环持有 GIL，keyboard 热键回调抢不到锁
**修复**: 每次 click 后 `time.sleep(0)` 让出 GIL；sleep 用 10ms 小段循环

### 4. 连点器紧急停止
**修复**: 鼠标移到屏幕左上角 (x≤1, y≤1) 自动停止

### 5. 改热键不生效
**原因**: 热键只在启动时注册一次，改设置后没重新注册
**修复**: `_save_all()` 末尾调用 `self.hotkey_mgr.setup()` 重新注册
**文件**: `ui/main_window.py`（`_save_all` 方法）

### 6. _notify 崩溃
**原因**: `_notify = None`，但代码有 8 处调用 `self._notify.show_message()`
**修复**: 用空壳对象 `_NullNotify` 代替 None
**文件**: `ui/main_window.py`（`__init__` 中）

### 7. 不能用 CTRL/ALT/SHIFT 作为热键
**原因**: `KeybindButton._key_to_string()` 和 `GlobalKeyCapture._key_to_string()` 主动跳过修饰键
**修复**: 添加 modifier_map，返回 "ctrl"/"alt"/"shift"
**文件**: `ui/settings_tab.py`（KeybindButton）、`ui/key_tab.py`（GlobalKeyCapture）

---

## 二、当前待测试的功能

用户从上次关机前最后改了这些，**还没测试过**：

1. ✅ Mode 1 通货选择是否正确（终端输出应显示 `通货=chaos` 不是 `通货=item`）
2. ✅ 连点器 toggle 能否正常启动/停止
3. ✅ 连点器按住模式
4. ✅ 改热键后保存，新热键是否生效
5. ✅ CTRL 作为热键是否可用
6. ✅ Mode 2/3 是否正常（逻辑没改，但需确认没被影响）

---

## 三、文件清单

```
poe-craft-tool/
├── models.py           # 数据模型（AffixRule, CraftRules, Mode, CurrencyType）
├── storage.py          # JSON 持久化（坐标、规则、设置）
├── affix_engine.py     # 词缀解析 + 匹配引擎
├── auto_operator.py    # Mode 1/2/3 洗词缀逻辑（有 DEBUG 输出）
├── clicker_operator.py # 连点器（已重写为常驻线程版）
├── key_operator.py     # 按键循环器
├── main.py             # 入口 + HotkeyManager
├── ui/
│   ├── main_window.py  # MainWindow + StatusBar + _NullNotify
│   ├── config_tab.py   # ConfigTab + CurrencyCard（有 coord_clicked 信号）
│   ├── clicker_tab.py  # 连点器 UI
│   ├── key_tab.py      # 按键循环 UI
│   └── settings_tab.py # 设置 UI（KeybindButton 支持修饰键）
├── sounds/default_ding.wav
├── 打包.bat            # PyInstaller 打包脚本
├── 一键启动.bat        # 直接运行
└── build.bat           # 连点器一键启动（旧版，可能不需要）
```

---

## 四、关键架构

### 信号流
```
CurrencyCard.name_label 点击 → selected 信号 → _on_currency_selected() → selected_currency
CurrencyCard.coord_btn 点击 → coord_clicked 信号 → hotkey_mgr.set_coord_target()
item_coord_btn 点击 → hotkey_mgr.set_coord_target("item")
```

### 连点器设计（最新版）
```
__init__: 创建唯一 daemon 线程，立即启动
start_toggle(): 如果运行→设False停止；如果停止→设True启动
_loop(): while True { if not running: sleep(50ms); continue; click(); sleep_chunks() }
```

### 热键注册
```
main.py: HotkeyManager.setup() → keyboard.add_hotkey() × 4
main_window.py: _save_all() → hotkey_mgr.setup() → cleanup() + 重新注册
```

---

## 五、已踩过的坑（别再踩）

1. **连点器不要用 join() + new Thread()**。一个线程常驻，翻 flag 就行
2. **pyautogui.FAILSAFE 只对 moveTo() 有效**，click 不触发。需要手动检查鼠标位置
3. **click() 后必须 time.sleep(0)** 让出 GIL，否则 keyboard 热键回调不触发
4. **信号不能复用**。选通货和设坐标必须用不同信号，否则互相覆盖
5. **lambda 捕获是快照**。在 setup() 时捕获的值不会随 UI 变化。要用闭包函数实时读取
6. **热键改了要重新 register**。keyboard 库的热键是注册时绑定的，不能动态改
7. **_notify = None 会崩溃**。所有 show_message() 调用都需要保护。用空壳对象
8. **不要改 index.html**。首页和全服搜索共享数据库，用户明确禁止修改首页
9. **PyInstaller 需要 --collect-all customtkinter**（虽然本项目用的是 PySide6）
10. **打包后 sounds 目录可能丢失**。--add-data "sounds;sounds" 需要包含

---

## 六、常见调试方法

### 连点器不工作
1. 打开终端看 `[连点器] 启动/停止` 输出
2. 如果没输出 → 热键没触发 → 检查热键注册
3. 如果有"启动"没"停止" → toggle 逻辑有问题
4. 鼠标左上角可以强制停止

### 模式1不右键通货
1. 看终端 `通货=xxx`
2. 如果是 `item` → selected_currency 被覆盖了（不该再出现）
3. 如果通货名正确但没右键 → 坐标不对，重新设

### 改热键不生效
1. 确认保存了（点启动/停止按钮会触发 _save_all）
2. 看终端有没有 `Failed to register hotkey` 报错
3. 如果有冲突 → 两个热键用了同一个键

---

## 七、打包发布

```bash
双击 打包.bat
```
产物在 `dist/poe-craft-tool/`，压缩成 zip 即可分发。

---

**明天开始：先跑 `python main.py`，逐个测试上面第二节的功能。有问题看终端输出。**
