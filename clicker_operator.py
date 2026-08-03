"""Auto-clicker operator: mouse click automation.

Design: ONE daemon thread, alive forever. Flipping _running flag controls it.
No thread creation/destruction, no join(), no race conditions.
"""
import threading
import time
import pyautogui


class ClickerOperator:

    def __init__(self):
        self._running = False
        self._lock = threading.Lock()
        self._wake_event = threading.Event()
        self.click_count = 0
        self._click_fn = pyautogui.click
        self._interval = 0.033
        self._target_process = ""  # Foreground process filter

        # ONE thread, starts immediately, never dies
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()

        self.on_status_update = None

    def set_target_process(self, name: str):
        self._target_process = name

    @property
    def is_running(self) -> bool:
        return self._running

    def start_toggle(self, button: str, interval_ms: int):
        """Toggle: press once starts, press again stops."""
        with self._lock:
            if self._running:
                self._running = False
                self._wake_event.set()  # Wake thread to notice stop
                print("[连点器] 停止")
                return
            # Start
            self._click_fn = pyautogui.click if button == "left" else pyautogui.rightClick
            self._interval = interval_ms / 1000.0
            self.click_count = 0
            self._running = True
            self._wake_event.set()  # Wake thread to start clicking
            print(f"[连点器] 启动: button={button} interval={interval_ms}ms")

        if self.on_status_update:
            self.on_status_update(True, 0)

    def start_hold(self, button: str, interval_ms: int):
        """Hold: starts clicking while key is held."""
        with self._lock:
            if self._running:
                return
            self._click_fn = pyautogui.click if button == "left" else pyautogui.rightClick
            self._interval = interval_ms / 1000.0
            self.click_count = 0
            self._running = True
            self._wake_event.set()  # Wake thread to start clicking

        if self.on_status_update:
            self.on_status_update(True, 0)

    def stop_hold(self):
        """Hold: stops clicking when key is released."""
        with self._lock:
            self._running = False
            self._wake_event.set()  # Wake thread to notice stop

        if self.on_status_update:
            self.on_status_update(False, self.click_count)

    def stop(self):
        """Force stop."""
        with self._lock:
            self._running = False
            self._wake_event.set()  # Wake thread to notice stop

        if self.on_status_update:
            self.on_status_update(False, self.click_count)

    def _loop(self):
        """Runs forever. When _running is True, clicks. When False, sleeps."""
        while True:
            # Only wait when NOT running (idle gate)
            if not self._running:
                self._wake_event.wait()
                self._wake_event.clear()
                continue

            # Emergency stop: mouse in top-left corner
            try:
                x, y = pyautogui.position()
                if x <= 1 and y <= 1:
                    with self._lock:
                        self._running = False
                    continue
            except Exception:
                pass

            # Foreground process check
            if self._target_process:
                try:
                    from foreground import is_foreground_process
                    if not is_foreground_process(self._target_process):
                        time.sleep(0.1)
                        continue
                except Exception:
                    pass

            self._click_fn()
            self.click_count += 1

            # Sleep for the full interval, but wake up early if stopped
            deadline = time.monotonic() + self._interval
            while self._running and time.monotonic() < deadline:
                # Sleep in small chunks to be responsive to stop signals
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                # Wait up to 0.01s or until woken
                self._wake_event.wait(timeout=min(0.01, remaining))
                self._wake_event.clear()