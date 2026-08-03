"""PySide6 UI - Main Window with tab layout."""
import sys
import webbrowser
from pathlib import Path
from PySide6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QTabWidget, QLabel, QPushButton, QFrame, QSystemTrayIcon, QMenu, QMessageBox
)
from PySide6.QtCore import Qt, Signal, Slot, QTimer, QObject
from PySide6.QtGui import QIcon, QAction, QFont

from models import Mode, CurrencyType, CraftRules, AffixCheckResult
from storage import load_coordinates, save_coordinates, load_rules, save_rules, load_settings, save_settings
from auto_operator import CraftOperator
from clicker_operator import ClickerOperator
from key_operator import KeyOperator

from ui.config_tab import ConfigTab
from ui.settings_tab import SettingsTab
from ui.clicker_tab import ClickerTab
from ui.key_tab import KeyTab


class SignalBridge(QObject):
    """Thread-safe bridge: background thread emits signals, Qt dispatches to main thread."""
    status_update = Signal(str, int, int)
    match_found = Signal(object)
    stopped = Signal(str)
    error = Signal(str)
    show_popup = Signal(str)


class NotificationOverlay(QWidget):
    """Floating notification in screen center, auto-dismiss after 2s."""

    def __init__(self):
        super().__init__()
        self.setWindowFlags(
            Qt.FramelessWindowHint |
            Qt.WindowStaysOnTopHint |
            Qt.Tool
        )
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setAttribute(Qt.WA_ShowWithoutActivating)

        self.container = QFrame(self)
        self.container.setStyleSheet("""
            QFrame {
                background: rgba(30, 42, 58, 0.85);
                border-radius: 16px;
                border: 1px solid rgba(255,255,255,0.15);
            }
        """)

        layout = QVBoxLayout(self.container)
        layout.setContentsMargins(32, 18, 32, 18)

        self.label = QLabel()
        self.label.setStyleSheet("color: white; font-size: 18px; font-weight: bold; background: transparent; border: none;")
        self.label.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.label)

        self._timer = QTimer(self)
        self._timer.setSingleShot(True)
        self._timer.timeout.connect(self.hide)

    def show_message(self, text: str):
        self.label.setText(text)
        self.label.adjustSize()
        w = max(280, self.label.sizeHint().width() + 64)
        h = self.label.sizeHint().height() + 36
        self.container.setFixedSize(w, h)
        self.setFixedSize(w, h)
        screen = QApplication.primaryScreen()
        if screen:
            geo = screen.availableGeometry()
            self.move(
                geo.x() + (geo.width() - w) // 2,
                geo.y() + (geo.height() - h) // 2,
            )
        self.setWindowOpacity(1.0)
        self.show()
        self._timer.start(2000)


