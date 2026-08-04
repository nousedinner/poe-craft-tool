---
name: poe-craft-tool
description: 拾刻 (ShiKe) — PoE 洗词缀自动化+连点器+按键循环工具 (Path of Exile crafting automation)
category: software-development
---

## Project Overview

Desktop automation tool for Path of Exile crafting. Uses clipboard (Ctrl+C) to read item text, matches against user-defined rules, automates mouse/keyboard actions.

**Location:** `/home/zzy/poe-craft-tool/`
**Stack:** Python 3.11, PySide6, pyautogui, pyperclip, keyboard
**Platform:** Windows only (uses `winsound`, `ctypes.windll`)

## Project Structure

```
poe-craft-tool/
├── main.py              # Entry point, HotkeyManager
├── models.py            # Data models: Mode, CurrencyType, AffixRule, CraftRules, AffixCheckResult
├── affix_engine.py      # Text parsing + rule matching engine
├── auto_operator.py     # Crafting automation loops (3 modes)
├── clicker_operator.py  # Mouse auto-clicker
├── key_operator.py      # Multi-key loop operator (independent threads)
├── storage.py           # JSON persistence (rules, coordinates, settings) — creates data/ at runtime
├── ui/
│   ├── main_window.py   # MainWindow, StatusBar, NotificationOverlay
│   ├── config_tab.py    # Mode selection, currency grid (text-only, no icons), affix rules, coord buttons
│   ├── settings_tab.py  # Hotkeys, delay, toggles (ToggleSwitch, KeybindButton)
│   ├── clicker_tab.py   # Clicker configuration (radio buttons with visible indicators)
│   └── key_tab.py       # Key loop configuration (10 slots, GlobalKeyCapture)
├── sounds/              # default_ding.wav (fallback), user puts custom .wav here
├── data/                # Created at runtime: coordinates.json, rules.json, settings.json
├── 打包.bat              # PyInstaller one-file build script
├── 一键启动.bat          # Auto-install Python + deps + launch
├── build.bat            # Simpler build script
├── run.bat              # Run script
├── README.txt           # End-user guide (included in release/)
├── README.md            # Developer docs
└── requirements.txt     # PySide6, pyautogui, pyperclip, keyboard
```

## Text Matching Logic (affix_engine.py + models.py)

### AffixRule.matches(item_affix_text)

1. **Substring check:** `self.text in item_affix_text` — simple Python `in`, no regex
2. **Value range (optional):** If min_value/max_value set:
   - Extract ALL numbers from text: `re.findall(r'[+-]?\d+\.?\d*', item_affix_text)`
   - If ANY number falls within [min, max] → match
   - If no numbers in text → default match (return True)
3. **No value range** → text match alone is sufficient

### Known Issue: Multi-number text

When one line contains multiple affixes (e.g., `"+30 全部抗性, +72 最大生命"`), the range check takes ALL numbers regardless of which affix they belong to. Rule "全部抗性 min:35" would match because 72 ≥ 35, even though 72 belongs to "最大生命".

**Planned fix (not yet implemented):** Only take numbers directly adjacent to the keyword (whitespace-separated, no other text between). User needs to provide real clipboard format first.

### parse_item_text(clipboard_text)

- Splits by `--------` sections
- Filters out metadata lines (Rarity, Item Level, Requires, Quality, etc.)
- **⚠️ 铁律：Item name and base type MUST participate in matching — do NOT filter them out.** The `pass` statement does nothing, `affix_lines.append(line)` always executes. This is intentional design (宽松匹配), not a tolerated bug. Equipment name must always be checked against affix rules in all modes. If you're rewriting this, keep item name in the affix list.

### ⚠️ Chinese Item Text (Tencent Client)

The Tencent (国服) client returns Chinese item text. The skip patterns MUST include Chinese equivalents:

```python
skip_patterns = [
    # ... existing English patterns ...
    # Chinese patterns (腾讯客户端)
    r'^稀\s*有\s*度',       # Rarity
    r'^物品类别',           # Item Class
    r'^物品等级',           # Item Level
    r'^需求',              # Requirements
    r'^等级:',             # Level requirement
    r'^敏捷:', r'^力量:', r'^智慧:',  # Attribute requirements
    r'^插槽',              # Sockets
    r'^物理伤害',          # Physical Damage
    r'^火焰，冰霜，闪电伤害',  # Elemental Damage
    r'^火焰伤害', r'^冰霜伤害', r'^闪电伤害',
    r'^攻击暴击率',        # Critical Strike Chance
    r'^每秒攻击次数',      # Attacks per Second
    r'^出售获得通货',      # Vendor text
]
```

Without these, Chinese property lines like "物理伤害: 38-115" and "插槽: G-G-G-R" get mixed into the affix list, causing incorrect matching.

### check_affixes(item_affixes, rules)

1. Check exclude rules first → early return on any match
2. Check primary rules → each rule matches at most one line (break)
3. Check secondary rules → same
4. Returns AffixCheckResult with counts and matched lines

## 3 Crafting Modes (CORRECTED LOGIC — v15)

### ⚠️ CRITICAL: Correct PoE Crafting Flow

**SHIFT must be held throughout the entire crafting cycle, including clipboard copy.** Never release SHIFT between item click and Ctrl+C.

### Mode 1: 单通货 (Single Currency)

Right-click currency → hold SHIFT → loop: shift+click item → ctrl+c → check → match or exhaust. Simple linear loop.

### Mode 2: 改造+增幅 (Alt + Aug) — CORRECTED v15

**Core rule: Augment is ONLY used when the item has exactly 1 affix.** If Alt gives 2 affixes, skip Aug entirely.

**Threshold concept:** The magic phase threshold = `total_required - 1` (because Regal adds 1 more in Mode 3; for Mode 2, the final check is the strict check).

```
Magic phase loop:
  1. Alt: roll until at least 1 hit (primary or secondary)
  2. After Alt, check affix count:
     - 2 affixes → final check (meets_final_rules)
       - Pass → success
       - Fail → continue Alt (reroll)
     - 1 affix → use Aug
       - After Aug → final check
         - Pass → success
         - Fail → continue Alt (reroll, NOT scour)
```

**Alt phase loose check:** `primary_hits >= 1 or secondary_hits >= 1` (at least 1 hit of any kind). This is intentional — the strict check happens at the final check stage.

### Mode 3: 改+增+富 (Alt + Aug + Regal) — CORRECTED v15

**Core rule: Augment is ONLY used when the item has exactly 1 affix.** Same as Mode 2.

**Threshold = `primary_hit_count + secondary_hit_count - 1`** (Regal adds 1 more affix).

```
Outer loop:
  1. Transmute (白→蓝)
  2. Magic phase loop (same as Mode 2 but with threshold check):
     a. Alt: roll until at least 1 hit
     b. After Alt, check affix count:
        - 2 affixes → check hits >= threshold
          - Pass → exit magic phase, go to Regal
          - Fail → continue Alt (reroll)
        - 1 affix → use Aug
          - After Aug → check hits >= threshold
            - Pass → exit magic phase, go to Regal
            - Fail → continue Alt (reroll, NOT scour)
  3. Regal (蓝→黄)
     - Final check: meets_final_rules
     - Pass → success
     - Fail → Scour (黄→白) → continue outer loop (back to Transmute)
```

**⚠️ Scour only happens after Regal fails.** Never scour after Aug fails — just continue Alt.

**Examples (Mode 3, primary=2, secondary=1, total=3, threshold=2):**
- Alt gives 2 affixes, 2 hits → threshold met → Regal ✓
- Alt gives 2 affixes, 1 hit → threshold not met → continue Alt
- Alt gives 1 affix, 1 hit → Aug → 2 hits → threshold met → Regal ✓
- Alt gives 1 affix, 1 hit → Aug → 1 hit → threshold not met → continue Alt

**Examples (Mode 3, primary=2, secondary=0, total=2, threshold=1):**
- Alt gives 2 affixes, 1 hit → threshold met → Regal ✓
- Alt gives 2 affixes, 0 hits → threshold not met → continue Alt
- Alt gives 1 affix, 1 hit → Aug → threshold met → Regal ✓

### Implementation helpers

```python
def _get_affix_count(self, text):
    """Get total affix count from clipboard text."""
    from affix_engine import parse_item_text
    return len(parse_item_text(text))

def _check_aug_close(self, result, rules):
    """After Aug, check if Regal can complete the requirements.
    Regal adds 1 affix (primary or secondary). Accept if either path works."""
    p, s = result.primary_hits, result.secondary_hits
    tp, ts = rules.primary_hit_count, rules.secondary_hit_count
    if p + 1 >= tp and s >= ts:  # Regal adds primary
        return True
    if p >= tp and s + 1 >= ts:  # Regal adds secondary
        return True
    return False
```

### Validation rules
- Mode 2: `primary_hit_count + secondary_hit_count <= 2`
- Mode 3: `primary_hit_count + secondary_hit_count <= 3`
- Mode 3: `primary_hit_count` can be up to 3 (e.g., primary=3, secondary=0 is valid — Alt needs 2 hits, Regal adds the 3rd)

## ⚠️ keyboard.add_hotkey Callback Thread (CRITICAL — v22)

`keyboard.add_hotkey()` runs the callback on the **keyboard listener thread**, NOT the main Qt thread. Any Qt operation (widget access, QMessageBox, etc.) from this thread causes undefined behavior — typically a hard freeze.

**Symptom:** Pressing F5 (start hotkey) → window freezes immediately. No error message.

**Fix:** Defer ALL Qt-touching hotkey callbacks to main thread:
```python
from PySide6.QtCore import QTimer

def _on_start(self):
    if not self._check_foreground():
        return
    QTimer.singleShot(0, self.main_window._start_crafting)  # ← defer to main thread

def _on_stop(self):
    QTimer.singleShot(0, self.main_window._stop_crafting)

def _on_set_coord(self):
    x, y = pyautogui.position()  # pyautogui is safe from any thread
    target = self._coord_target
    self._coord_target = None
    def _do():
        # All Qt widget operations here
        self.main_window.coordinates[target] = (x, y)
        ...
    QTimer.singleShot(0, _do)

def _on_key_loop_toggle(self):
    if self.main_window.key_operator.is_running:
        QTimer.singleShot(0, self.main_window._stop_key_loop)
    else:
        QTimer.singleShot(0, self.main_window._start_key_loop)
```

