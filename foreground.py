"""Foreground window process detection (Windows only)."""
import ctypes
from ctypes import wintypes
import subprocess


def get_foreground_process_name() -> str | None:
    """Get the executable name of the foreground window's process."""
    try:
        hwnd = ctypes.windll.user32.GetForegroundWindow()
        if not hwnd:
            return None
        pid = wintypes.DWORD()
        ctypes.windll.user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        buf = ctypes.create_unicode_buffer(260)
        size = wintypes.DWORD(260)
        # PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid.value)
        if handle:
            ctypes.windll.kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size))
            ctypes.windll.kernel32.CloseHandle(handle)
            return buf.value.split('\\')[-1]
    except Exception:
        pass
    return None


def is_foreground_process(target_name: str) -> bool:
    """Check if the foreground window belongs to the target process.
    Returns True if target_name is empty (no filter)."""
    if not target_name:
        return True
    current = get_foreground_process_name()
    if current is None:
        return False
    return current.lower() == target_name.lower()


def list_process_names() -> list[str]:
    """Get sorted unique list of running process names."""
    try:
        result = subprocess.run(
            ['tasklist', '/FO', 'CSV', '/NH'],
            capture_output=True, text=True, timeout=5,
            creationflags=subprocess.CREATE_NO_WINDOW
        )
        names = set()
        for line in result.stdout.strip().split('\n'):
            line = line.strip()
            if line:
                name = line.split(',')[0].strip('"')
                names.add(name)
        return sorted(names)
    except Exception:
        return []
