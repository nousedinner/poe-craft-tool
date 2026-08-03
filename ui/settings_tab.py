"""Settings tab: hotkeys, delay, notification toggles."""
from PySide6.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QLabel, QPushButton,
    QSpinBox, QFrame, QComboBox, QTextEdit, QApplication
)
from PySide6.QtCore import Qt, Signal, Property, QPropertyAnimation, QEasingCurve
from PySide6.QtGui import QFont, QPainter, QBrush, QColor, QPen
import os
import subprocess
from pathlib import Path


class ToggleSwitch(QWidget):
    """iOS 风格滑动开关，带平滑动画。"""
    toggled = Signal(bool)

    def __init__(self, initial: bool = True):
        super().__init__()
        self._checked = initial
        self._knob_pos = 22 if initial else 2  # 滑块 x 坐标
        self.setFixedSize(44, 24)
        self.setCursor(Qt.PointingHandCursor)

        # 动画
        self._anim = QPropertyAnimation(self, b"knob_pos")
        self._anim.setDuration(200)
        self._anim.setEasingCurve(QEasingCurve.InOutCubic)

    # 动画属性
    def _get_knob_pos(self):
        return self._knob_pos

    def _set_knob_pos(self, pos):
        self._knob_pos = pos
        self.update()

    knob_pos = Property(float, _get_knob_pos, _set_knob_pos)

    @property
    def value(self) -> bool:
        return self._checked

    @value.setter
    def value(self, v: bool):
        if v != self._checked:
            self._checked = v
            self._animate()

    def mousePressEvent(self, event):
        self._checked = not self._checked
        self.toggled.emit(self._checked)
        self._animate()

    def _animate(self):
        target = 22 if self._checked else 2
        self._anim.stop()
        self._anim.setStartValue(self._knob_pos)
        self._anim.setEndValue(target)
        self._anim.start()

    def paintEvent(self, event):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)

        # 轨道
        track_rect = self.rect().adjusted(0, 0, 0, 0)
        track_color = QColor("#34C759") if self._checked else QColor("#E2E8F0")
        p.setPen(Qt.NoPen)
        p.setBrush(QBrush(track_color))
        p.drawRoundedRect(track_rect, 12, 12)

        # 滑块
        knob_d = 20
        knob_y = (self.height() - knob_d) // 2
        knob_x = int(self._knob_pos)
        p.setBrush(QBrush(QColor("white")))
        p.setPen(QPen(QColor(0, 0, 0, 18), 1))
        p.drawEllipse(knob_x, knob_y, knob_d, knob_d)

class SettingsRow(QFrame):
    """A single settings row with label and widget."""

    def __init__(self, label: str, widget: QWidget, hint: str = ""):
        super().__init__()
        self.setStyleSheet("QFrame { border-bottom: 1px solid rgba(0,80,160,0.08); }")
        layout = QHBoxLayout(self)
        layout.setContentsMargins(0, 8, 0, 8)

        label_widget = QLabel(label)
        label_widget.setStyleSheet("color: #334455; font-size: 12px;")
        label_widget.setFixedWidth(120)
        layout.addWidget(label_widget)

        layout.addStretch()

        if hint:
            hint_label = QLabel(hint)
            hint_label.setStyleSheet("color: #8a9aaa; font-size: 11px; margin-right: 8px;")
            layout.addWidget(hint_label)

        layout.addWidget(widget)