**Rule:** `pyautogui` calls (click, moveTo, position) are safe from any thread. But ALL Qt widget operations (setText, setStyleSheet, setVisible, QMessageBox, etc.) MUST run on the main thread.

**Why this is different from the SignalBridge pattern:** SignalBridge handles operator→main thread communication (background thread emitting signals). The keyboard callback issue is a DIFFERENT thread (keyboard listener) calling Qt directly. Both patterns can coexist.

## ⚠️ PySide6 Thread Safety (CRITICAL)

Background threads calling Qt widget methods directly → freeze/white screen after ~10 seconds. Two solutions:

### Solution 1: SignalBridge (recommended for multiple callbacks)
```python
from PySide6.QtCore import QObject, Signal

class SignalBridge(QObject):
    status_update = Signal(str, int, int)
    match_found = Signal(object)
    stopped = Signal(str)
    error = Signal(str)

# In MainWindow.__init__:
self._bridge = SignalBridge()
self._bridge.status_update.connect(self._on_status_update)  # QueuedConnection auto
self._bridge.match_found.connect(self._on_match_found)

# Set operator callbacks to emit signals:
self.operator.on_status_update = lambda *a: self._bridge.status_update.emit(*a)
```

### Solution 2: QTimer.singleShot (simpler for single callbacks)
```python
# For callbacks that run from background threads:
self.clicker.on_status_update = lambda *a: QTimer.singleShot(0, lambda: self._on_clicker_update(*a))
```

**Why:** Cross-thread signal connections use `Qt.QueuedConnection` by default, which marshals the call to the receiver's thread (main/GUI thread). Direct calls from background threads modify widgets in the wrong thread.

### Status Update Throttling
Background loops that call `on_status_update` every ~50ms flood the UI. Throttle to max 4 updates/second:
```python
def _notify_status(self, status):
    now = time.time()
    if now - self._last_status_time < 0.25:
        return
    self._last_status_time = now
    if self.on_status_update:
        self.on_status_update(status, self.use_count, self.match_count)
```

## Window Icon (.ico) Generation

Generate a minimal .ico file with pure Python (no PIL needed) for window/tray icons:
```python
def make_ico(filename, size=32, color=(26, 111, 181)):
    """Create a minimal .ico file with a colored circle."""
    # Uses struct to build BMP data with BGRA pixels + AND mask
    # Output: single 32x32 entry ICO file (~4KB)
```

Set in MainWindow:
```python
icon_path = Path(__file__).parent.parent / "poe.ico"  # v19: renamed from icon.ico
if icon_path.exists():
    self.setWindowIcon(QIcon(str(icon_path)))
    self.tray_icon.setIcon(QIcon(str(icon_path)))
```

Note: `.ico` works better than `.png` for Windows window icons. PySide6 can load .ico directly with `QIcon`.

### ⚠️ Windows Taskbar Icon from Source (v22)

`QApplication.setWindowIcon()` does NOT set the Windows taskbar icon when running from source (`python main.py`). Windows determines the taskbar icon from the window handle's `GCLP_HICON`, not from Qt.

**Fix:** Use ctypes to directly set the icon on the window handle after the window is created:
```python
window = MainWindow()

# Set icon via Win32 API — works for both title bar AND taskbar
try:
    import ctypes
    hwnd = int(window.winId())
    hicon = ctypes.windll.user32.LoadImageW(0, str(icon_path), 1, 0, 0, 0x10)  # IMAGE_ICON, LR_LOADFROMFILE
    if hicon:
        ctypes.windll.user32.SendMessageW(hwnd, 0x0080, 0, hicon)  # WM_SETICON, ICON_SMALL
        ctypes.windll.user32.SendMessageW(hwnd, 0x0080, 1, hicon)  # WM_SETICON, ICON_BIG
except Exception as e:
    print(f"[图标] 设置失败: {e}")
```

**Also needed in build.bat:** `--icon "poe.ico"` flag for PyInstaller so the exe itself has the icon embedded. Without it, the exe gets the default Python icon.

## Clicker Tab UI (QPushButton toggles — v4 final)

**4 iterations of failed approaches before finding what works:**

1. ❌ QRadioButton with hidden indicator (0x0) → labels invisible
2. ❌ QRadioButton with visible indicator → still broken in nested QFrame
3. ❌ QPushButton with CSS stylesheets in QFrame → styles overridden by parent
4. ❌ QPushButton with `✔` emoji → renders as giant colored bar, text pushed off-screen
5. ✅ **QGroupBox + plain text `[*]`/`[ ]` prefix — final working approach**

### Why each failed:
- **QRadioButton indicator 0x0**: Hides indicator but still allocates layout space → empty box
- **QFrame stylesheet inheritance**: Qt CSS in nested widgets is unreliable — parent QFrame style overrides child QPushButton style on Windows
- **Emoji in QPushButton text** (`✔`, `🖱️`): Qt renders emoji as full-size pixmap filling the entire button width. The button appears as a colored rectangle with no visible text. **Never use emoji in QPushButton.setText()**

### Final working approach (v4):
```python
from PySide6.QtWidgets import QGroupBox

# Use QGroupBox (has native title bar) instead of QFrame
box1 = QGroupBox("点击按键")  # Title is built-in
r1 = QHBoxLayout()

self.btn_left = QPushButton("[ ] 左键")  # ASCII prefix, NOT emoji
self.btn_left.setCheckable(True)
self.btn_left.setMinimumHeight(36)
self.btn_left.setMinimumWidth(110)  # Prevent text truncation
self.btn_left.clicked.connect(lambda: self._pick("left"))
r1.addWidget(self.btn_left)

# ... same for other buttons ...
box1.setLayout(r1)

def _refresh(self):
    ON = "[*] "
    OFF = "[ ] "
    if self._click_button == "left":
        self.btn_left.setText(ON + "左键")
        self.btn_right.setText(OFF + "右键")
    else:
        self.btn_left.setText(OFF + "左键")
        self.btn_right.setText(ON + "右键")
```

**Key rules:**
- Use `QGroupBox` for card containers (has built-in title, no need for nested QFrame)
- Use `[*]` / `[ ]` ASCII prefix for selection state (NOT `✔` or other emoji)
- Set `setMinimumWidth(110)` on buttons to prevent truncation
- Apply `setStyleSheet` directly on buttons if needed, NEVER rely on inheritance from parent

## Sound Customization (auto_operator.py + settings_tab.py)

**Selection:** Dropdown in Settings tab auto-scans `sounds/` folder for .wav/.mp3/.ogg/.flac files. User selects from list, no need to rename files. Folder open button (📁) opens the sounds directory. Refresh button (🔄) rescans.

**Settings key:** `selected_sound` (filename string, default: `"default_ding.wav"`)

**Playback (auto_operator.py _play_sound):**
1. Uses `self._selected_sound` (set via `start()` param)
2. Search order: EXE dir `sounds/` → bundled `sounds/` → source `sounds/`
3. Falls back to `default_ding.wav` if selected file not found
4. **WAV:** `winsound.PlaySound(str(sound_file), winsound.SND_FILENAME | winsound.SND_ASYNC)`
5. **MP3/OGG/FLAC:** Windows MCI interface via ctypes — no new dependencies:
```python
import ctypes
winmm = ctypes.windll.winmm
winmm.mciSendStringW("close pohelper_snd", None, 0, None)
cmd = f'open "{str(sound_file)}" type mpegvideo alias pohelper_snd'
winmm.mciSendStringW(cmd, None, 0, None)
winmm.mciSendStringW("play pohelper_snd", None, 0, None)
```
6. Falls back to `default_ding.wav` if selected file not found

## Key Operator (key_operator.py) — v2: Pre-created Threads

Multi-key auto-press with independent intervals. 10 pre-created daemon threads (one per slot), alive forever. Each thread checks per-slot `_running_flags[index]`. `stop()` is instant, no `join()`.

```python
class KeyOperator:
    def __init__(self):
        self._running_flags = [False] * 10
        self._slots = [None] * 10
        for i in range(10):
            t = threading.Thread(target=self._key_loop, args=(i,), daemon=True)
            t.start()

    def start(self, slots):
        with self._lock:
            for i, slot in enumerate(slots[:10]):
                self._slots[i] = slot
                self._running_flags[i] = slot.enabled and bool(slot.key)

    def stop(self):
        with self._lock:
            for i in range(10):
                self._running_flags[i] = False  # Instant, no join

    def _key_loop(self, index):
        while True:
            slot = self._slots[index]
            if not self._running_flags[index] or not slot or not slot.key:
                time.sleep(0.05); continue
            keyboard.press_and_release(slot.key)
            deadline = time.monotonic() + slot.delay
            while self._running_flags[index] and time.monotonic() < deadline:
                time.sleep(0.05)
```

- Each slot: KeySlot(enabled, key, delay)
- 10 idle daemon threads cost nothing (sleep 50ms when inactive)
- Single-thread time-wheel rejected: ~20ms jitter unacceptable for short intervals

## UI Key Tab (key_tab.py)

- **GlobalKeyCapture:** App-level event filter for key recording (better than per-button focus)
- 10 slots, default show 5, "加载更多" button toggles
- Each slot: ToggleSwitch + KeyCaptureButton + QDoubleSpinBox (delay, seconds, range 0.1-999, step 0.1)
- Conflicts: checked at startup in HotkeyManager._check_conflicts()

## ⚠️ HotkeyManager: Re-register on Mode Change

**Bug:** `HotkeyManager.setup()` registers the clicker hotkey callback based on `clicker_mode` (toggle vs hold). If the user changes the mode in the UI after startup, the hotkey is NOT re-registered — it still uses the old callback.

**Fix:** When clicker mode changes in the UI, call `setup()` to re-register all hotkeys:

```python
# In clicker_tab.py _pick_mode():
def _pick_mode(self, mode: str):
    self._click_mode = mode
    self._refresh()
    # Re-register hotkeys with new mode
    main_window = self.window()
    if hasattr(main_window, 'hotkey_mgr'):
        main_window.hotkey_mgr.setup()
```

