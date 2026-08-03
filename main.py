"""拾刻 - Main entry point."""
import sys
import os
import threading
import json
import urllib.request
from pathlib import Path
import time

# Add project root to path
PROJECT_ROOT = Path(__file__).parent
sys.path.insert(0, str(PROJECT_ROOT))

from PySide6.QtWidgets import QApplication, QMessageBox  # 改动：增加 QMessageBox 用于热键冲突弹窗
from PySide6.QtGui import QIcon
from PySide6.QtCore import QObject, Signal
import keyboard
import pyautogui

from ui.main_window import MainWindow
from storage import load_settings, save_coordinates


CURRENT_VERSION = "1.0.1" # 用于版本检查，格式为 "主版本.次版本.修订号"，必须为字符串

def _execute_hideout(delay_ms: int):
    """执行一键回藏身处：Enter → sleep(0.1s) → 输入 /hideout → Enter"""
    import keyboard
    try:
        keyboard.press_and_release('enter')
        time.sleep(0.1)
        keyboard.write('/hideout')
        keyboard.press_and_release('enter')
    except Exception:
        pass

class BackgroundBridge(QObject):
    """Bridge for background thread results to main thread."""
    ads_data = Signal(object)
    version_outdated = Signal(str)  # 参数：下载链接

def _show_update_dialog(url: str):
    """主线程弹窗：强制更新"""
    from PySide6.QtWidgets import QMessageBox
    import webbrowser
    msg = QMessageBox()
    msg.setIcon(QMessageBox.Warning)
    msg.setWindowTitle("发现新版本")
    msg.setText("检测到新版本，请下载更新后使用。")
    msg.setStandardButtons(QMessageBox.Ok)
    msg.button(QMessageBox.Ok).clicked.connect(lambda: webbrowser.open(url))
    msg.exec()
    sys.exit(0)


def _send_daily_ping(bridge: BackgroundBridge):
    """后台线程：发送签到并拉取广告数据。"""


        # --- 版本检查（强制更新） ---
    try:
        req = urllib.request.Request(
            "https://open.cancanneed.top/version.json",
            headers={"User-Agent": "Mozilla/5.0 poe-craft-tool/1.0"}
        )
        with urllib.request.urlopen(req, timeout=5) as resp:
            version_data = json.loads(resp.read().decode("utf-8"))
        latest = version_data.get("latest", "")
        if latest and CURRENT_VERSION < latest:
            # 需要更新，通过 bridge 信号通知主线程弹窗并退出
            bridge.version_outdated.emit(version_data.get("url", ""))
            return
    except Exception:
        pass  # 网络失败静默跳过
    # --- 签到 ---
    try:
        data = json.dumps({
            "type": "event",
            "payload": {
                "website": "0c4520ad-ae66-4453-a901-7bdfef3c4b44",
                "url": "/app/poe-craft-tool",
                "hostname": "拾刻",
                "language": "zh-CN",
                "screen": "1920x1080",
                "title": "拾刻启动",
                "event": "pageview"
            }
        }).encode("utf-8")
        req = urllib.request.Request(
            "https://open.cancanneed.top/api/send",
            data=data,
            headers={
                "Content-Type": "application/json",
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) poe-craft-tool/1.0"
            }
        )
        urllib.request.urlopen(req, timeout=5)
    except Exception:
        pass

    # --- 广告请求 ---
    ads = None
    try:
        req = urllib.request.Request(
            "https://open.cancanneed.top/ads.json",
            headers={"User-Agent": "Mozilla/5.0 poe-craft-tool/1.0"}
        )
        with urllib.request.urlopen(req, timeout=5) as resp:
            ads_json = json.loads(resp.read().decode("utf-8"))
            ads = ads_json.get("ads", [])
    except Exception as e:
        print(f"[广告] 请求失败: {e}")

    # 把广告数据抛回主线程
    if bridge:
        print(f"[广告] 收到数据: {ads}")
        bridge.ads_data.emit(ads or [])