class KeybindButton(QPushButton):
    """A button that captures a key press for hotkey binding."""

    def __init__(self, initial_key: str = "F6"):
        super().__init__(initial_key)
        self.key = initial_key
        self.setFixedSize(80, 28)
        self.setStyleSheet("""
            QPushButton {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                color: #1a6fb5; border-radius: 6px; font-size: 12px;
            }
            QPushButton:hover { border-color: #1a6fb5; }
        """)
        self._listening = False
        self._on_change = None  # Callback(key) when hotkey changes
        self.clicked.connect(self._start_listening)

    def _start_listening(self):
        self._listening = True
        self.setText("按下按键...")
        self.setStyleSheet("""
            QPushButton {
                background: rgba(26,111,181,0.1); border: 1px solid #1a6fb5;
                color: #1a6fb5; border-radius: 6px; font-size: 12px;
            }
        """)

    def keyPressEvent(self, event):
        if self._listening:
            key_name = self._key_to_string(event.key())
            if key_name:
                old_key = self.key
                self.key = key_name
                self.setText(key_name)
                self._listening = False
                self.setStyleSheet("""
                    QPushButton {
                        background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                        color: #1a6fb5; border-radius: 6px; font-size: 12px;
                    }
                    QPushButton:hover { border-color: #1a6fb5; }
                """)
                if self._on_change and key_name != old_key:
                    self._on_change(key_name)
        else:
            super().keyPressEvent(event)

    def _key_to_string(self, key_code: int) -> str | None:
        """Convert Qt key code to string."""
        from PySide6.QtCore import Qt

        # Function keys
        key_map = {
            Qt.Key_F1: "F1", Qt.Key_F2: "F2", Qt.Key_F3: "F3",
            Qt.Key_F4: "F4", Qt.Key_F5: "F5", Qt.Key_F6: "F6",
            Qt.Key_F7: "F7", Qt.Key_F8: "F8", Qt.Key_F9: "F9",
            Qt.Key_F10: "F10", Qt.Key_F11: "F11", Qt.Key_F12: "F12",
        }
        if key_code in key_map:
            return key_map[key_code]

        # Modifier keys (standalone)
        modifier_map = {
            Qt.Key_Control: "ctrl",
            Qt.Key_Alt: "alt",
            Qt.Key_Shift: "shift",
        }
        if key_code in modifier_map:
            return modifier_map[key_code]

        # Named keys
        named = {
            Qt.Key_Return: "enter", Qt.Key_Enter: "enter",
            Qt.Key_Space: "space", Qt.Key_Tab: "tab",
            Qt.Key_Escape: "esc", Qt.Key_Backspace: "backspace",
            Qt.Key_Delete: "delete", Qt.Key_Insert: "insert",
            Qt.Key_Home: "home", Qt.Key_End: "end",
            Qt.Key_PageUp: "pageup", Qt.Key_PageDown: "pagedown",
            Qt.Key_Up: "up", Qt.Key_Down: "down",
            Qt.Key_Left: "left", Qt.Key_Right: "right",
        }
        if key_code in named:
            return named[key_code]

        # Printable keys
        if 32 <= key_code <= 126:
            return chr(key_code)

        return None