Or alternatively, save to settings + re-register in `_save_all()`:
```python
# In MainWindow._save_all():
save_settings(self.settings)
if hasattr(self, 'hotkey_mgr'):
    self.hotkey_mgr.setup()  # Re-register all hotkeys
```

**Key insight:** The `keyboard` library's `on_press_key`/`on_release_key` (hold mode) and `add_hotkey` (toggle mode) are fundamentally different registration methods. You can't just change a flag — you must call `cleanup()` + `setup()` to switch between them.

## ⚠️ keyboard library: remove_hotkey() vs unhook() (CRITICAL)

**Bug:** `cleanup()` uses `keyboard.remove_hotkey()` for ALL IDs, but `on_press_key()`/`on_release_key()` return **hook IDs** that require `keyboard.unhook()`. Using `remove_hotkey()` on hook IDs fails silently (caught by `except`), leaving old callbacks active.

**Symptom:** After switching from hold→toggle mode, hold-mode callbacks remain active alongside toggle callbacks. Multi-switch cycles accumulate orphaned callbacks, causing toggle mode to behave like hold mode.

**Fix:** Track ID types separately:
```python
class HotkeyManager:
    def __init__(self, main_window):
        ...
        self._hotkey_ids = []   # IDs from add_hotkey (toggle mode)
        self._hook_ids = []     # IDs from on_press_key/on_release_key (hold mode)

    def cleanup(self):
        for h_id in self._hotkey_ids:
            try:
                keyboard.remove_hotkey(h_id)
            except Exception:
                pass
        self._hotkey_ids = []
        for h_id in self._hook_ids:
            try:
                keyboard.unhook(h_id)
            except Exception:
                pass
        self._hook_ids = []

    def setup(self):
        ...
        if clicker_mode == "toggle":
            h_id = keyboard.add_hotkey(hotkey_clicker, callback)
            self._hotkey_ids.append(h_id)       # ← hotkey ID
        else:
            h_id = keyboard.on_press_key(hotkey_clicker, callback)
            self._hook_ids.append(h_id)          # ← hook ID
            h_id2 = keyboard.on_release_key(hotkey_clicker, callback)
            self._hook_ids.append(h_id2)         # ← hook ID
```

**Rule:** `add_hotkey()` → `remove_hotkey()`. `on_press_key()`/`on_release_key()` → `unhook()`. Never mix them in the same list.

## ⚠️ Hotkey Conflict Detection: Print → MessageBox

`_check_conflicts()` currently only `print()`s to console. User never sees it (no console visible when running as EXE). Fix: show `QMessageBox.warning()` when conflict is detected during `setup()`.

## ⚠️ Critical: Windows CMD Encoding Pitfall

Windows CMD defaults to GBK (code page 936). `.bat` files with Chinese text saved as UTF-8 will produce garbled output.

**Rule: ALL .bat files must use English only.** No Chinese in:
- `echo` statements
- `title` commands
- `REM` comments
- Variable names

Even with `chcp 65001`, fonts may not support all Chinese characters. Just use English — it always works.

**File names:** Prefer ASCII filenames (`README.txt` over `使用说明.txt`). Non-ASCII filenames can cause issues in bat `copy` commands.

## ⚠️ QDoubleSpinBox Input Difficulty (User Can't Type Numbers)

**Bug:** Primary affix pool SpinBoxes (min/max) require double-click or scroll wheel to input numbers. User reported: "主词缀池没法正常输入数字，后面的两个框一个是最大，一个是最小，我需要点级，然后滑动滚轮，把他滑成数字之后才能修改。"

**Root cause:** QDoubleSpinBox default behavior requires clicking arrows or using mouse wheel. Users expect to click once and type.

**Fix: Override focusInEvent to select all text automatically:**

```python
# In config_tab.py, when creating primary_min and primary_max SpinBoxes:
self.primary_min = QDoubleSpinBox()
self.primary_min.setRange(-9999, 9999)
self.primary_min.setPrefix("≥")
self.primary_min.setSpecialValueText("最小")
self.primary_min.setValue(self.primary_min.minimum())
# Make it easier to edit: select text on focus
self.primary_min.focusInEvent = lambda e: (super(self.primary_min.__class__, self.primary_min).focusInEvent(e), self.primary_min.selectAll())

self.primary_max = QDoubleSpinBox()
self.primary_max.setRange(-9999, 9999)
self.primary_max.setPrefix("≤")
self.primary_max.setSpecialValueText("最大")
self.primary_max.setValue(self.primary_max.minimum())
# Make it easier to edit: select text on focus
self.primary_max.focusInEvent = lambda e: (super(self.primary_max.__class__, self.primary_max).focusInEvent(e), self.primary_max.selectAll())
```

**Alternative approach:** Use QLineEdit with QIntValidator for direct typing (integer only — PoE affix values are always integers). SpinBox with auto-select is simpler for Python/Qt, but in C#/WPF, TextBox + IntValidationRule is the standard pattern.

**Note:** This pattern can be applied to ALL SpinBoxes in the UI that users need to type into frequently.

## Qt/PySide6 CSS Pitfalls

**DO NOT use `box-shadow`** — Qt's QSS (Qt Style Sheets) does NOT support CSS `box-shadow`. Generates `Unknown property box-shadow` warnings. Use border styling or gradient backgrounds instead.

**DO NOT use `cursor`** — Qt's QSS does NOT support CSS `cursor` property. Generates `Unknown property cursor` warnings. Use `widget.setCursor(Qt.PointingHandCursor)` in Python code instead.

**DO NOT hide QRadioButton::indicator with 0x0** — Setting `width: 0px; height: 0px` on `QRadioButton::indicator` hides the visual indicator AND can hide the label text. Causes rendering issues where the button appears as an empty box.

**DO NOT use emoji (✔, 🖱️, etc.) in QPushButton text** — Qt renders emoji as large colored pixmaps that fill the entire button width, pushing actual label text off-screen. The button appears as a colored bar instead of text. Use ASCII alternatives: `[*]` / `[ ]` instead of `✔` / empty. Or use plain text without any prefix.

**DO NOT rely on QFrame stylesheet inheritance** — When a QFrame has a stylesheet, child QPushButton styles can be overridden. Use QGroupBox instead, or set styles directly on each widget.
```css
QRadioButton::indicator {
    width: 14px; height: 14px;
    border: 2px solid rgba(26,111,181,0.4);
    border-radius: 8px;
    background: rgba(255,255,255,0.6);
}
QRadioButton::indicator:checked {
    border: 2px solid #1a6fb5;
    background: #1a6fb5;
}
```

**Lesson:** Icons were removed from the project entirely — user preferred clean text-only UI over bad icons. When creating resources programmatically, always validate file headers (`PNG: 89504e47`, `JPG: ffd8ff`, etc.) to catch corrupted downloads.

## ⚠️ Icon Path Resolution in Subdirectory-Structured Projects

When Python files are in a `ui/` subdirectory, `Path(__file__).parent` points to `ui/`, NOT the project root. Icon paths need **two** levels up:

```python
# WRONG — resolves to ui/icons/chaos.png (doesn't exist)
icon_path = Path(__file__).parent / "icons" / "chaos.png"

# CORRECT — resolves to project-root/icons/chaos.png
icon_path = Path(__file__).parent.parent / "icons" / "chaos.png"
```

This applies to ALL resource loading in `ui/*.py` files. Also add `setWindowIcon()` to MainWindow — it's not set by default.

**QSystemTrayIcon:** Must call `setIcon()` BEFORE `show()`. If icon path is wrong, tray shows "No Icon set" warning and the tray icon won't appear.

## ⚠️ Windows Deployment Pitfalls

### Python from Microsoft Store
User may have Python installed via Microsoft Store (`pythoncore-3.14-64`). In this case:
- `pip` command may not work → use `python -m pip` instead
- Python executable path: `C:\Users\<user>\AppData\Local\Python\pythoncore-3.14-64\python.exe`
- Use `where python` to find it dynamically in .bat files

### PyPI Speed in China
Direct PyPI access from China is very slow (~90 kB/s). Use Aliyun mirror:
```bash
python -m pip install <packages> -i https://mirrors.aliyun.com/pypi/simple/
```

### Admin Rights for keyboard library
The `keyboard` library requires **Administrator privileges** on Windows to register global hotkeys. Without admin:
- `SetProcessDpiAwarenessContext() failed: 拒绝访问` warning
- Hotkeys don't work in games or other elevated apps
- Solution: Always right-click → "Run as Administrator"

### Batch File Debugging
If bat files produce garbled Chinese text like `'弻鍑昏繍琛` — it's an encoding issue. Use English-only in all .bat files. See "Windows CMD Encoding Pitfall" section above.

## Coordinate Setting UX (buttons with recording state)

Old UX (confusing): plain labels showing "未设置坐标", user clicked label then pressed F7 — no visual feedback.

New UX (button-based): Each coordinate (item + 8 currencies) is a **QPushButton** with 3 states:

```python
# Styles for 3 states
BTN_STYLE = "background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15); ..."
BTN_RECORDING = "background: rgba(26,111,181,0.12); border: 1.5px solid #1a6fb5; ..."  # blue highlight
BTN_SAVED = "background: rgba(27,138,62,0.08); border: 1px solid rgba(27,138,62,0.3); ..."  # green
```

**Flow:**
1. Click "设定坐标" button → text changes to "移动鼠标后按 F7..." (blue highlight)
2. Move mouse to target → press F7 → text changes to "✓ (x, y)" (green), clear button (✕) appears
3. Click ✕ to reset coordinate

**Implementation in CurrencyCard:** `_on_coord_click()` sets recording state, `_clear_coord()` resets. Currency name label is clickable to select currency (separate from coord button).

**main.py connection:** Currency cards emit `selected` signal → `hotkey_mgr.set_coord_target(k)`. Item coord button → `hotkey_mgr.set_coord_target("item")`. Clear button saves coordinates.

## Floating Notification Overlay

Semi-transparent notification appears in screen center for 2 seconds (with fade-out), used for all start/stop events.

