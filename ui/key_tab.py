"""Key loop tab: multi-key auto-press with independent intervals."""
from PySide6.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QLabel, QPushButton,
    QFrame, QScrollArea
)
from PySide6.QtCore import Qt, Signal, QObject, QEvent
from PySide6.QtGui import QKeyEvent

from key_operator import KeyOperator, KeySlot
from ui.settings_tab import ToggleSwitch, KeybindButton
from widgets import SliderInput


class KeyCaptureButton(QPushButton):
    """Button that captures a key press when clicked. Uses app-level event filter."""
    key_captured = Signal(str)

    def __init__(self, initial_key: str = ""):
        super().__init__(initial_key if initial_key else "点击设置")
        self.key = initial_key
        self.setFixedSize(90, 28)
        self._listening = False
        self._update_style()
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

    def _update_style(self):
        if self.key:
            self.setText(self.key)
        else:
            self.setText("点击设置")
        self.setStyleSheet("""
            QPushButton {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                color: #1a6fb5; border-radius: 6px; font-size: 12px;
            }
            QPushButton:hover { border-color: #1a6fb5; }
        """)

    def set_key(self, key_name: str):
        """Set key from external capture."""
        if self._listening and key_name:
            self.key = key_name
            self._listening = False
            self._update_style()
            self.key_captured.emit(key_name)

    def cancel(self):
        """Cancel listening."""
        self._listening = False
        self._update_style()


class GlobalKeyCapture(QObject):
    """Global event filter to capture key presses for KeyCaptureButton."""

    def __init__(self, app):
        super().__init__()
        self._active_button: KeyCaptureButton | None = None
        app.installEventFilter(self)

    def listen(self, button: KeyCaptureButton):
        """Start listening for a key press for the given button."""
        # Cancel previous if any
        if self._active_button and self._active_button is not button:
            self._active_button.cancel()
        self._active_button = button

    def eventFilter(self, obj, event):
        if self._active_button and event.type() == QEvent.KeyPress:
            key_name = self._key_to_string(event)
            if key_name:
                self._active_button.set_key(key_name)
                self._active_button = None
            return True  # Consume the event
        return super().eventFilter(obj, event)

    @staticmethod
    def _key_to_string(event: QKeyEvent) -> str | None:
        """Convert key event to string name."""
        from PySide6.QtCore import Qt

        key_code = event.key()

        # Modifier keys (standalone)
        modifier_map = {
            Qt.Key_Control: "ctrl",
            Qt.Key_Alt: "alt",
            Qt.Key_Shift: "shift",
        }
        if key_code in modifier_map:
            return modifier_map[key_code]

        # Function keys
        func_keys = {
            Qt.Key_F1: "F1", Qt.Key_F2: "F2", Qt.Key_F3: "F3",
            Qt.Key_F4: "F4", Qt.Key_F5: "F5", Qt.Key_F6: "F6",
            Qt.Key_F7: "F7", Qt.Key_F8: "F8", Qt.Key_F9: "F9",
            Qt.Key_F10: "F10", Qt.Key_F11: "F11", Qt.Key_F12: "F12",
        }
        if key_code in func_keys:
            return func_keys[key_code]

        # Named keys
        named_keys = {
            Qt.Key_Return: "enter", Qt.Key_Enter: "enter",
            Qt.Key_Space: "space", Qt.Key_Tab: "tab",
            Qt.Key_Escape: "esc", Qt.Key_Backspace: "backspace",
            Qt.Key_Delete: "delete", Qt.Key_Insert: "insert",
            Qt.Key_Home: "home", Qt.Key_End: "end",
            Qt.Key_PageUp: "pageup", Qt.Key_PageDown: "pagedown",
            Qt.Key_Up: "up", Qt.Key_Down: "down",
            Qt.Key_Left: "left", Qt.Key_Right: "right",
        }
        if key_code in named_keys:
            return named_keys[key_code]

        # Printable ASCII
        text = event.text()
        if text and 32 <= ord(text) <= 126:
            return text.lower()

        return None


class KeySlotRow(QFrame):
    """A single key slot row: toggle + key capture + delay."""

    def __init__(self, index: int, capture: GlobalKeyCapture, slot: KeySlot | None = None):
        super().__init__()
        self.index = index
        self.capture = capture
        self.setStyleSheet("""
            QFrame {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.1);
                border-radius: 6px; padding: 4px;
            }
        """)

        layout = QHBoxLayout(self)
        layout.setContentsMargins(10, 6, 10, 6)
        layout.setSpacing(10)

        # Slot number
        num_label = QLabel(f"#{index + 1}")
        num_label.setStyleSheet("color: #8a9aaa; font-size: 11px;")
        num_label.setFixedWidth(24)
        layout.addWidget(num_label)

        # Enable toggle
        self.toggle = ToggleSwitch(slot.enabled if slot else False)
        layout.addWidget(self.toggle)

        # Key capture button
        self.key_btn = KeyCaptureButton(slot.key if slot else "")
        self.key_btn.clicked.connect(self._on_key_btn_clicked)
        layout.addWidget(self.key_btn)

        # Delay input (seconds, min 0.1, step 0.1)
        from PySide6.QtWidgets import QDoubleSpinBox
        layout.addWidget(QLabel("延时"))
        self.delay_spin = QDoubleSpinBox()
        self.delay_spin.setRange(0.1, 999.0)
        self.delay_spin.setSingleStep(0.1)
        self.delay_spin.setDecimals(1)
        self.delay_spin.setSuffix(" 秒")
        self.delay_spin.setValue(slot.delay if slot else 1.0)
        self.delay_spin.setFixedWidth(90)
        self.delay_spin.setStyleSheet("""
            QDoubleSpinBox {
                background: rgba(255,255,255,0.5); border: 1px solid rgba(0,80,160,0.15);
                color: #1e2a3a; padding: 4px; border-radius: 6px;
            }
        """)
        layout.addWidget(self.delay_spin)

        layout.addStretch()

    def _on_key_btn_clicked(self):
        self.capture.listen(self.key_btn)

    def get_slot(self) -> KeySlot:
        return KeySlot(
            enabled=self.toggle.value,
            key=self.key_btn.key,
            delay=self.delay_spin.value(),
        )