class SettingsTab(QWidget):
    """Settings configuration tab."""

    def __init__(self, settings: dict):
        super().__init__()
        self.settings = settings
        self._build_ui()
        self._load_state()

    def _build_ui(self):
        layout = QVBoxLayout(self)
        layout.setSpacing(0)

        title = QLabel("⚙️ 设置")
        title.setStyleSheet("color: #1a6fb5; font-size: 14px; font-weight: bold; padding: 8px 0;")
        layout.addWidget(title)

        # Hotkey settings
        self.hotkey_start = KeybindButton(self.settings.get("hotkey_start", "F6"))
        self.hotkey_start._on_change = self._on_hotkey_changed
        layout.addWidget(SettingsRow("启动热键", self.hotkey_start, "点击修改"))

        self.hotkey_stop = KeybindButton(self.settings.get("hotkey_stop", "F7"))
        self.hotkey_stop._on_change = self._on_hotkey_changed
        layout.addWidget(SettingsRow("停止热键", self.hotkey_stop, "点击修改"))

        self.hotkey_coord = KeybindButton(self.settings.get("hotkey_set_coord", "F8"))
        self.hotkey_coord._on_change = self._on_hotkey_changed
        layout.addWidget(SettingsRow("坐标设定键", self.hotkey_coord))

        # Target process selector
        proc_frame = QFrame()
        proc_frame.setStyleSheet("QFrame { border-bottom: 1px solid rgba(0,80,160,0.08); }")
        proc_layout = QVBoxLayout(proc_frame)
        proc_layout.setContentsMargins(0, 8, 0, 8)
        proc_layout.setSpacing(6)

        proc_row = QHBoxLayout()
        proc_label = QLabel("目标进程")
        proc_label.setStyleSheet("color: #334455; font-size: 12px;")
        proc_label.setFixedWidth(120)
        proc_row.addWidget(proc_label)
        proc_row.addStretch()

        self.process_combo = QComboBox()
        self.process_combo.setStyleSheet("""
            QComboBox {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                color: #1e2a3a; padding: 4px 8px; border-radius: 6px; font-size: 11px;
            }
            QComboBox QAbstractItemView {
                background: rgba(255,255,255,0.9); color: #1e2a3a;
                selection-background-color: rgba(26,111,181,0.15);
            }
        """)
        self.process_combo.setMinimumWidth(200)
        self.process_combo.addItem("不检查（全部生效）", "")
        self._refresh_process_list()
        proc_row.addWidget(self.process_combo)

        proc_hint = QLabel("选择后仅在该游戏窗口前台时热键才生效")
        proc_hint.setStyleSheet("color: #8a9aaa; font-size: 10px;")
        proc_row.addWidget(proc_hint)

        refresh_proc_btn = QPushButton("🔄")
        refresh_proc_btn.setFixedSize(28, 28)
        refresh_proc_btn.setStyleSheet("""
            QPushButton {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                color: #8a9aaa; border-radius: 6px; font-size: 12px;
            }
            QPushButton:hover { border-color: #1a6fb5; color: #1a6fb5; }
        """)
        refresh_proc_btn.setToolTip("刷新进程列表")
        refresh_proc_btn.clicked.connect(self._refresh_process_list)
        proc_row.addWidget(refresh_proc_btn)
        proc_layout.addLayout(proc_row)

        layout.addWidget(proc_frame)

        # --- 统计行为告知（醒目） ---
                # --- 统计行为告知 ---
        stats_label = QLabel("📊 隐私声明")
        stats_label.setStyleSheet("color: #1a6fb5; font-size: 13px; font-weight: bold; padding: 0 0 4px 0;")
        layout.addWidget(stats_label)

        stats_text = QLabel(
            "本工具仅在启动时发送一条匿名信息（包含应用名称与当日日期），"
            "用于统计使用人数。不收集、不上传任何个人身份信息、设备信息或游戏数据。"
        )
        stats_text.setWordWrap(True)
        stats_text.setStyleSheet("color: #334455; font-size: 12px; padding-bottom: 8px;")
        layout.addWidget(stats_text)

        stats_link = QLabel("<a href='https://open.cancanneed.top/share/poe-craft-tool-5c9767d3' style='color: #1a6fb5; text-decoration: underline;'>查看统计</a>")
        stats_link.setOpenExternalLinks(True)
        stats_link.setStyleSheet("font-size: 11px;")
        layout.addWidget(stats_link)
        layout.addSpacing(12)
        # Sound settings
        sound_frame = QFrame()
        sound_frame.setStyleSheet("QFrame { border-bottom: 1px solid #0f346033; }")
        sound_layout = QVBoxLayout(sound_frame)
        sound_layout.setContentsMargins(0, 8, 0, 8)
        sound_layout.setSpacing(6)

        # Row 1: label + toggle + sound selector + folder button
        sound_row1 = QHBoxLayout()
        sound_label = QLabel("声音提醒")
        sound_label.setStyleSheet("color: #334455; font-size: 12px;")
        sound_label.setFixedWidth(120)
        sound_row1.addWidget(sound_label)
        sound_row1.addStretch()

        sound_hint = QLabel("选择音效:")
        sound_hint.setStyleSheet("color: #8a9aaa; font-size: 11px; margin-left: 12px;")
        sound_row1.addWidget(sound_hint)

        self.sound_combo = QComboBox()
        self.sound_combo.setStyleSheet("""
            QComboBox {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                color: #1e2a3a; padding: 4px 8px; border-radius: 6px; font-size: 11px;
            }
            QComboBox QAbstractItemView {
                background: rgba(255,255,255,0.9); color: #1e2a3a;
                selection-background-color: rgba(26,111,181,0.15);
            }
        """)
        self.sound_combo.setMinimumWidth(160)
        self._refresh_sound_list()
        sound_row1.addWidget(self.sound_combo)

        refresh_btn = QPushButton("🔄")
        refresh_btn.setFixedSize(28, 28)
        refresh_btn.setStyleSheet("""
            QPushButton {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                color: #8a9aaa; border-radius: 6px; font-size: 12px;
            }
            QPushButton:hover { border-color: #1a6fb5; color: #1a6fb5; }
        """)
        refresh_btn.setToolTip("刷新音效列表")
        refresh_btn.clicked.connect(self._refresh_sound_list)
        sound_row1.addWidget(refresh_btn)

        # Folder open button
        folder_btn = QPushButton("📁")
        folder_btn.setFixedSize(28, 28)
        folder_btn.setStyleSheet("""
            QPushButton {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                color: #1a6fb5; border-radius: 6px; font-size: 14px;
            }
            QPushButton:hover { border-color: #1a6fb5; }
        """)
        folder_btn.setToolTip("打开音效文件夹")
        folder_btn.clicked.connect(self._open_sounds_folder)
        sound_row1.addWidget(folder_btn)

        self.sound_toggle = ToggleSwitch(self.settings.get("sound_enabled", True))
        sound_row1.addWidget(self.sound_toggle)


        sound_layout.addLayout(sound_row1)

       

        layout.addWidget(sound_frame)

        # Popup toggle
        self.popup_toggle = ToggleSwitch(self.settings.get("popup_enabled", True))
        layout.addWidget(SettingsRow("弹窗提醒", self.popup_toggle))

                # 一键回藏身处
        hideout_frame = QFrame()
        hideout_layout = QHBoxLayout(hideout_frame)
        hideout_layout.setContentsMargins(0, 8, 0, 8)
        hideout_label = QLabel("一键回藏身处")
        hideout_label.setStyleSheet("color: #334455; font-size: 12px;")
        hideout_label.setFixedWidth(120)
        hideout_layout.addWidget(hideout_label)
        hideout_layout.addStretch()

        

        self.hideout_hotkey_btn = KeybindButton(self.settings.get("hideout_hotkey", "F2"))
        self.hideout_hotkey_btn._on_change = self._on_hotkey_changed
        hideout_layout.addWidget(self.hideout_hotkey_btn)

        self.hideout_toggle = ToggleSwitch(self.settings.get("hideout_enabled", False))
        hideout_layout.addWidget(self.hideout_toggle)

        hideout_frame.setStyleSheet("QFrame { border-bottom: 1px solid rgba(0,80,160,0.08); }")
        layout.addWidget(hideout_frame)

        layout.addStretch()

    def _load_state(self):
        """Restore saved state into UI."""
        # Restore target process selection (fix bug: was empty before)
        target = self.settings.get("target_process", "")
        if target:
            idx = self.process_combo.findData(target)
            if idx < 0:
                self.process_combo.addItem(target, target)
                idx = self.process_combo.findData(target)
            self.process_combo.setCurrentIndex(idx)

    def set_target_process(self, name: str):
        """Set the target process dropdown to the given name (for external auto-detect)."""
        if not name:
            idx = self.process_combo.findData("")
            if idx >= 0:
                self.process_combo.setCurrentIndex(idx)
            return
        idx = self.process_combo.findData(name)
        if idx < 0:
            self.process_combo.addItem(name, name)
            idx = self.process_combo.findData(name)
        self.process_combo.setCurrentIndex(idx)

    def _on_hotkey_changed(self, key: str):
        """Called when a hotkey is modified in the UI. Auto-saves and re-registers."""
        # Find the main window via parent chain
        from PySide6.QtWidgets import QMainWindow
        parent = self
        while parent:
            if isinstance(parent, QMainWindow):
                if hasattr(parent, '_save_all'):
                    parent._save_all()
                break
            parent = parent.parentWidget()

    def _get_sounds_dir(self) -> Path:
        """Get the sounds directory path."""
        import sys
        if getattr(sys, '_MEIPASS', None):
            # Packaged: use EXE's directory
            return Path(sys.executable).parent / "sounds"
        return Path(__file__).parent.parent / "sounds"

    def _refresh_sound_list(self):
        """Scan sounds folder and populate dropdown."""
        self.sound_combo.clear()

        # 查找默认叮声文件（支持多种后缀）
        sounds_dir = self._get_sounds_dir()
        default_file = None
        if sounds_dir.exists():
            for f in sounds_dir.iterdir():
                if f.stem == 'default_ding' and f.suffix.lower() in ('.wav', '.mp3', '.ogg', '.flac'):
                    default_file = f.name
                    break

        # 添加默认叮声选项
        self.sound_combo.addItem("默认叮声", default_file or "default_ding.wav")

        # 加载其他自定义音效
        if sounds_dir.exists():
            audio_files = sorted([
                f.name for f in sounds_dir.iterdir()
                if f.suffix.lower() in ('.wav', '.mp3', '.ogg', '.flac')
                and f.stem != 'default_ding'
            ])
            for fname in audio_files:
                self.sound_combo.addItem(fname, fname)

        # Restore previous selection
        saved = self.settings.get("selected_sound", default_file or "default_ding.wav")
        idx = self.sound_combo.findData(saved)
        if idx >= 0:
            self.sound_combo.setCurrentIndex(idx)

    def _open_sounds_folder(self):
        """Open the sounds folder in file explorer."""
        sounds_dir = self._get_sounds_dir()
        sounds_dir.mkdir(exist_ok=True)
        os.startfile(str(sounds_dir))

    def _refresh_process_list(self):
        """Scan running processes and populate dropdown."""
        from foreground import list_process_names
        saved = self.process_combo.currentData() or ""
        self.process_combo.clear()
        self.process_combo.addItem("不检查（全部生效）", "")
        for name in list_process_names():
            self.process_combo.addItem(name, name)
        # Restore previous selection
        idx = self.process_combo.findData(saved)
        if idx >= 0:
            self.process_combo.setCurrentIndex(idx)

    def update_settings(self, settings: dict):
        """Update settings dict from current UI state."""
        settings["hotkey_start"] = self.hotkey_start.key
        settings["hotkey_stop"] = self.hotkey_stop.key
        settings["hotkey_set_coord"] = self.hotkey_coord.key
        settings["target_process"] = self.process_combo.currentData() or ""
        settings["sound_enabled"] = self.sound_toggle.value
        settings["selected_sound"] = self.sound_combo.currentData() or "default_ding.wav"
        settings["popup_enabled"] = self.popup_toggle.value
        settings["hideout_enabled"] = self.hideout_toggle.value
        settings["hideout_hotkey"] = self.hideout_hotkey_btn.key