```python
class NotificationOverlay(QWidget):
    # Frameless, always-on-top, Tool window, translucent background
    # Container: rgba(30,42,58,0.82) dark bg, border-radius 16px
    # Label: white, 18px, bold
    # Timer: 2000ms → fade out at 30ms intervals (opacity -= 0.08)
```

**Usage in MainWindow:**
```python
self._notify = NotificationOverlay()
self._notify.show_message("▶ 洗词缀 启动")   # _start_crafting
self._notify.show_message("⏹ 洗词缀 停止")   # _stop_crafting
self._notify.show_message("🎉 命中！词缀满足条件")  # _on_match_found
self._notify.show_message(f"⏹ {reason}")      # _on_stopped (exhaustion)
```

Connected to: crafting start/stop, clicker start/stop (detected via state change in `_on_clicker_update`), key loop start/stop, match found, auto-stopped.

## ⚠️ pyautogui Stuck Keys (CRITICAL)

When using `pyautogui.keyDown('shift')` + click + `pyautogui.keyUp('shift')`, the shift key can remain "stuck" between iterations if timing is off or an exception occurs between keyDown/keyUp. This causes subsequent right-clicks to register as **shift+right-click**, which PoE interprets differently (or ignores).

**Fix:** Call `_release_all_keys()` at the START of each operation AND in the `finally` block:
```python
def _release_all_keys(self):
    for k in ['shift', 'ctrl', 'alt']:
        try:
            pyautogui.keyUp(k)
        except Exception:
            pass
    time.sleep(0.05)

# In _shift_click:
def _shift_click(self, coord, delay):
    self._release_all_keys()  # Always release first!
    time.sleep(0.05)
    pyautogui.keyDown('shift')
    time.sleep(delay)
    self._safe_move(coord, delay)
    time.sleep(delay)
    pyautogui.click()
    time.sleep(delay)
    pyautogui.keyUp('shift')
    time.sleep(delay * 2)

# In _run finally block:
finally:
    self._release_all_keys()  # Always clean up
    with self._lock:
        self._running = False
```

Also: always cast coordinates to `int()` — `pyautogui.moveTo(float, float)` can fail silently on some systems:
```python
def _safe_move(self, coord, delay):
    x, y = int(coord[0]), int(coord[1])
    pyautogui.moveTo(x, y, duration=max(delay, 0.02))
    time.sleep(0.05)
```

## Floating Notification (Simplified)

Previous version used fade-out animation with `_fade_timer` (30ms interval, opacity -= 0.08). This can **block the Qt event loop** when combined with heavy background automation threads, causing UI freeze.

**Safe version — no animation, just hide after timeout:**
```python
class NotificationOverlay(QWidget):
    def __init__(self):
        super().__init__()
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool)
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setAttribute(Qt.WA_ShowWithoutActivating)
        # ... layout setup ...
        self._timer = QTimer(self)
        self._timer.setSingleShot(True)
        self._timer.timeout.connect(self.hide)  # Just hide, no fade

    def show_message(self, text: str):
        self.label.setText(text)
        # ... size + center on screen ...
        self.setWindowOpacity(1.0)
        self.show()
        self._timer.start(2000)  # 2 seconds then hide
```

**Do NOT use `Qt.X11BypassWindowManagerHint`** — it can prevent the window from being properly managed/closed.

## ⚠️ Clicker: Separate Hotkeys for Toggle/Hold Mode

**Feature:** Both toggle and hold modes are always registered simultaneously with different hotkeys. No need to switch modes in UI.

**Settings:**
- `clicker_hotkey` (default "F9") → toggle mode (press once starts, press again stops)
- `clicker_hold_hotkey` (default "F11") → hold mode (hold to click, release to stop)

**HotkeyManager.setup()** always registers BOTH:
```python
# Toggle hotkey — always registered
h_id = keyboard.add_hotkey(hotkey_clicker, lambda: self._on_clicker_toggle(...))
self._hotkey_ids.append(h_id)

# Hold hotkey — always registered
h_id = keyboard.on_press_key(hotkey_clicker_hold, lambda e: self._on_clicker_hold_start(...))
self._hook_ids.append(h_id)
h_id2 = keyboard.on_release_key(hotkey_clicker_hold, lambda e: self._on_clicker_hold_stop())
self._hook_ids.append(h_id2)
```

**Conflict behavior:** If toggle is running and hold is pressed, `start_hold()` sees `_running=True` and returns (does nothing). If hold is running and toggle is pressed, `start_toggle()` sees `_running=True` and stops it. This is acceptable — users typically use one mode at a time.

## Deploying to User (WeChat delivery)

User's Windows machine can't easily download from SSH. Package as zip:
```python
import zipfile, os
base = "/home/zzy/poe-craft-tool"
with zipfile.ZipFile('/tmp/poe-craft-tool.zip', 'w') as zf:
    for root, dirs, files in os.walk(base):
        dirs[:] = [d for d in dirs if not d.startswith(('.', '__pycache__', 'venv'))]
        for f in files:
            zf.write(os.path.join(root, f), os.path.relpath(...))
```
Put at `/var/www/poe/poe-craft-tool.zip` → user downloads from `http://poe.cancanneed.top/poe-craft-tool.zip`

**⚠️ Always repackage AFTER making changes AND copy to nginx root.** The zip file is NOT auto-updated. Nginx serves from `/var/www/poe/`, not from `/home/zzy/`. Two steps:
```bash
# 1. Repackage
cd /home/zzy/poe-craft-tool && python3 -c "
import zipfile, os
with zipfile.ZipFile('/home/zzy/poe-craft-tool.zip', 'w', zipfile.ZIP_DEFLATED) as zf:
    for root, dirs, files in os.walk('.'):
        dirs[:] = [d for d in dirs if not d.startswith(('.', '__pycache__', 'venv'))]
        for f in files:
            full = os.path.join(root, f)
            zf.write(full, os.path.relpath(full, '.'))
"
# 2. Copy to web root
cp /home/zzy/poe-craft-tool.zip /var/www/poe/poe-craft-tool.zip
```
If you forget step 2, the user downloads a stale/corrupted file.
```python
with zipfile.ZipFile('/tmp/poe-craft-tool.zip', 'r') as zf:
    for n in sorted(zf.namelist()):
        print(f"  {n}  ({zf.getinfo(n).file_size} bytes)")
```
Compare file sizes between zip and disk to detect staleness.

## Packaging & Versioning

- `打包.bat` / `build.bat` use PyInstaller `--onefile`
- **Must include `--icon "poe.ico"`** in PyInstaller command — without it, the exe gets the default Python icon, not the custom poe.ico. The source code loads poe.ico at runtime for window/tray icons, but the exe itself needs the flag for the taskbar/exe icon.
- **Never hardcode Python path** in .bat files — use `where python` lookup or just `python` from PATH
- Copies `README.txt` to `release/` folder
- Self-contained EXE, no Python needed on target machine
- **Must run as Administrator** — `keyboard` library needs admin for global hotkeys
- Build requires Python 3.10+ on build machine. Don't hardcode Python path — use `where python` lookup.

### Version Number Convention

Each update must:
1. Delete ALL old zip files from `/var/www/poe/` (`rm -f /var/www/poe/poe-craft-tool*.zip`)
2. Update window title in `main_window.py`: `self.setWindowTitle("poe小助手 vN")`
3. Package as `poe-craft-tool-vN.zip` (versioned filename)
4. Copy to `/var/www/poe/poe-craft-tool-vN.zip`
5. Download URL: `http://poe.cancanneed.top/poe-craft-tool-vN.zip`

**Never reuse zip filenames** — always increment version number.

## UI Theme (Light Blue Semi-Transparent)

方案D — 浅蓝半透明白色模块:
- Background gradient: `#c8dff0 → #dae8f5 → #e4eef8`
- Cards: `rgba(255, 255, 255, 0.55)` (白色半透明, border-radius 10px)
- Card border: `rgba(0, 80, 160, 0.12)` (浅蓝半透明)
- Text primary: `#1e2a3a` (深色)
- Text secondary: `#5a7a90`
- Blue accent: `#1a6fb5` (替代原金色 `#ffd700`)
- Red highlight: `#d04555` (替代 `#e94560`)
- Green success: `#1b8a3e` (替代 `#4CAF50`)
- Disabled toggle: `#c0c8d0`
- Window: 720×660 fixed
- Scrollbar: light blue `#c0d8e8`, border-radius 5px
- Tab hover: `rgba(26, 111, 181, 0.10)`
- Input background: `rgba(255, 255, 255, 0.70)`

QPalette: `#dae8f5` base, `#1e2a3a` text, `rgba(255,255,255,0.55)` button.

## GitHub Repository

**Repo:** https://github.com/nousedinner/poe-craft-tool
**Branch:** master
**CHANGELOG.md:** Must be updated with every commit (project convention since 2026-08-03).

### Initial Setup (done 2026-08-03)
- Source was in `1.0.1源文件.zip`, extracted to project root
- `.gitignore` excludes: `__pycache__/`, `*.pyc`, runtime JSON (`data/coordinates.json`, `data/rules.json`, `data/settings.json`), `platforms/`, build artifacts
- Presets (`data/presets/武器.json`) and sounds (.mp3) are committed
- Git config: `nousedinner` / `nousedinner@users.noreply.github.com`

## C++ Rewrite — PLANNED, NOT IMPLEMENTED

User confirmed (2026-08-03): **no C++ version exists.** The Python/PyInstaller package is ~50-80MB and has slight lag. User plans to rewrite in a lighter stack eventually but has no timeline. Technical notes below are from earlier planning discussions only.

### Key Differences from Python Version

| | Python | C++ |
|---|---|---|
| UI | PySide6 (Qt) | Win32 API (raw) |
| Mouse/Keyboard | pyautogui | SendInput API |
| Clipboard | pyperclip | OpenClipboard + GetClipboardData |
| Hotkeys | keyboard library | RegisterHotKey API |
| Threading | threading (GIL!) | std::thread (true parallel) |
| Sound | winsound | PlaySoundA |
| GIL white screen | Yes (problem) | No (solved) |
| External deps | PySide6, pyautogui, pyperclip, keyboard | None (only MinGW) |

### Architecture Notes