class HotkeyManager:
    """Manages global hotkeys for start/stop, coordinate setting, and clicker."""

    def __init__(self, main_window: MainWindow):
        self.main_window = main_window
        self._coord_target = None
        self._hotkey_ids = []   # IDs from add_hotkey (toggle mode)
        self._hook_ids = []     # IDs from on_press_key/on_release_key (hold mode)
        self._clicker_held = False

    def setup(self):
        """Register hotkeys."""
        settings = self.main_window.settings
        hotkey_start = settings.get("hotkey_start", "F6")
        hotkey_stop = settings.get("hotkey_stop", "F7")
        hotkey_coord = settings.get("hotkey_set_coord", "F8")
        hotkey_clicker = settings.get("clicker_hotkey", "F9")
        hotkey_key_loop = settings.get("key_loop_hotkey", "F10")

        self.cleanup()

        # Check for hotkey conflicts
                # Check for hotkey conflicts
        all_hotkeys = {
            "启动热键": hotkey_start,
            "停止热键": hotkey_stop,
            "坐标设定键": hotkey_coord,
            "连点器热键": hotkey_clicker,
            "按键循环热键": hotkey_key_loop,
        }
        conflict_list = self._check_conflicts(all_hotkeys)   # 改动：现在返回冲突列表
        if conflict_list:
            # 改动：用 QMessageBox 模态弹窗展示所有冲突，父窗口为 main_window
            msg_text = "检测到热键冲突：\n\n" + "\n".join(conflict_list)
            QMessageBox.warning(self.main_window, "热键冲突", msg_text)

        # Craft tool start
        try:
            h_id = keyboard.add_hotkey(hotkey_start, self._on_start)
            self._hotkey_ids.append(h_id)
        except Exception as e:
            print(f"Failed to register hotkey {hotkey_start}: {e}")

        # Craft tool stop
        try:
            h_id = keyboard.add_hotkey(hotkey_stop, self._on_stop)
            self._hotkey_ids.append(h_id)
        except Exception as e:
            print(f"Failed to register hotkey {hotkey_stop}: {e}")

        # Coordinate capture
        try:
            h_id = keyboard.add_hotkey(hotkey_coord, self._on_set_coord)
            self._hotkey_ids.append(h_id)
        except Exception as e:
            print(f"Failed to register hotkey {hotkey_coord}: {e}")

        # Clicker hotkeys — always register both toggle and hold
        clicker_button = settings.get("clicker_button", "left")
        clicker_interval = settings.get("clicker_interval_ms", 33)
        hotkey_clicker = settings.get("clicker_hotkey", "F9")
        hotkey_clicker_hold = settings.get("clicker_hold_hotkey", "F11")

        def get_clicker_settings():
            """Read current clicker settings from the UI at call time."""
            s = self.main_window.clicker_tab.get_settings()
            return s.get("clicker_button", "left"), s.get("clicker_interval_ms", 33)

        # Toggle mode hotkey
        try:
            h_id = keyboard.add_hotkey(
                hotkey_clicker,
                lambda: self._on_clicker_toggle(*get_clicker_settings())
            )
            self._hotkey_ids.append(h_id)
        except Exception as e:
            print(f"Failed to register clicker toggle hotkey: {e}")

        # Hold mode hotkey (press/release)
        try:
            h_id = keyboard.on_press_key(
                hotkey_clicker_hold,
                lambda e: self._on_clicker_hold_start(*get_clicker_settings())
            )
            self._hook_ids.append(h_id)
            h_id2 = keyboard.on_release_key(
                hotkey_clicker_hold,
                lambda e: self._on_clicker_hold_stop()
            )
            self._hook_ids.append(h_id2)
        except Exception as e:
            print(f"Failed to register clicker hold hotkey: {e}")

        # Key loop hotkey
        try:
            h_id = keyboard.add_hotkey(hotkey_key_loop, self._on_key_loop_toggle)
            self._hotkey_ids.append(h_id)
        except Exception as e:
            print(f"Failed to register key loop hotkey {hotkey_key_loop}: {e}")

                # Hideout hotkey (一键回藏身处)
        hideout_enabled = settings.get("hideout_enabled", False)
        hideout_hotkey = settings.get("hideout_hotkey", "F2")
        if hideout_enabled:
            try:
                h_id = keyboard.add_hotkey(hideout_hotkey, self._on_hideout)
                self._hotkey_ids.append(h_id)
                print(f"[调试] F2 热键已注册")  # 再加一行确认注册成功
            except Exception as e:
                print(f"Failed to register hideout hotkey {hideout_hotkey}: {e}")

    def cleanup(self):
        """Unregister only our hotkeys — don't use unhook_all() as it breaks keyboard lib state."""
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

    def set_coord_target(self, target: str):
        self._coord_target = target

    def _check_foreground(self) -> bool:
        """Check if foreground window matches target process. True = allowed."""
        from foreground import is_foreground_process
        target = self.main_window.settings.get("target_process", "")
        return is_foreground_process(target)

    def _on_start(self):
        if not self._check_foreground():
            return
        # 热键回调在键盘监听线程，用信号安全地 defer 到主线程
        self.main_window._hotkey_start.emit()

    def _on_stop(self):
        self.main_window._hotkey_stop.emit()

    def _on_set_coord(self):
        if self._coord_target is None:
            return
        x, y = pyautogui.position()
        target = self._coord_target
        self._coord_target = None
        # Use signal to safely defer coord update to main thread
        self.main_window._hotkey_coord.emit(target, x, y)

    def _on_clicker_toggle(self, button: str, interval: int):
        if not self._check_foreground():
            return
        print(f"[连点器] toggle: button={button} interval={interval} currently_running={self.main_window.clicker.is_running}")
        self.main_window.clicker.start_toggle(button, interval)

    def _on_clicker_hold_start(self, button: str, interval: int):
        if not self._check_foreground():
            return
        if not self._clicker_held:
            self._clicker_held = True
            self.main_window.clicker.start_hold(button, interval)

    def _on_clicker_hold_stop(self):
        if self._clicker_held:
            self._clicker_held = False
            self.main_window.clicker.stop_hold()

    def _on_key_loop_toggle(self):
        if not self._check_foreground():
            return
        self.main_window._hotkey_key_loop.emit()

    def _on_hideout(self):
        if not self._check_foreground():
            return
        delay_ms = self.main_window.settings.get("delay_ms", 33)
        _execute_hideout(delay_ms)

    @staticmethod
    def _check_conflicts(hotkeys: dict[str, str]) -> list[str]:
        """检查所有热键冲突，返回冲突描述字符串列表（无冲突返回空列表）。"""
        conflicts = []                                       # 改动：收集全部冲突
        seen = {}
        for name, key in hotkeys.items():
            if not key:
                continue
            key_lower = key.lower()
            if key_lower in seen:
                # 改动：格式化为可读冲突描述并加入列表
                conflict_msg = f"'{key}' 同时绑定到了「{seen[key_lower]}」和「{name}」"
                conflicts.append(conflict_msg)
            else:
                seen[key_lower] = name
        return conflicts