class MainWindow(QMainWindow):
    # Thread-safe signals for hotkey callbacks (emitted from keyboard listener thread)
    _hotkey_start = Signal()
    _hotkey_stop = Signal()
    _hotkey_key_loop = Signal()
    _hotkey_coord = Signal(str, int, int)  # target, x, y

    # Signals for cross-thread status updates (clicker / key loop)
    _clicker_status = Signal(bool, int)
    _key_status = Signal(bool, object)

    # Signal for receiving ads data from background thread
    _ads_data = Signal(object)

    FEEDBACK_URL = "https://docs.qq.com/form/page/DZkpVa05VSEtUZkhT"

    def __init__(self):
        super().__init__()
        self.setWindowTitle("拾刻")
        self.setFixedSize(720, 660)
        self.setStyleSheet("""
            QMainWindow { background: #dae8f5; }
            QTabWidget::pane { border: 1px solid rgba(0,80,160,0.15); border-radius: 6px; background: rgba(255,255,255,0.3); }
            QTabBar::tab {
                background: rgba(255,255,255,0.3); color: #6a7a8a; padding: 10px 22px;
                border: 1px solid rgba(0,80,160,0.1); border-bottom: none;
                border-top-left-radius: 8px; border-top-right-radius: 8px;
                margin-right: 3px; font-size: 12px;
            }
            QTabBar::tab:selected { background: rgba(255,255,255,0.6); color: #1a6fb5; border-color: rgba(26,111,181,0.25); font-weight: bold; }
            QTabBar::tab:hover { border-color: rgba(26,111,181,0.4); color: #1a6fb5; }
        """)

        # Load data
        self.coordinates = load_coordinates()
        self.rules = load_rules()
        self.settings = load_settings()

        # Operator
        self.operator = CraftOperator()
        self._bridge = SignalBridge()
        self._bridge.status_update.connect(self._on_status_update)
        self._bridge.match_found.connect(self._on_match_found)
        self._bridge.stopped.connect(self._on_stopped)
        self._bridge.error.connect(self._on_error)
        self._bridge.show_popup.connect(self._on_show_popup)
        self.operator.on_status_update = lambda *a: self._bridge.status_update.emit(*a)
        self.operator.on_match_found = lambda r: self._bridge.match_found.emit(r)
        self.operator.on_stopped = lambda r: self._bridge.stopped.emit(r)
        self.operator.on_error = lambda m: self._bridge.error.emit(m)
        self.operator.on_show_popup = lambda m: self._bridge.show_popup.emit(m)

        # Clicker – use Qt signal for thread-safe GUI update
        self.clicker = ClickerOperator()
        self._clicker_status.connect(self._on_clicker_update)
        self.clicker.on_status_update = lambda running, count: self._clicker_status.emit(running, count)

        # Key loop – use Qt signal for thread-safe GUI update
        self.key_operator = KeyOperator()
        self._key_status.connect(self._on_key_update)
        self.key_operator.on_status_update = lambda running, counts: self._key_status.emit(running, counts)

        # Defend against repeated error/popup storms
        self._error_shown = False
        self._popup_shown = False

        # Connect ads signal
        self._ads_data.connect(self._update_ads)

        # Central widget
        central = QWidget()
        self.setCentralWidget(central)
        main_layout = QVBoxLayout(central)
        main_layout.setContentsMargins(12, 12, 12, 12)
        main_layout.setSpacing(8)

        # --- 顶部广告区（原标题位置） ---
        self._ad_area = QFrame()
        self._ad_area.setFixedHeight(50)
        self._ad_area.setStyleSheet("""
            QFrame {
                background: rgba(255,255,255,0.35);
                border: 1px dashed rgba(0,80,160,0.2);
                border-radius: 6px;
            }
        """)
        ad_layout = QHBoxLayout(self._ad_area)
        ad_layout.setContentsMargins(12, 0, 12, 0)
        ad_layout.setSpacing(12)

        # 占位标签 1
        placeholder1 = QLabel("广告位招租")
        placeholder1.setAlignment(Qt.AlignCenter)
        placeholder1.setStyleSheet("color: #aaa; font-size: 11px; border: 1px dashed #ccc; border-radius: 4px;")
        placeholder1.setFixedSize(220, 36)
        ad_layout.addWidget(placeholder1)

        # 占位标签 2
        placeholder2 = QLabel("广告位招租")
        placeholder2.setAlignment(Qt.AlignCenter)
        placeholder2.setStyleSheet("color: #aaa; font-size: 11px; border: 1px dashed #ccc; border-radius: 4px;")
        placeholder2.setFixedSize(220, 36)
        ad_layout.addWidget(placeholder2)

        ad_layout.addStretch()
        main_layout.addWidget(self._ad_area)

        # Tabs
        self.tabs = QTabWidget()
        self.config_tab = ConfigTab(self.coordinates, self.rules, self.settings)
        self.config_tab.set_hotkey_coord(self.settings.get("hotkey_set_coord", "F8"))
        self.settings_tab = SettingsTab(self.settings)
        self.clicker_tab = ClickerTab(self.settings)
        self.key_tab = KeyTab(self.settings, self.key_operator)
        self.tabs.addTab(self.config_tab, "📋 配置")
        self.tabs.addTab(self.clicker_tab, "🖱️ 连点")
        self.tabs.addTab(self.key_tab, "⌨️ 按键")
        self.tabs.addTab(self.settings_tab, "⚙️ 设置")
        main_layout.addWidget(self.tabs)

        # Status bar
        self._status_frame = QFrame()
        self._status_frame.setFixedHeight(40)
        self._status_frame.setStyleSheet("""
            QFrame {
                background: rgba(255,255,255,0.45);
                border-radius: 8px;
                border: 1px solid rgba(0,80,160,0.1);
            }
        """)
        status_layout = QHBoxLayout(self._status_frame)
        status_layout.setContentsMargins(16, 4, 16, 4)

        self._status_dot = QLabel("●")
        self._status_dot.setStyleSheet("color: #aaa; font-size: 14px;")
        status_layout.addWidget(self._status_dot)

        self._status_label = QLabel("就绪")
        self._status_label.setStyleSheet("color: #445566; font-size: 12px;")
        status_layout.addWidget(self._status_label)

        # 使用说明按钮（新增）
        guide_btn = QPushButton("📖 使用说明")
        guide_btn.setStyleSheet("""
            QPushButton {
                background: transparent; border: none; color: #556; font-size: 11px;
                padding: 2px 6px;
            }
            QPushButton:hover { color: #1a6fb5; text-decoration: underline; }
        """)
        guide_btn.setCursor(Qt.PointingHandCursor)
        guide_btn.clicked.connect(lambda: webbrowser.open("https://open.cancanneed.top/guide.html"))
        status_layout.addWidget(guide_btn)

        # Separator
        sep = QLabel("|")
        sep.setStyleSheet("color: #ccc; font-size: 12px; margin: 0 4px;")
        status_layout.addWidget(sep)

        # Feedback button
        feedback_btn = QPushButton("📩 反馈")
        feedback_btn.setStyleSheet("""
            QPushButton {
                background: transparent; border: none; color: #556; font-size: 11px;
                padding: 2px 6px;
            }
            QPushButton:hover { color: #1a6fb5; text-decoration: underline; }
        """)
        feedback_btn.setCursor(Qt.PointingHandCursor)
        feedback_btn.clicked.connect(lambda: webbrowser.open(self.FEEDBACK_URL))
        status_layout.addWidget(feedback_btn)

        status_layout.addStretch()

        # --- 底部广告占位 ---
        self._bottom_ad = QLabel("广告位招租")
        self._bottom_ad.setAlignment(Qt.AlignCenter)
        self._bottom_ad.setStyleSheet("color: #aaa; font-size: 10px; border: 1px dashed #ccc; border-radius: 3px; padding: 2px 6px;")
        self._bottom_ad.setFixedSize(160, 22)
        status_layout.addWidget(self._bottom_ad)

        self._use_count_label = QLabel("")
        self._use_count_label.setStyleSheet("color: #1a6fb5; font-size: 12px; font-weight: bold;")
        status_layout.addWidget(self._use_count_label)

        main_layout.addWidget(self._status_frame)

        # System tray
        self._setup_tray()

        # Window icon
        if getattr(sys, 'frozen', False):
            icon_path = Path(sys._MEIPASS) / "poe.ico"
        else:
            icon_path = Path(__file__).parent.parent / "poe.ico"
        if icon_path.exists():
            self.setWindowIcon(QIcon(str(icon_path)))

        
                # 真正的浮层通知（非模态，不阻塞音效）
        from ui.main_window import NotificationOverlay
        self._notify = NotificationOverlay()

        # Connect hotkey signals to main-thread handlers
        self._hotkey_start.connect(self._start_crafting)
        self._hotkey_stop.connect(self._stop_crafting)
        self._hotkey_key_loop.connect(self._toggle_key_loop)
        self._hotkey_coord.connect(self._handle_coord)
        # 强制刷新，消除初始化阶段的小方块闪烁
        self.update()
        self.repaint()

        # 自动检测 PoE 进程（延迟 0.5 秒）
        QTimer.singleShot(500, self._auto_detect_poe)

    def _auto_detect_poe(self):
        """启动时自动检测 PathOfExile 进程，命中则锁定目标进程。"""
        saved = self.settings.get("target_process", "")
        if saved:
            return
        try:
            from foreground import list_process_names
            poe_variants = [
                "PathOfExile.exe", "PathOfExile_x64.exe", "PathOfExile",
                "PathOfExileSteam.exe", "PathOfExile_KG.exe"
            ]
            proc_names = [p.lower() for p in list_process_names()]
            matched = None
            for variant in poe_variants:
                if variant.lower() in proc_names:
                    matched = variant
                    break
            if matched:
                self.settings["target_process"] = matched
                if hasattr(self.settings_tab, 'set_target_process'):
                    self.settings_tab.set_target_process(matched)
                self.operator.set_target_process(matched)
                self.clicker.set_target_process(matched)
                self.key_operator.set_target_process(matched)
                self._status_label.setText(f"已锁定: {matched}")
        except Exception:
            pass

    def _setup_tray(self):
        """Create system tray icon (force attempt even if not 'available' on some Windows)."""
        self.tray_icon = QSystemTrayIcon(self)
        if getattr(sys, 'frozen', False):
            tray_icon_path = Path(sys._MEIPASS) / "poe.ico"
        else:
            tray_icon_path = Path(__file__).parent.parent / "poe.ico"
        if tray_icon_path.exists():
            self.tray_icon.setIcon(QIcon(str(tray_icon_path)))
        tray_menu = QMenu()
        show_action = QAction("显示", self)
        show_action.triggered.connect(self.show)
        tray_menu.addAction(show_action)
        quit_action = QAction("退出", self)
        quit_action.triggered.connect(self._quit_app)
        tray_menu.addAction(quit_action)
        self.tray_icon.setContextMenu(tray_menu)
        self.tray_icon.activated.connect(self._tray_activated)
        try:
            self.tray_icon.show()
        except Exception:
            pass

    def _tray_activated(self, reason):
        if reason == QSystemTrayIcon.DoubleClick:
            self.show()

    def closeEvent(self, event):
        if self.operator.is_running:
            event.ignore()
            self.hide()
        else:
            self._save_all()
            event.accept()

    def _quit_app(self):
        self._stop_crafting()
        self.clicker.stop()
        self.key_operator.stop()
        self._save_all()
        QApplication.quit()

    def _save_all(self):
        self.config_tab.update_rules(self.rules)
        self.settings_tab.update_settings(self.settings)
        self.settings.update(self.clicker_tab.get_settings())
        self.settings.update(self.key_tab.get_settings())
        self.settings["delay_ms"] = self.config_tab.get_delay_ms()
        save_rules(self.rules)
        save_coordinates(self.coordinates)
        save_settings(self.settings)
        tp = self.settings.get("target_process", "")
        self.operator.set_target_process(tp)
        self.clicker.set_target_process(tp)
        self.key_operator.set_target_process(tp)
        self.operator.set_mode2_scour_alch(self.config_tab.get_mode2_scour_alch())
        self.operator.set_use_exalt(self.config_tab.get_use_exalt())
        if hasattr(self, 'hotkey_mgr'):
            self.hotkey_mgr.setup()
        self.config_tab.set_hotkey_coord(self.settings.get("hotkey_set_coord", "F8"))

    def _start_crafting(self):
        self.config_tab.update_rules(self.rules)
        valid, msg = self.rules.validate()
        if not valid:
            self._on_error(msg)
            return
        self._save_all()
        self.operator.start(
            self.rules,
            self.coordinates,
            self.config_tab.get_delay_ms(),
            self.settings["sound_enabled"],
            self.settings["popup_enabled"],
            self.settings.get("selected_sound", "default_ding.wav"),
            self.settings.get("clipboard_unchanged_threshold", 15),
            self.settings.get("hotkey_stop", "F6"),
        )
        if self.operator.is_running:
            self._status_dot.setStyleSheet("color: #1b8a3e; font-size: 14px;")
            self._notify.show_message("▶ 洗词缀 启动")

    def _stop_crafting(self):
        self.operator.stop()
        self._status_dot.setStyleSheet("color: #aaa; font-size: 14px;")
        self._notify.show_message("⏹ 洗词缀 停止")

    def _clear_rules(self):
        self.config_tab.clear_all()
        self.rules = CraftRules()

    @Slot(str, int, int)
    def _on_status_update(self, text: str, use_count: int, match_count: int):
        self._status_label.setText(text)
        self._use_count_label.setText(f"已使用: {use_count} 次" if use_count else "")

    @Slot(object)
    def _on_match_found(self, result: AffixCheckResult):
        self._status_dot.setStyleSheet("color: #aaa; font-size: 14px;")
        

    @Slot(str)
    def _on_stopped(self, reason: str):
        self._status_dot.setStyleSheet("color: #aaa; font-size: 14px;")
        self._status_label.setText(reason)
        

    @Slot(str)
    def _on_error(self, msg: str):
        if self._error_shown:
            return
        self._error_shown = True
        self._status_dot.setStyleSheet("color: #aaa; font-size: 14px;")
        self._status_label.setText(f"❌ {msg}")
        QMessageBox.critical(self, "错误", msg)
        self._error_shown = False

    @Slot(str)
    def _on_show_popup(self, msg: str):
        """成功弹窗 — 使用非模态浮层，不阻塞音效。"""
        if self._popup_shown:
            return
        self._popup_shown = True
        self._notify.show_message(msg)
        # 浮层会在 2 秒后自动隐藏，重置标记
        QTimer.singleShot(2200, lambda: setattr(self, '_popup_shown', False))

    @Slot(str)
    def _on_success(self, sound_enabled, popup_enabled):
        self._status(f"匹配成功! 共{self.use_count}次", force=True)
        self._cb(self.on_match_found, None)
        self._play_sound(sound_enabled)
        if popup_enabled:
            self._cb(self.on_show_popup, f"匹配成功!\n使用次数: {self.use_count}")

    @Slot(bool, int)
    def _on_clicker_update(self, running: bool, count: int):
        was_running = self.clicker_tab.status_label.text() == "连点中..."
        self.clicker_tab.set_running(running)
        self.clicker_tab.update_click_count(count)
        """ if running and not was_running:
            self._notify.show_message("▶ 连点器 启动")
        elif not running and was_running:
            self._notify.show_message("⏹ 连点器 停止") """  #注释来关闭弹窗

    @Slot(bool, object)
    def _on_key_update(self, running: bool, counts: dict):
        self.key_tab.set_running(running)

    @Slot(object)
    def _update_ads(self, ads_data: list):
        """Receive ads data from background thread and update top/bottom ad widgets."""
        try:
            # 处理顶部广告区
            top_layout = self._ad_area.layout()
            if top_layout:
                while top_layout.count():
                    item = top_layout.takeAt(0)
                    if item.widget():
                        item.widget().deleteLater()

            # 收集顶部和底部广告
            top_ads = [a for a in ads_data if a.get("location") == "top"]
            bottom_ads = [a for a in ads_data if a.get("location") == "bottom"]

            # 重建顶部广告
            if not top_ads:
                p1 = QLabel("广告位招租")
                p1.setAlignment(Qt.AlignCenter)
                p1.setStyleSheet("color: #aaa; font-size: 11px; border: 1px dashed #ccc; border-radius: 4px;")
                p1.setFixedSize(220, 36)
                top_layout.addWidget(p1)
                top_layout.addStretch()
            else:
                for ad in top_ads:
                    if ad.get("type") == "text":
                        btn = QPushButton(ad.get("text", ""))
                        btn.setFlat(True)
                        btn.setStyleSheet(ad.get("style", "color: #888; font-size: 12px;"))
                        if ad.get("link"):
                            btn.clicked.connect(lambda checked, url=ad["link"]: webbrowser.open(url))
                        top_layout.addWidget(btn)
                top_layout.addStretch()

            # 更新底部广告
            bottom_ad_text = "广告位招租"
            bottom_ad_style = "color: #aaa; font-size: 10px; border: 1px dashed #ccc; border-radius: 3px; padding: 2px 6px;"
            bottom_ad_link = None
            if bottom_ads:
                # 取第一个底部广告
                ba = bottom_ads[0]
                if ba.get("type") == "text":
                    bottom_ad_text = ba.get("text", bottom_ad_text)
                    bottom_ad_style = ba.get("style", bottom_ad_style)
                    bottom_ad_link = ba.get("link")

            # 重新创建底部广告控件
            old_bottom = self._bottom_ad
            if old_bottom:
                old_bottom.deleteLater()
            if bottom_ad_link:
                self._bottom_ad = QPushButton(bottom_ad_text)
                self._bottom_ad.setFlat(True)
                self._bottom_ad.setStyleSheet(bottom_ad_style)
                self._bottom_ad.clicked.connect(lambda checked, url=bottom_ad_link: webbrowser.open(url))
            else:
                self._bottom_ad = QLabel(bottom_ad_text)
                self._bottom_ad.setAlignment(Qt.AlignCenter)
                self._bottom_ad.setStyleSheet(bottom_ad_style)
                self._bottom_ad.setFixedSize(160, 22)
            # 添加到状态栏布局（用原来的位置）
            status_layout = self._status_frame.layout()
            if status_layout:
                # 找到 use_count_label 之前的位置插入
                status_layout.insertWidget(status_layout.count() - 1, self._bottom_ad)
        except Exception:
            pass

    def _start_key_loop(self):
        slots = []
        for row in self.key_tab._slot_rows:
            slots.append(row.get_slot())
        self.key_operator.start(slots)
        self.key_tab.set_running(True)
        # self._notify.show_message("▶ 按键循环 启动")  通过注释来关闭弹窗，功能保留，需要开启时删除注释即可

    def _stop_key_loop(self):
        self.key_operator.stop()
        self.key_tab.set_running(False)
        # self._notify.show_message("⏹ 按键循环 停止")  通过注释来关闭弹窗，功能保留，需要开启时删除注释即可

    def _toggle_key_loop(self):
        if self.key_operator.is_running:
            self._stop_key_loop()
        else:
            self._start_key_loop()

    def _handle_coord(self, target: str, x: int, y: int):
        self.coordinates[target] = (x, y)
        if target == "item":
            config_tab = self.config_tab
            config_tab.item_coord_btn.setText(f"✓ ({x}, {y})")
            config_tab.item_coord_btn.setStyleSheet("QPushButton { background: rgba(27,138,62,0.08); border: 1px solid rgba(27,138,62,0.3); border-radius: 6px; color: #1b8a3e; font-size: 11px; padding: 4px 10px; }")
            config_tab.item_clear_btn.setVisible(True)
        elif target in self.config_tab.currency_cards:
            card = self.config_tab.currency_cards[target]
            card.set_coordinate((x, y))
        save_coordinates(self.coordinates)
        from models import CurrencyType
        label = CurrencyType.LABELS.get(target, target)
        print(f"坐标已保存: {label} = ({x}, {y})")