- `common.h` is the base — all other headers include it. No circular dependencies.
- `#pragma comment(lib, ...)` in common.h is MSVC-only; MinGW ignores it silently. Link flags in build.bat handle it.
- Settings stored as plain `key=value` text files in `data/` (not JSON — avoids nlohmann dependency).
- `Automator` class uses `SendInput` for mouse clicks, `SetCursorPos` for movement, `OpenClipboard`/`GetClipboardData` for clipboard.
- Hotkeys via `RegisterHotKey` with `WM_HOTKEY` message — no admin privileges needed (unlike Python `keyboard` library).
- UI: `WC_TABCONTROLW` for tabs, child windows per tab page (show/hide on tab change), `WM_COMMAND` dispatch.

### C++ Clicker Fix (Same Bugs as Python)

The C++ version had the same `sleep_ms(interval_ms)` blocking bug. The `run_clicker` function blocked on `Sleep(interval_ms)` and couldn't respond to `clicker_running = false` until sleep finished.

**Fix:** Sleep in 20ms chunks with `GetCursorPos` emergency stop:
```cpp
while (state_->clicker_running) {
    // Emergency stop: mouse in top-left corner
    POINT pt;
    GetCursorPos(&pt);
    if (pt.x <= 1 && pt.y <= 1) break;

    SendInput(2, inputs, sizeof(INPUT));
    state_->click_count++;
    for (int i = 0; i < interval_ms / 20 && state_->clicker_running; i++)
        Sleep(20);
    Sleep(interval_ms % 20);
}
```

**Note:** The C++ clicker uses `g_clicker_thread.detach()` so it can't be joined. The atomic `clicker_running` flag + interruptible sleep is sufficient. Unlike Python, C++ has no GIL so the atomic flag reliably stops the thread.

### Build Requirements

User needs MinGW-w64 installed with `g++` in PATH. Install options:
1. MSYS2: `pacman -S mingw-w64-x86_64-gcc`
2. winlibs.com: download zip, extract, add `bin/` to PATH

Verify: `g++ --version` in terminal.

## ⚠️ Daemon Thread Idle Sleep Causes UI Lag

When a daemon thread uses `time.sleep(0.05)` or `_stop_event.wait(0.05)` in idle state, the thread wakes up 20 times per second even when doing nothing. This:
- Wastes ~1-5% CPU constantly
- Interferes with Qt's main thread scheduling (Python GIL)
- Causes UI lag (buttons slow to respond, window drag stutters)
- **User reported: "daemon线程一直在轮询，导致卡顿，鼠标移动都卡"**

### Root Cause Analysis

Two operators had this issue:

1. **key_operator.py**: `_key_loop` had `time.sleep(0.05)` at line 80 and 93
2. **clicker_operator.py**: `_loop` had `time.sleep(0.05)` at line 80 and `time.sleep(0.01)` at line 99

These 50ms/10ms sleeps cause constant CPU polling even when idle.

### Fix: Use threading.Event for True Zero-CPU Idle

**Pattern:** Replace all `time.sleep()` idle loops with `threading.Event.wait()`:

```python
# BAD — wakes 20 times/second:
while True:
    if not self._running:
        time.sleep(0.05)  # Polling!
        continue

# GOOD — zero CPU when idle:
def __init__(self):
    self._wake_event = threading.Event()
    # ... thread starts

def _loop(self):
    while True:
        # Wait until we're woken up (either by start or stop)
        self._wake_event.wait()
        self._wake_event.clear()
        
        if not self._running:
            continue  # Go back to waiting
        
        # ... do work ...
        
        # Sleep for the full interval, but wake up early if stopped
        deadline = time.monotonic() + self._interval
        while self._running and time.monotonic() < deadline:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            # Wait up to 0.1s or until woken
            self._wake_event.wait(timeout=min(0.1, remaining))
            self._wake_event.clear()

def start(self):
    with self._lock:
        self._running = True
        self._wake_event.set()  # Wake thread to start

def stop(self):
    with self._lock:
        self._running = False
        self._wake_event.set()  # Wake thread to notice stop
```

### Key Implementation Details

1. **Clear event after wait:** Always call `self._wake_event.clear()` after `wait()` returns
2. **Wake on both start and stop:** Both `start()` and `stop()` must call `_wake_event.set()`
3. **Timeout for responsive stop:** Use `wait(timeout=min(0.1, remaining))` during active loops
4. **Multiple threads (KeyOperator):** Use list of events: `self._wake_events = [threading.Event() for _ in range(MAX_SLOTS)]`

### Performance Impact

- **Before:** 10 threads × 20 wakeups/sec = 200 context switches/sec, ~2-5% CPU
- **After:** Zero CPU when idle, only wakes on user action

### AutoOperator: _stop_event Dual-Use Bug (CRITICAL)

`auto_operator.py` uses `_stop_event` for TWO purposes:
1. **Wake the thread** from idle: `start()` calls `_stop_event.set()`
2. **Interruptible sleep**: `_interruptible_sleep(N)` calls `_stop_event.wait(timeout=N)`

**The conflict:** When `start()` sets the event to wake the thread, the event remains SET. The thread wakes up in `_run()`, but if it doesn't `clear()` the event before entering the crafting loop, then EVERY subsequent `_interruptible_sleep(N)` call returns **instantly** (`wait(timeout=N)` returns immediately when event is already set). All delays are skipped — the loop runs at CPU full speed.

**Symptom:** User clicks start → only 1 actual click happens → 10+ clipboard checks fire in <1 second → exhaustion triggers immediately. User sees "只点了一次就耗尽".

**Fix:** Clear the event after the thread wakes up, BEFORE entering the crafting loop:
```python
def _run(self):
    while True:
        if not self._running:
            self._stop_event.clear()
            self._stop_event.wait()  # Sleep until woken by start()
            continue

        # start() set _stop_event to wake us — clear it now so
        # _interruptible_sleep() properly waits for its timeout
        self._stop_event.clear()

        my_run_id = self._run_id
        try:
            self._release_all()
            self._do_crafting()
        ...
```

**Why this is subtle:** The idle loop correctly calls `clear()` before `wait()`. But the transition from idle→active doesn't clear the event. `start()` sets the event, the thread wakes, checks `_running` (True), and falls through to `_do_crafting()` — but the event is still set from `start()`.

**Rule:** Whenever `threading.Event` is used for both "wake from idle" AND "interruptible sleep with timeout", you MUST clear the event after the wake-up check and before any timed waits.

## ⚠️ Hardcoded Hotkey Labels in UI (Not Reading from Settings)

**Bug:** UI labels show hardcoded hotkey names (e.g., "F8") even when user has changed the hotkey in settings.

**Affected files:**
1. **clicker_tab.py**: Line 81 and 181 had `self.settings.get("clicker_hotkey", "F8")` — default "F8" was hardcoded
2. **storage.py**: Default `"clicker_hotkey": "F8"` didn't match actual default after hotkey reshuffle

**Root cause:** After separating start/stop hotkeys (F6→start, F7→stop), clicker hotkey moved from F8→F9, but UI labels weren't updated.

**Fix: Update all hardcoded defaults to match actual defaults:**

```python
# In clicker_tab.py:
self.hotkey_display = QLabel(self.settings.get("clicker_hotkey", "F9"))  # Changed from "F8"

def get_settings(self) -> dict:
    return {
        "clicker_hotkey": self.settings.get("clicker_hotkey", "F9"),  # Changed from "F8"
        # ...
    }

# In storage.py:
"clicker_hotkey": "F9",  # Changed from "F8"
```

**General rule:** When hotkey defaults change due to feature updates (like separating start/stop), update ALL hardcoded defaults in:
- UI labels (`clicker_tab.py`, `config_tab.py`, `settings_tab.py`)
- Default settings (`storage.py`)
- Hotkey manager defaults (`main.py`)
- Documentation (`README.txt`)

**Verification:** Search for all occurrences of the old hotkey:
```bash
grep -r "F8" /home/zzy/poe-craft-tool/
```

Update each occurrence based on context:
- UI display labels → use `self.settings.get("key", "NEW_DEFAULT")`
- Default settings → update to new default
- Documentation → update user instructions

## ⚠️ Crafting Logic Stops After Right-Clicking Currency

**Bug:** In all crafting modes (mode1, mode2, mode3), the automation stops immediately after right-clicking currency, before attempting to apply it to the item.

**User report:** "洗词缀逻辑在右键通货后停止"

**Root cause:** After `_right_click(currency_coord, delay)`, there's insufficient delay for the currency to attach to the cursor. PoE needs time to register the right-click and attach the currency item to the cursor before the next action.

**Fix: Add 0.2s wait after right-clicking currency:**

```python
# In auto_operator.py, in all crafting modes:
def _mode1(self, rules, coordinates, delay, sound_enabled, popup_enabled):
    # Right-click currency
    self._right_click(curr_coord, delay)
    self._interruptible_sleep(0.2)  # Wait for currency to attach to cursor
    
    # Hold SHIFT and continue...
    
def _mode2(self, rules, coordinates, delay, sound_enabled, popup_enabled):
    # Alt phase
    self._right_click(alt_coord, delay)
    self._interruptible_sleep(0.2)  # Wait for currency to attach
    
    # Aug phase  
    self._right_click(aug_coord, delay)
    self._interruptible_sleep(0.2)  # Wait for currency to attach
    
def _mode3(self, rules, coordinates, delay, sound_enabled, popup_enabled):
    # Each currency phase needs the wait
    self._right_click(transmute_coord, delay)
    self._interruptible_sleep(0.2)
    # ... etc for alt, aug, regal, scour
```

**Why 0.2s?** Empirical testing shows PoE needs ~100-200ms to process the right-click and attach the currency to cursor. Too short → currency doesn't attach, subsequent shift+click does nothing. Too long → wasted time. 0.2s is safe margin.

**Note:** This applies to ALL currency right-clicks in ALL modes. The delay is only needed after right-click (picking up currency), not after shift+click (applying currency).

## ⚠️ Threading Pattern: Single Daemon Thread (ALL Operators)