def main():
    os.environ['PYAUTOGUI_PAUSE'] = '0'

    app = QApplication(sys.argv)
    app.setStyle('Fusion')

    from PySide6.QtGui import QPalette, QColor
    palette = QPalette()
    palette.setColor(QPalette.Window, QColor(218, 232, 245))
    palette.setColor(QPalette.WindowText, QColor(30, 42, 58))
    palette.setColor(QPalette.Base, QColor(218, 232, 245))
    palette.setColor(QPalette.Text, QColor(30, 42, 58))
    palette.setColor(QPalette.Button, QColor(200, 223, 240))
    palette.setColor(QPalette.ButtonText, QColor(30, 42, 58))
    palette.setColor(QPalette.Highlight, QColor(26, 111, 181))
    app.setPalette(palette)

    # 设置应用图标（影响任务栏和标题栏）
    icon_path = PROJECT_ROOT / "poe.ico"
    if icon_path.exists():
        app.setWindowIcon(QIcon(str(icon_path)))

    window = MainWindow()

    # Windows: 用 ctypes 直接设置窗口图标，确保任务栏也生效
    try:
        import ctypes
        hwnd = int(window.winId())
        hicon = ctypes.windll.user32.LoadImageW(0, str(icon_path), 1, 0, 0, 0x10)  # IMAGE_ICON, LR_LOADFROMFILE
        if hicon:
            # WM_SETICON = 0x0080, ICON_SMALL = 0, ICON_BIG = 1
            ctypes.windll.user32.SendMessageW(hwnd, 0x0080, 0, hicon)
            ctypes.windll.user32.SendMessageW(hwnd, 0x0080, 1, hicon)
    except Exception as e:
        print(f"[图标] 设置失败: {e}")

    # Setup hotkeys
    hotkey_mgr = HotkeyManager(window)
    window.hotkey_mgr = hotkey_mgr  # Store on window so MainWindow can access

    # Connect coordinate target selection (via coord button, NOT name label)
    for key, card in window.config_tab.currency_cards.items():
        card.coord_clicked.connect(lambda k=key: hotkey_mgr.set_coord_target(k))

    # Connect item coord button to hotkey target
    window.config_tab.item_coord_btn.clicked.connect(lambda: hotkey_mgr.set_coord_target("item"))
    window.config_tab.item_clear_btn.clicked.connect(lambda: save_coordinates(window.coordinates))

    hotkey_mgr.setup()

    # 回藏身处开关改变时立刻重新注册热键
    window.settings_tab.hideout_toggle.toggled.connect(
        lambda checked: (
            window.settings.update({"hideout_enabled": checked}),
            hotkey_mgr.setup()
        )
    )
    # 下拉切换进程时立刻重新注册热键
    window.settings_tab.process_combo.currentIndexChanged.connect(
        window._save_all
    )

    # 启动后台网络线程（签到 + 广告）
    bridge = BackgroundBridge()
    bridge.ads_data.connect(window._ads_data)
    bridge.version_outdated.connect(lambda url: _show_update_dialog(url))
    t = threading.Thread(target=_send_daily_ping, args=(bridge,), daemon=True)
    t.start()

    window.show()

    sys.exit(app.exec())


if __name__ == "__main__":
    main()