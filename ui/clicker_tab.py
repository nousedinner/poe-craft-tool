"""Auto-clicker tab - v5. Two independent hotkeys, no mode toggle."""
from PySide6.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, QGroupBox
)
from PySide6.QtCore import Qt
from ui.settings_tab import KeybindButton
from widgets import SliderInput


class ClickerTab(QWidget):

    def __init__(self, settings: dict):
        super().__init__()
        self.settings = settings
        self._click_button = settings.get("clicker_button", "left")
        self._build_ui()

    def _build_ui(self):
        root = QVBoxLayout(self)
        root.setSpacing(8)

        title = QLabel("连点器")
        title.setStyleSheet("font-size: 16px; font-weight: bold; color: #1a6fb5;")
        root.addWidget(title)

        d = QLabel("按热键触发鼠标连点，可配合洗词缀使用")
        d.setStyleSheet("font-size: 11px; color: #888;")
        root.addWidget(d)

        # ---- 点击按键 ----
        box1 = QGroupBox("点击按键")
        r1 = QHBoxLayout()
        self.btn_left = QPushButton("[ ] 左键")
        self.btn_left.setCheckable(True)
        self.btn_left.setMinimumHeight(36)
        self.btn_left.setMinimumWidth(110)
        self.btn_left.clicked.connect(lambda: self._pick("left"))
        r1.addWidget(self.btn_left)

        self.btn_right = QPushButton("[*] 右键")
        self.btn_right.setCheckable(True)
        self.btn_right.setChecked(True)
        self.btn_right.setMinimumHeight(36)
        self.btn_right.setMinimumWidth(110)
        self.btn_right.clicked.connect(lambda: self._pick("right"))
        r1.addWidget(self.btn_right)
        r1.addStretch()
        box1.setLayout(r1)
        root.addWidget(box1)

        # ---- 热键设置 ----
        box3 = QGroupBox("热键设置")
        r3 = QVBoxLayout()
        r3.setSpacing(6)

        # Toggle hotkey row
        row_toggle = QHBoxLayout()
        hk1 = QLabel("切换热键:")
        hk1.setStyleSheet("color: #334455; font-size: 12px;")
        hk1.setFixedWidth(70)
        row_toggle.addWidget(hk1)

        self.hotkey_btn = KeybindButton(self.settings.get("clicker_hotkey", "F9"))
        self.hotkey_btn._on_change = self._on_hotkey_changed
        row_toggle.addWidget(self.hotkey_btn)

        hint1 = QLabel("按下触发 / 再次按下停止")
        hint1.setStyleSheet("color: #8a9aaa; font-size: 10px; padding-left: 6px;")
        row_toggle.addWidget(hint1)
        row_toggle.addStretch()
        r3.addLayout(row_toggle)

        # Hold hotkey row
        row_hold = QHBoxLayout()
        hk2 = QLabel("按住热键:")
        hk2.setStyleSheet("color: #334455; font-size: 12px;")
        hk2.setFixedWidth(70)
        row_hold.addWidget(hk2)

        self.hold_hotkey_btn = KeybindButton(self.settings.get("clicker_hold_hotkey", "F11"))
        self.hold_hotkey_btn._on_change = self._on_hotkey_changed
        row_hold.addWidget(self.hold_hotkey_btn)

        hint2 = QLabel("长按触发 / 松开停止")
        hint2.setStyleSheet("color: #8a9aaa; font-size: 10px; padding-left: 6px;")
        row_hold.addWidget(hint2)
        row_hold.addStretch()
        r3.addLayout(row_hold)

        # Interval row
        row_interval = QHBoxLayout()
        iv_lbl = QLabel("点击间隔:")
        iv_lbl.setStyleSheet("color: #334455; font-size: 12px;")
        iv_lbl.setFixedWidth(70)
        row_interval.addWidget(iv_lbl)
        self.interval_slider = SliderInput(default=self.settings.get("clicker_interval_ms", 33))
        row_interval.addWidget(self.interval_slider)
        row_interval.addStretch()
        r3.addLayout(row_interval)

        box3.setLayout(r3)
        root.addWidget(box3)

        # ---- 状态 ----
        bar = QHBoxLayout()
        self.dot = QLabel("●")
        self.dot.setStyleSheet("color: #aaa; font-size: 16px;")
        bar.addWidget(self.dot)
        self.status_label = QLabel("连点器就绪")
        bar.addWidget(self.status_label)
        bar.addStretch()
        bar.addWidget(QLabel("已点击:"))
        self.click_count = QLabel("0")
        self.click_count.setStyleSheet("font-weight: bold; color: #1a6fb5;")
        bar.addWidget(self.click_count)
        root.addLayout(bar)

        root.addStretch()

        # Sync UI state
        self._refresh()

    def _auto_save(self):
        from PySide6.QtWidgets import QMainWindow
        parent = self
        while parent:
            if isinstance(parent, QMainWindow):
                if hasattr(parent, '_save_all'):
                    parent._save_all()
                break
            parent = parent.parentWidget()

    def _on_hotkey_changed(self, key: str):
        self._auto_save()

    def _pick(self, btn: str):
        self._click_button = btn
        self._refresh()
        self._auto_save()

    def _refresh(self):
        ON = "[*] "
        OFF = "[ ] "
        if self._click_button == "left":
            self.btn_left.setText(ON + "左键")
            self.btn_left.setChecked(True)
            self.btn_right.setText(OFF + "右键")
            self.btn_right.setChecked(False)
        else:
            self.btn_left.setText(OFF + "左键")
            self.btn_left.setChecked(False)
            self.btn_right.setText(ON + "右键")
            self.btn_right.setChecked(True)

    def set_running(self, running: bool):
        if running:
            self.dot.setStyleSheet("color: #1b8a3e; font-size: 16px;")
            self.status_label.setText("连点中...")
        else:
            self.dot.setStyleSheet("color: #aaa; font-size: 16px;")
            self.status_label.setText("连点器就绪")

    def update_click_count(self, count: int):
        self.click_count.setText(str(count))

    def get_settings(self) -> dict:
        return {
            "clicker_hotkey": self.hotkey_btn.key,
            "clicker_hold_hotkey": self.hold_hotkey_btn.key,
            "clicker_interval_ms": self.interval_slider.value(),
            "clicker_button": self._click_button,
        }