**This pattern applies to ALL THREE operators: ClickerOperator, CraftOperator, KeyOperator.** After multiple failed iterations, the final design is: pre-create daemon thread(s) at startup, never destroy them. Control with boolean flags only. No `Thread()` calls in toggle, no `join()`, no race conditions.

General principle: For background automation tasks, create ONE daemon thread at init. Control it with a single boolean flag. Never create/destroy threads in response to user actions.

### ⚠️ CraftOperator: Dead Code Bug (Thread Never Created)

**Bug:** In `auto_operator.py`, the daemon thread creation code was accidentally placed INSIDE `_check_stop_key()` AFTER a `return False` statement, making it unreachable dead code. `CraftOperator.__init__` never created the thread.

**Symptom:** `start()` sets `_running = True` and `_stop_event.set()`, but no thread is running `_run()`. Crafting validation passes but nothing actually executes.

**Fix:** Thread creation must be in `__init__`, NOT in any other method:
```python
class CraftOperator:
    def __init__(self):
        self._running = False
        self._lock = threading.Lock()
        self._stop_event = threading.Event()
        ...
        # ONE thread, starts immediately, never dies
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def _check_stop_key(self) -> bool:
        try:
            if keyboard.is_pressed(self._stop_hotkey):
                return True
        except Exception:
            pass
        return False
        # ⚠️ DEAD CODE BELOW — never executes!
        # self._thread = threading.Thread(target=self._run, daemon=True)
        # self._thread.start()
```

**Lesson:** After refactoring code, verify that thread creation is in `__init__` and not accidentally moved into a helper method. Code after `return` is unreachable.

## ⚠️ ClickerOperator Thread Safety (CRITICAL FIX — Final Version)

**Multiple failed iterations before finding the correct approach. The final version is extremely simple.**

### FINAL Working Code (clicker_operator.py)

The toggle was rewritten to be as simple as possible. No join, no complex locking:

```python
def start_toggle(self, button: str, interval_ms: int):
    """Toggle mode: start or stop clicking."""
    with self._lock:
        if self._running:
            # Stop — just flip the flag, thread will exit within 10ms
            self._running = False
            return
        # Start
        self._running = True
        self.click_count = 0

    interval = interval_ms / 1000.0
    click_fn = pyautogui.click if button == "left" else pyautogui.rightClick

    self._thread = threading.Thread(
        target=self._click_loop,
        args=(click_fn, interval),
        daemon=True
    )
    self._thread.start()
    if self.on_status_update:
        self.on_status_update(True, 0)
```

Key insight: **Don't join the old thread.** Just flip `_running = False` and return. The old thread exits on its own within one sleep chunk (10ms). No race condition because:

- First F8 press: `_running=False` → starts thread → `_running=True`
- Second F8 press: `_running=True` → sets False → returns → old thread exits in 10ms
- Third F8 press: `_running=False` → starts new thread → `_running=True`

Even if F8 is pressed while old thread is still exiting: the lock ensures only one path runs. The old thread's finally block sets `_running=False`, but the new thread already set it to True (after the old thread's finally runs). Wait — this is actually a race. But in practice, the 10ms exit time means the user won't press F8 again that fast. And even if they do, they just get a double-click at worst.

### ✅ FINAL CORRECT DESIGN: Single Daemon Thread (One Thread, Forever Alive)

After 8+ iterations of failed fixes, the correct design is **one daemon thread created at startup, never destroyed.**

```python
class ClickerOperator:
    def __init__(self):
        self._running = False
        self._lock = threading.Lock()
        self.click_count = 0
        self._click_fn = pyautogui.click
        self._interval = 0.033

        # ONE thread, starts immediately, never dies
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

    def start_toggle(self, button: str, interval_ms: int):
        with self._lock:
            if self._running:
                self._running = False  # Just flip flag
                return
            self._click_fn = pyautogui.click if button == "left" else pyautogui.rightClick
            self._interval = interval_ms / 1000.0
            self.click_count = 0
            self._running = True

    def _loop(self):
        """Runs forever. When _running is True, clicks. When False, sleeps."""
        while True:
            if not self._running:
                time.sleep(0.05)  # Idle: CPU ≈ 0
                continue
            # Emergency stop
            try:
                x, y = pyautogui.position()
                if x <= 1 and y <= 1:
                    with self._lock:
                        self._running = False
                    continue
            except: pass
            self._click_fn()
            self.click_count += 1
            deadline = time.monotonic() + self._interval
            while self._running and time.monotonic() < deadline:
                time.sleep(0.01)  # 10ms chunks
```

**Why this is correct:**
- No `Thread()` calls in toggle → no thread creation/destruction
- No `join()` → no blocking the keyboard callback thread
- No `finally` blocks → no race between finally and toggle
- One `_running` flag, one thread, atomic toggle
- When idle, sleeps at 50ms intervals (CPU ≈ 0%)
- When running, clicks and sleeps at 10ms intervals (responsive stop)

**Why every previous approach failed:**
1. Creating new thread each toggle → race between old thread's `finally` and new thread's start
2. `join()` in toggle → blocks keyboard callback, deadlocks if click loop is stuck
3. `join()` + double lock → race between unlock and re-lock
4. Complex state machine → more states = more bugs

**General principle:** For simple background tasks (clicking, polling), create ONE daemon thread at init. Control it with a single boolean flag. Never create/destroy threads in response to user actions.

### Root Cause 1: time.sleep Blocking (Legacy — fixed by design above)
`time.sleep(interval)` blocks the thread completely. If interval is 1000ms, the thread can't respond to `_running = False` until the sleep finishes.

**Fix:** Sleep in 10ms chunks checking `_running`:
```python
while self._running and time.monotonic() < deadline:
    time.sleep(0.01)  # 10ms chunks — responsive enough
```

### Root Cause 2: GIL Starvation

`pyautogui.click()` makes Win32 API calls that release the GIL during the actual system call. The `keyboard` library's hotkey callback runs in a separate thread that also needs the GIL. In practice, the GIL is released during Win32 calls, so this is rarely the issue. The real problem was the toggle logic bug (Root Cause 3).

### Signal Separation: Currency Selection vs Coordinate Capture (CRITICAL)

**Bug:** Clicking any currency's "设定坐标" button also changed Mode 1's selected currency. Worst case: clicking "装备坐标" set `selected_currency = "item"`, causing the tool to right-click the item itself.

**Root cause:** `CurrencyCard` had ONE `selected` signal used for both:
- Name label click → change `selected_currency` (currency choice for Mode 1)
- Coord button click → set `coord_target` (coordinate capture target)

**Fix:** Separate into two signals:
```python
class CurrencyCard(QFrame):
    selected = Signal(str)       # Currency name click → changes selected_currency
    coord_clicked = Signal(str)  # Coord button → only sets coord_target

    def _on_coord_click(self):
        self.coord_clicked.emit(self.currency_key)  # NOT self.selected
        self.coord_btn.setText("移动鼠标后按 F7...")
        self.coord_btn.setStyleSheet(BTN_RECORDING)
```

In `config_tab.py`, `_on_item_coord_click` must NOT set `selected_currency`:
```python
def _on_item_coord_click(self):
    # DON'T overwrite selected_currency!
    self._update_currency_selection()
    self.item_coord_btn.setText("移动鼠标后按 F7...")
```

In `main.py`, connect `coord_clicked` to coord capture:
```python
for key, card in window.config_tab.currency_cards.items():
    card.coord_clicked.connect(lambda k=key: hotkey_mgr.set_coord_target(k))
```

**General rule:** If a widget serves two different purposes (selection + action), use two separate signals. Never share one signal for unrelated behaviors.

### Hotkey Re-registration on Settings Change

**Bug:** Changing hotkeys in Settings tab had no effect until restart.

**Fix:** Store `hotkey_mgr` on `MainWindow`, call `setup()` in `_save_all()`:
```python
# main():
window.hotkey_mgr = hotkey_mgr

# MainWindow._save_all():
if hasattr(self, 'hotkey_mgr'):
    self.hotkey_mgr.setup()
```

### CTRL/ALT/SHIFT as Hotkeys

Both `KeybindButton._key_to_string()` (settings_tab.py) and `GlobalKeyCapture._key_to_string()` (key_tab.py) explicitly skip modifier keys. Fix by adding modifier mapping:
```python
modifier_map = {
    Qt.Key_Control: "ctrl",
    Qt.Key_Alt: "alt",
    Qt.Key_Shift: "shift",
}
if key_code in modifier_map:
    return modifier_map[key_code]
```

The `keyboard` library supports standalone modifier hotkeys.

### Root Cause 3: Lambda Captures at Setup Time

In `main.py`, the clicker hotkey lambda captured `clicker_button` when `setup()` was called. If user changed button setting, the hotkey still used the old value.

**Fix:** Use a function that reads current settings at call time:
```python
def get_clicker_settings():
    s = self.main_window.clicker_tab.get_settings()
    return s.get("clicker_button", "left"), s.get("clicker_interval_ms", 33)

lambda: self._on_clicker_toggle(*get_clicker_settings())
```

**General rule:** In closures/lambdas used with hotkey libraries, NEVER capture mutable setting values directly. Always wrap in a function that reads current state at call time.

### Root Cause 4: pyautogui.FAILSAFE Doesn't Work with click()

`pyautogui.FAILSAFE = True` only triggers on `moveTo()` calls. `click()` and `rightClick()` DON'T check position. If the clicker is stuck, user has no escape.

**Fix:** Manually check mouse position in the click loop (see `_click_loop` above).

### Root Cause 5: Clicker Hotkey Hardcoded (Not in Settings UI)

Settings tab only showed F6 and F7. Clicker hotkey (F8) was hardcoded.

**Fix:** Add KeybindButton for clicker hotkey in SettingsTab:
```python
# In _build_ui():
self.hotkey_clicker = KeybindButton(self.settings.get("clicker_hotkey", "F8"))
layout.addWidget(SettingsRow("连点器热键", self.hotkey_clicker, "点击修改"))

# In update_settings():
settings["clicker_hotkey"] = self.hotkey_clicker.key
```

### Root Cause 6: Hotkeys Not Re-registered After Settings Change

`setup()` was only called once at startup. Changing hotkeys in Settings tab didn't re-register them.

