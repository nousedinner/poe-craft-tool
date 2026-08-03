"""Multi-key loop operator: press multiple keys at independent intervals.

Design: 10 pre-created daemon threads (one per slot), alive forever.
Each thread checks its slot's enabled/running flags. Toggle is instant, no join().
"""
import threading
import time
import keyboard
from dataclasses import dataclass


MAX_SLOTS = 10


@dataclass
class KeySlot:
    """A single key slot configuration."""
    enabled: bool = False
    key: str = ""
    delay: float = 1.0  # seconds between presses


class KeyOperator:
    """Manages multiple independent key-press loops."""

    def __init__(self):
        self._lock = threading.Lock()
        self._running_flags = [False] * MAX_SLOTS
        self._wake_events = [threading.Event() for _ in range(MAX_SLOTS)]
        self.press_counts: dict[str, int] = {}
        self._slots = [None] * MAX_SLOTS
        self._target_process = ""  # Foreground process filter

        self.on_status_update = None

        for i in range(MAX_SLOTS):
            t = threading.Thread(target=self._key_loop, args=(i,), daemon=True)
            t.start()

    def set_target_process(self, name: str):
        self._target_process = name

    @property
    def is_running(self) -> bool:
        return any(self._running_flags)

    def start(self, slots: list):
        """Start pressing all enabled keys."""
        with self._lock:
            self.press_counts = {}
            for i, slot in enumerate(slots[:MAX_SLOTS]):
                self._slots[i] = slot
                self._running_flags[i] = slot.enabled and bool(slot.key)
                if slot.enabled and slot.key:
                    self.press_counts[slot.key] = 0
            for i in range(len(slots), MAX_SLOTS):
                self._slots[i] = None
                self._running_flags[i] = False

            # Wake up all threads to check their new state
            for event in self._wake_events:
                event.set()

        if self.on_status_update:
            self.on_status_update(True, dict(self.press_counts))

    def stop(self):
        """Stop all key loops. Instant, no blocking."""
        with self._lock:
            for i in range(MAX_SLOTS):
                self._running_flags[i] = False
            # Wake up all threads so they can exit their loops
            for event in self._wake_events:
                event.set()

        if self.on_status_update:
            self.on_status_update(False, dict(self.press_counts))

    def update_slot(self, index: int, enabled: bool):
        """Update a single slot's enabled state at runtime."""
        if 0 <= index < MAX_SLOTS:
            with self._lock:
                self._running_flags[index] = enabled and (
                    self._slots[index] is not None and bool(self._slots[index].key)
                )
                self._wake_events[index].set()

    def _key_loop(self, index: int):
        """Runs forever. When slot is enabled and running, presses the key."""
        while True:
            # Only wait when NOT running (idle gate)
            if not self._running_flags[index]:
                self._wake_events[index].wait()
                self._wake_events[index].clear()

            slot = self._slots[index]
            if not self._running_flags[index] or not slot or not slot.key:
                continue  # Go back to waiting

            # Foreground process check
            if self._target_process:
                try:
                    from foreground import is_foreground_process
                    if not is_foreground_process(self._target_process):
                        time.sleep(0.1)
                        continue
                except Exception:
                    pass

            try:
                keyboard.press_and_release(slot.key)
                key = slot.key
                with self._lock:
                    self.press_counts[key] = self.press_counts.get(key, 0) + 1
            except Exception:
                pass

            # Sleep for the full delay, but wake up early if stopped
            deadline = time.monotonic() + slot.delay
            while self._running_flags[index] and time.monotonic() < deadline:
                # Sleep in small chunks to be responsive to stop signals
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                # Wait up to 0.1s or until woken
                self._wake_events[index].wait(timeout=min(0.1, remaining))
                self._wake_events[index].clear()