class KeyTab(QWidget):
    """Key loop configuration tab."""

    def __init__(self, settings: dict, operator: KeyOperator):
        super().__init__()
        self.settings = settings
        self.operator = operator
        self._all_slots_shown = False
        self._build_ui()

    def _build_ui(self):
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setStyleSheet("QScrollArea { border: none; }")

        container = QWidget()
        layout = QVBoxLayout(container)
        layout.setSpacing(8)

        # Title
        title = QLabel("⌨️ 按键循环器")
        title.setStyleSheet("color: #1a6fb5; font-size: 14px; font-weight: bold; padding: 8px 0;")
        layout.addWidget(title)

        desc = QLabel("每个按键按自己的间隔独立循环触发，互不影响")
        desc.setStyleSheet("color: #8a9aaa; font-size: 11px; padding-bottom: 4px;")
        layout.addWidget(desc)

        # Hotkey row
        hotkey_frame = QFrame()
        hotkey_frame.setStyleSheet("""
            QFrame {
                background: rgba(255,255,255,0.4); border: 1px solid rgba(0,80,160,0.1);
                border-radius: 6px; padding: 8px;
            }
        """)
        hotkey_layout = QHBoxLayout(hotkey_frame)
        hotkey_label = QLabel("🎯 启停热键")
        hotkey_label.setStyleSheet("color: #334455; font-size: 12px;")
        hotkey_layout.addWidget(hotkey_label)
        hotkey_layout.addStretch()

        self.hotkey_btn = KeybindButton(self.settings.get("key_loop_hotkey", "F9"))
        self.hotkey_btn._on_change = self._on_hotkey_changed
        hotkey_layout.addWidget(self.hotkey_btn)
        layout.addWidget(hotkey_frame)

        # Global capture (for slot key buttons)
        from PySide6.QtWidgets import QApplication
        self._capture = GlobalKeyCapture(QApplication.instance())

        # Key slots
        saved_slots = self.settings.get("key_loop_slots", [])
        self._slot_rows: list[KeySlotRow] = []

        for i in range(10):
            slot_data = saved_slots[i] if i < len(saved_slots) else None
            slot = None
            if slot_data:
                slot = KeySlot(
                    enabled=slot_data.get("enabled", False),
                    key=slot_data.get("key", ""),
                    delay=slot_data.get("delay", 1.0),
                )
            row = KeySlotRow(i, self._capture, slot)
            self._slot_rows.append(row)
            layout.addWidget(row)
            if i >= 5:
                row.setVisible(False)

        # Load more button
        self._load_more_btn = QPushButton("▼ 加载更多 (5/10)")
        self._load_more_btn.setStyleSheet("""
            QPushButton {
                background: rgba(255,255,255,0.4); color: #6a7a8a; border: 1px solid rgba(0,80,160,0.1);
                border-radius: 6px; padding: 8px; font-size: 12px;
            }
            QPushButton:hover { border-color: #1a6fb5; color: #1a6fb5; }
        """)
        self._load_more_btn.clicked.connect(self._toggle_slots)
        layout.addWidget(self._load_more_btn)

        # Status
        self._status_frame = QFrame()
        self._status_frame.setStyleSheet("""
            QFrame {
                background: rgba(255,255,255,0.3); border-radius: 8px; padding: 12px;
            }
        """)
        status_layout = QHBoxLayout(self._status_frame)
        self._status_dot = QLabel("●")
        self._status_dot.setStyleSheet("color: #aaa; font-size: 18px;")
        status_layout.addWidget(self._status_dot)
        self._status_label = QLabel("按键循环器就绪")
        self._status_label.setStyleSheet("color: #445566; font-size: 13px;")
        status_layout.addWidget(self._status_label)
        status_layout.addStretch()
        layout.addWidget(self._status_frame)

        layout.addStretch()

        scroll.setWidget(container)
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.addWidget(scroll)

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

    def _toggle_slots(self):
        self._all_slots_shown = not self._all_slots_shown
        for i in range(5, 10):
            self._slot_rows[i].setVisible(self._all_slots_shown)
        if self._all_slots_shown:
            self._load_more_btn.setText("▲ 收起 (10/10)")
        else:
            self._load_more_btn.setText("▼ 加载更多 (5/10)")

    def get_settings(self) -> dict:
        """Get current settings from UI."""
        slots = []
        for row in self._slot_rows:
            s = row.get_slot()
            slots.append({"enabled": s.enabled, "key": s.key, "delay": s.delay})
        return {
            "key_loop_hotkey": self.hotkey_btn.key,
            "key_loop_slots": slots,
        }

    def set_running(self, running: bool):
        if running:
            self._status_dot.setStyleSheet("color: #1b8a3e; font-size: 18px;")
            self._status_label.setText("运行中...")
        else:
            self._status_dot.setStyleSheet("color: #aaa; font-size: 18px;")
            self._status_label.setText("按键循环器就绪")