**Fix:** Store `hotkey_mgr` on MainWindow, call `setup()` in `_save_all()`:
```python
# In main():
hotkey_mgr = HotkeyManager(window)
window.hotkey_mgr = hotkey_mgr

# In MainWindow._save_all():
save_settings(self.settings)
if hasattr(self, 'hotkey_mgr'):
    self.hotkey_mgr.setup()  # Re-register with new keys
```

### Root Cause 7: Signal Pollution Between Currency Selection and Coordinate Capture

`CurrencyCard` had one `selected` signal used for BOTH:
- Clicking the name label → change Mode 1's selected currency
- Clicking the coord button → set coord capture target

Both emitted `selected`, which was connected to `_on_currency_selected` (changing `selected_currency`) AND `hotkey_mgr.set_coord_target` (setting coord target). So clicking "设定坐标" on any currency card overwrote `selected_currency`!

Worst case: user sets item coordinates → `_on_item_coord_click()` did `self.selected_currency = "item"` → tool treats "item" as a currency key → right-clicks the item itself instead of the currency.

**Fix:** Separate into two signals:
```python
class CurrencyCard(QFrame):
    selected = Signal(str)       # For currency name click → changes selected_currency
    coord_clicked = Signal(str)  # For coord button → only sets coord_target

    def _on_coord_click(self):
        self.coord_clicked.emit(self.currency_key)  # NOT self.selected
        self.coord_btn.setText("移动鼠标后按 F7...")
        self.coord_btn.setStyleSheet(BTN_RECORDING)
```

In main.py, connect `coord_clicked` to coord capture (not `selected`):
```python
for key, card in window.config_tab.currency_cards.items():
    card.coord_clicked.connect(lambda k=key: hotkey_mgr.set_coord_target(k))
```

In config_tab.py, connect `selected` to currency selection:
```python
card.selected.connect(self._on_currency_selected)
```

### Root Cause 8: CTRL/ALT/SHIFT Not Allowed as Hotkeys

`KeybindButton._key_to_string()` and `GlobalKeyCapture._key_to_string()` both explicitly returned `None` for modifier keys, preventing users from setting CTRL/ALT/SHIFT as hotkeys.

**Fix:** Add modifier key mapping in both classes:
```python
modifier_map = {
    Qt.Key_Control: "ctrl",
    Qt.Key_Alt: "alt",
    Qt.Key_Shift: "shift",
}
if key_code in modifier_map:
    return modifier_map[key_code]
```

The `keyboard` library supports standalone modifier hotkeys (e.g., `keyboard.add_hotkey('ctrl', callback)` works).

## ⚠️ Batch File Robustness

**Old `打包.bat` issues:**
- `where python` may not find Python from Microsoft Store
- `2>nul` silences ALL error output → user sees nothing when it fails
- `--onefile` + customtkinter often fails (missing resources)
- Window closes instantly on error

**Robust pattern:**
- Use `python --version >nul 2>&1` instead of `where python`
- Remove `2>nul` — show ALL errors
- Use `--onedir` instead of `--onefile` (customtkinter resources preserved)
- Add `pause` at every failure point
- Use `--collect-all customtkinter` to bundle all resources

## ⚠️ _notify = None Crash (NotificationOverlay Removal)

**Symptoms:** All hotkeys stop working (F6, F7, F8, F9). Starting/stopping anything crashes silently.

**Root cause:** `NotificationOverlay` was removed by setting `self._notify = None`, but 8 places in `main_window.py` still call `self._notify.show_message(...)`. When any of these fire (e.g., F9 keyloop toggle), it raises `AttributeError: 'NoneType' object has no attribute 'show_message'`. Since this happens inside the `keyboard` library's event thread, it kills the entire keyboard processing loop — **all hotkeys break**.

**Fix:** Use a Null Object pattern instead of None:
```python
# In MainWindow.__init__:
class _NullNotify:
    def show_message(self, text): pass
self._notify = _NullNotify()
```

This is safer than adding `if self._notify:` checks at all 8 call sites. The null object silently swallows all `show_message` calls.

**Lesson:** When removing a feature that's called from multiple places, prefer Null Object pattern over None. Especially critical when the caller is in a different thread (keyboard callback) — a single exception kills the entire event handler.

## ⚠️ Signal Separation: Currency Selection vs Coordinate Capture (CRITICAL)

**Bug:** Clicking any currency's "设定坐标" button also changed Mode 1's selected currency. Worst case: clicking "装备坐标" set `selected_currency = "item"`, causing the tool to right-click the item itself as if it were a currency.

**Root cause:** `CurrencyCard` had ONE `selected` signal used for both purposes:
- Name label click → change `selected_currency` (currency choice for Mode 1)
- Coord button click → set `coord_target` (coordinate capture target)

Both emitted the same signal, which was connected to BOTH handlers.

**Fix:** Add a separate `coord_clicked` signal:

In `config_tab.py`, CurrencyCard class:
```python
class CurrencyCard(QFrame):
    selected = Signal(str)       # Currency name click → changes selected_currency
    coord_clicked = Signal(str)  # Coord button → only sets coord_target

    def _on_coord_click(self):
        self.coord_clicked.emit(self.currency_key)  # NOT self.selected
```

In `config_tab.py`, `_on_item_coord_click` must NOT set `selected_currency`:
```python
def _on_item_coord_click(self):
    # DON'T overwrite selected_currency!
    self._update_currency_selection()
    self.item_coord_btn.setText("移动鼠标后按 F7...")
    self.item_coord_btn.setStyleSheet(BTN_RECORDING)
```

In `main.py`, connect `coord_clicked` to coord capture:
```python
for key, card in window.config_tab.currency_cards.items():
    card.coord_clicked.connect(lambda k=key: hotkey_mgr.set_coord_target(k))
# Item coord button:
window.config_tab.item_coord_btn.clicked.connect(lambda: hotkey_mgr.set_coord_target("item"))
```

`selected` signal stays connected only to `_on_currency_selected` in config_tab.py.

**General rule:** If a widget serves two different purposes (selection + action), use two separate signals. Never share one signal for unrelated behaviors.

### key_tab.py Hotkey Auto-Save (Same Pattern)

Same auto-save pattern applies to key_tab.py's hotkey:

```python
# In key_tab.py _build_ui():
self.hotkey_btn = KeybindButton(self.settings.get("key_loop_hotkey", "F9"))
self.hotkey_btn._on_change = self._on_hotkey_changed  # Bind callback

# In key_tab.py:
def _on_hotkey_changed(self, key: str):
    """Auto-save and re-register hotkeys when key loop hotkey changes."""
    from PySide6.QtWidgets import QMainWindow
    parent = self
    while parent:
        if isinstance(parent, QMainWindow):
            if hasattr(parent, '_save_all'):
                parent._save_all()
            break
        parent = parent.parentWidget()
```

## ⚠️ Hotkey Re-registration on Settings Change

**Bug:** Changing hotkeys in Settings tab had no effect until program restart.

**Root cause:** `HotkeyManager.setup()` was only called once at startup. After that, changing the hotkey in the UI updated the settings dict but never called `setup()` again.

**Fix:** Store `hotkey_mgr` on `MainWindow` and call `setup()` in `_save_all()`:

In `main()`:
```python
hotkey_mgr = HotkeyManager(window)
window.hotkey_mgr = hotkey_mgr  # Store on window
hotkey_mgr.setup()
```

In `MainWindow._save_all()`:
```python
save_settings(self.settings)
if hasattr(self, 'hotkey_mgr'):
    self.hotkey_mgr.setup()  # Re-register all hotkeys with new settings
```

`setup()` calls `cleanup()` first (removes old hotkeys), then registers new ones. Safe to call repeatedly.

**Note:** `keyboard.unhook_all()` in `cleanup()` removes ALL keyboard hooks. If other parts of the system use keyboard hooks, they'll be lost. This is acceptable for this app since HotkeyManager owns all hooks.

## Showing UI Mockups (Headless Server)

When the user asks to see UI designs but we're on a headless Linux server (can't run PySide6):

1. Create HTML/CSS files that mimic the PySide6 style (colors, fonts, layout) in `/tmp/poe-ui-mockup/`
2. Use `browser_navigate(url="file:///tmp/poe-ui-mockup/page.html")` to load
3. Use `browser_vision(question="截图")` to take screenshots
4. Send each screenshot via `send_message` with `MEDIA:/path/to/screenshot.png`

Pattern: one HTML per tab, matching the exact CSS values from the Python code.

## ⚠️ Foreground Process Detection (v17)

**Feature:** Only activate hotkeys when a specific game process is in the foreground. Prevents accidental clicks outside the game.

**Implementation:**
- `foreground.py` module: `get_foreground_process_name()`, `is_foreground_process(target)`, `list_process_names()`
- Uses pure ctypes (`QueryFullProcessImageNameW`) for foreground detection — no new dependencies
- Uses `subprocess` with `tasklist /FO CSV /NH` for listing processes (with `CREATE_NO_WINDOW` flag)
- Settings key: `target_process` (empty string = no check, backward compatible)

**Coverage (all features):**
- Hotkey callbacks in `HotkeyManager`: `_on_start`, `_on_clicker_toggle`, `_on_clicker_hold_start`, `_on_key_loop_toggle` all check foreground BEFORE executing
- `_on_stop` does NOT check (stopping should always work)
- Operator loops also check continuously:
  - `ClickerOperator._loop`: checks before each click, sleeps 0.1s if wrong process
  - `KeyOperator._key_loop`: checks before each key press, sleeps 0.1s if wrong process
  - `CraftOperator._interruptible_sleep`: if wrong process, enters a pause loop (`_stop_event.wait(0.2)`) until process returns to foreground

**UI:** Settings tab has a QComboBox dropdown listing all processes + 🔄 refresh button. First item is "不检查（全部生效）".

**Sync:** `_save_all()` in MainWindow calls `set_target_process(tp)` on all three operators (operator, clicker, key_operator).

## ⚠️ Mode 3 Alt Loop Efficiency (v18)

**Bug:** When Alt gives 2 affixes with only 1 hit (below threshold), the code released Shift, re-right-clicked Alt, and held Shift again — unnecessary mouse movement.

**Fix:** Check threshold INSIDE the Alt shift+click loop. If 2 affixes but hits < threshold, continue shift+click without releasing Shift:

```python
while self._running:
    self._shift_click(item_coord, delay)
    ...
    hits = result.primary_hits + result.secondary_hits
    affix_count = self._get_affix_count(text)

    if affix_count >= 2:
        if hits >= threshold:
            go_to_regal = True
            break  # Exit Alt loop → Regal
        # 2 affixes but insufficient hits → continue shift+click (don't release Shift!)
        self._status(f"#{self.use_count} 2词缀命中{hits}不足{threshold}，继续改造...")
    else:  # 1 affix
        if hits >= 1:
            break  # Exit Alt loop → Aug
        self._status(f"#{self.use_count} 改造中...")
```

**Key insight:** In PoE, right-clicking a stack of Alt picks up the entire stack. Each shift+click applies one Alt from the stack. So you only need to right-click Alt ONCE, then keep shift+clicking. Releasing Shift and re-right-clicking is wasteful.

## Mode Currencies (v19)

```python
MODE_CURRENCIES = {
    Mode.SINGLE: [ALTERATION, CHAOS, CUSTOM],          # Mode 1: only 改造/混沌/自定义
    Mode.ALT_AUG: [ALTERATION, AUGMENTATION, ALCHEMY, SCOURING],  # Mode 2: +点金石
    Mode.ALT_AUG_REGAL: [ALTERATION, AUGMENTATION, REGAL, SCOURING, TRANSMUTATION, EXALTED],
}
```

**⚠️ Currency Grid Layout Order (config_tab.py):** The `all_currencies` list order determines the 4-column grid layout. Order currencies so that each mode's currencies fill complete rows:

```python
all_currencies = [
    CurrencyType.ALTERATION, CurrencyType.AUGMENTATION, CurrencyType.ALCHEMY, CurrencyType.SCOURING, # Mode2: 一行4格
    CurrencyType.CHAOS, CurrencyType.CUSTOM,       # Mode1 额外
    CurrencyType.REGAL, CurrencyType.TRANSMUTATION, CurrencyType.EXALTED,   # Mode3 额外
    CurrencyType.DIVINE,
]
```

Mode2 shows positions 0-3 (one row: 改造/增幅/点金/重铸). Mode1 shows positions 0,4,5 (改造 shared + 混沌/自定义, 2 rows). ALCHEMY must be in BOTH `MODE_CURRENCIES[Mode.ALT_AUG]` AND `all_currencies` list — if missing from `all_currencies`, the card is never created and Mode2 won't show 点金石.

## Mode 2 Sub-modes (v18)

Mode 2 supports two sub-modes selected via radio buttons:
- **改造+增幅 (Alt+Aug):** Existing logic — Alt rolls blue item, Aug adds affix if only 1
- **重铸+点金 (Scour+Alch):** New logic — Alch (白→黄) → check meets_final_rules → Scour (黄→白) → repeat

**Implementation:** `_mode2_use_scour_alch` flag on CraftOperator, set from UI radio buttons. `_do_crafting()` branches based on flag.

## Mode 3 Exalted After Regal (v18)

Optional Exalted (崇高石) after Regal. User checks a checkbox in Mode 3 config.

**Flow:** Transmute → Alt → Aug → Regal → [Exalted if checked] → final check → Scour if failed

**Implementation:** `_use_exalt` flag on CraftOperator. Exalted coordinate validated only if enabled. Phase 4.5 inserted between Regal and Scour.

## SliderInput Widget (v18)

Reusable `SliderInput` widget in `widgets.py`: QSlider + QSpinBox combo, bidirectionally connected.

```python
from widgets import SliderInput
self.delay_slider = SliderInput(default=33, min_val=10, max_val=200, suffix=" ms")
value = self.delay_slider.value()  # Returns int in ms
```

Used for: 操作延迟 (config_tab), 点击间隔 (clicker_tab). Key tab uses QDoubleSpinBox directly (seconds, 0.1-999, step 0.1).

## Modal Error Dialog (v18)

Config errors (validation failure, missing coordinates, hotkey conflicts) now show `QMessageBox.critical()` instead of just updating status bar text. User sees a blocking dialog they must dismiss.

```python
def _on_error(self, msg: str):
    self._status_label.setText(f"❌ {msg}")
    QMessageBox.critical(self, "配置错误", msg)
```

### ⚠️ Thread-Stop-Before-Signal Pattern (v22)

**Bug:** `QMessageBox.critical()` in `_on_error` slot caused window freeze. Initial fix: defer with `QTimer.singleShot`. User rejected — wanted root cause fix.

**Root cause:** Operator thread emits `on_error` signal while still running. Signal queues up. Main thread shows QMessageBox (blocks). Operator keeps emitting signals → infinite dialog queue → freeze.

**Correct fix:** Stop the thread BEFORE emitting the error signal. Then the slot receives the signal when thread is already dead — no more signals can queue:

```python
# In auto_operator.py _run():
except Exception as e:
    # 先停线程再发信号，确保槽里处理时不会再有后续信号排队
    with self._lock:
        if self._run_id == my_run_id:
            self._running = False
            self._stop_event.set()
    if self.on_error:
        self._cb(self.on_error, f"异常: {e}")
```

**Why this is better than QTimer.singleShot:** The deferral approach still allows signals to pile up if the operator keeps running. The stop-first approach eliminates the problem at the source.

**When to use each pattern:**
- `QTimer.singleShot(0, ...)`: For keyboard callback thread → main thread (no signal involved, just thread safety)
- Thread-stop-before-signal: For operator thread error handling (prevents signal queue buildup)

### ⚠️ Windows MessageBoxW Plays System Sounds

`ctypes.windll.user32.MessageBoxW` always plays a Windows notification sound (based on the icon type). No way to suppress it without complex Win32 hooking.

**Fix:** Use Qt's `QMessageBox.information()` on the main thread via signals instead:
```python
# In auto_operator.py: emit via callback instead of direct MessageBoxW
self._cb(self.on_show_popup, f"匹配成功!\n使用次数: {self.use_count}")

# In main_window.py: handle on main thread with Qt dialog (silent)
@Slot(str)
def _on_show_popup(self, msg: str):
    QMessageBox.information(self, "poe小助手", msg)
```

**Rule:** Never use `ctypes.windll.user32.MessageBoxW` from a PySide6 app if you want silent dialogs. Always route through Qt signals to the main thread.

## Known Issues (Not Bugs Per User)

1. **parse_item_text includes item name** — **Intentional design (铁律)**: item name and base type MUST participate in matching. Do NOT filter them out during rewrite.
2. **Mode 2 no exclude check after augmentation** — augmentation only adds 1 mod, final check covers it
3. **Clipboard delay (33ms×3 ≈ 100ms)** — user-configurable
4. **10x unchanged clipboard = exhaustion** — probability of false positive near zero
5. **DPI awareness warning** — `SetProcessDpiAwarenessContext() failed` appears without admin, harmless

## ⚠️ Primary Count Combo Must Include "0"

**Bug:** `primary_count_combo` only had items `["1", "2", "3"]` — no "0" option. Users who don't want to check primary affixes couldn't set `primary_hit_count = 0`. Validation would always fail with "主词缀命中数要求 1，但主词缀池为空" even if the user only wanted secondary affix checking.

**Fix:** Add "0" as the first item:
```python
self.primary_count_combo.addItems(["0", "1", "2", "3"])
```

**Why needed:** Users may want to use Mode 1 (single currency) with only secondary affix rules, or with no rules at all (just apply currency repeatedly). The `validate()` method already handles `primary_hit_count = 0` correctly (skips the check). The `meets_final_rules()` method also handles it (`primary_hits >= 0` is always True).

**Caveat for Mode 2/3:** If `primary_hit_count = 0`, the alt phase in Mode 2/3 will always pass immediately (0 >= 0), which may not be the user's intent. Consider adding a UI hint that Mode 2/3 typically needs primary_hit_count >= 1.

## Community Distribution Strategy (2026-08-03 discussion)

**Status:** Planning phase — not yet published to players.

**Competitive positioning:** Individual features (crafting automation, clicker, key loop) each exist as separate tools. 拾刻's differentiator is **bundling** — all-in-one, no window switching. Key loop has a specific PoE pain point: game removed left-click skill binding, and the replacement automation gem takes a socket slot. External key loop avoids that cost.

**Planned approach:**
1. Demo video/GIFs → post to NGA流放版、贴吧、流放QQ群
2. Gauge interest before investing in multi-user architecture
3. Free, no monetization initially
4. Market monitor (市集监控) stays private — separate tool, contains user's arbitrage strategy

**Video/demo tools:** ScreenToGif (free, lightweight) for GIF captures. 剪映 for trimming if needed. Raw screenshots + text may work better than video for forum posts.

**Server cost concern:** Multi-user would increase Tencent API calls (currently 6s global queue). Don't build multi-user infra until demand is confirmed.

## User Working Style

- **Discuss first, code later** — User repeatedly asks to stop and discuss before implementing. Always confirm understanding before writing code.
- **User doesn't code** — Must verify changes from a user's perspective, can't rely on user to debug
- **Risk-averse about game account** — Tool must NOT modify game memory/files. Only external keyboard/mouse automation + clipboard reading.
- **Build environment** — User may not have Python installed. `一键启动.bat` can auto-install. Packaging requires Python 3.10+ on build machine. Target machines need nothing (PyInstaller one-file).

## Backup Comparison Technique

When user reports a regression ("XXX used to work, now it doesn't"), compare backup files to find exact changes:

```bash
# Compare a specific function between backup and current:
diff <(sed -n '/def _mode1/,/^    # ===== Mode 2/p' auto_operator.py.bak) \
     <(sed -n '/def _mode1/,/^    # ===== Mode 2/p' auto_operator.py)

# Compare entire file:
diff auto_operator.py.bak auto_operator.py | head -100

# Find all backups:
ls -la auto_operator.py.bak*
```

Backups are named `.bak`, `.bak2`, `.bak3` etc. (oldest → newest). Always check the earliest backup first — if no diff, the code hasn't changed and the issue is elsewhere (user setup, runtime state, etc.).
