"""Persistent storage for rules, coordinates, and settings."""
import json
import os
from pathlib import Path
from models import Mode, CurrencyType, AffixRule, CraftRules

import sys

if getattr(sys, 'frozen', False):
    # 打包后：data 目录在 exe 所在目录下
    DATA_DIR = Path(sys.executable).parent / "data"
else:
    # 源码运行：data 目录在 storage.py 的上级目录下
    DATA_DIR = Path(__file__).parent / "data"


def _ensure_data_dir():
    DATA_DIR.mkdir(exist_ok=True)


def _get_path(filename: str) -> Path:
    _ensure_data_dir()
    return DATA_DIR / filename


# --- Coordinates ---

def load_coordinates() -> dict:
    """Load currency coordinates. Returns {currency_key: (x, y) or None}."""
    path = _get_path("coordinates.json")
    if path.exists():
        with open(path, 'r', encoding='utf-8') as f:
            data = json.load(f)
            # Convert lists back to tuples
            return {k: tuple(v) if v else None for k, v in data.items()}
    return {key: None for key in CurrencyType.LABELS}


def save_coordinates(coords: dict):
    """Save currency coordinates."""
    path = _get_path("coordinates.json")
    # Convert tuples to lists for JSON
    data = {k: list(v) if v else None for k, v in coords.items()}
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


# --- Rules ---

def save_rules(rules: CraftRules):
    """Save craft rules."""
    path = _get_path("rules.json")
    data = {
        "mode": int(rules.mode),
        "single_currency": rules.single_currency,
        "primary_affixes": [a.text for a in rules.primary_affixes],
        "primary_hit_count": rules.primary_hit_count,
        "secondary_affixes": [a.text for a in rules.secondary_affixes],
        "secondary_hit_count": rules.secondary_hit_count,
        "exclude_affixes": [a.text for a in rules.exclude_affixes],
    }
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def load_rules() -> CraftRules:
    """Load craft rules."""
    path = _get_path("rules.json")
    if not path.exists():
        return CraftRules()

    with open(path, 'r', encoding='utf-8') as f:
        data = json.load(f)

    rules = CraftRules(
        mode=Mode(data.get("mode", 1)),
        single_currency=data.get("single_currency", CurrencyType.ALTERATION),
        primary_hit_count=data.get("primary_hit_count", 1),
        secondary_hit_count=data.get("secondary_hit_count", 0),
    )

    for a in data.get("primary_affixes", []):
        text = a if isinstance(a, str) else a.get("text", "")
        if text:
            rules.primary_affixes.append(AffixRule(text=text))

    for a in data.get("secondary_affixes", []):
        text = a if isinstance(a, str) else a.get("text", "")
        if text:
            rules.secondary_affixes.append(AffixRule(text=text))

    for a in data.get("exclude_affixes", []):
        text = a if isinstance(a, str) else a.get("text", "")
        if text:
            rules.exclude_affixes.append(AffixRule(text=text))

    return rules


# --- Settings ---

def load_settings() -> dict:
    """Load app settings."""
    path = _get_path("settings.json")
    defaults = {
        "hotkey_start": "F5",
        "hotkey_stop": "F6",
        "hotkey_set_coord": "F7",
        "delay_ms": 33,
        "sound_enabled": True,
        "popup_enabled": True,
        "custom_sound": None,  # filename in sounds/ folder
        "clipboard_unchanged_threshold": 10,
        "clicker_hotkey": "F8",
        "clicker_hold_hotkey": "F11",
        "clicker_interval_ms": 33,
        "clicker_button": "left",
        "clicker_mode": "toggle",
        "selected_sound": "default_ding.wav",
        "target_process": "",
        "key_loop_hotkey": "F9",
        "key_loop_slots": [],
        "hideout_hotkey": "F2",
        "hideout_enabled": False,
    }
    if path.exists():
        with open(path, 'r', encoding='utf-8') as f:
            data = json.load(f)
        defaults.update(data)
    return defaults


def save_settings(settings: dict):
    """Save app settings."""
    path = _get_path("settings.json")
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(settings, f, ensure_ascii=False, indent=2)


# --- Presets ---

def _presets_dir() -> Path:
    """Get the presets directory, create if not exists."""
    p = DATA_DIR / "presets"
    p.mkdir(exist_ok=True)
    return p


def list_presets() -> list[str]:
    """List available preset names (without .json extension)."""
    presets = []
    for f in _presets_dir().glob("*.json"):
        presets.append(f.stem)
    return sorted(presets)


def load_preset(name: str) -> dict | None:
    """Load a preset by name. Returns dict or None if not found."""
    path = _presets_dir() / f"{name}.json"
    if not path.exists():
        return None
    with open(path, 'r', encoding='utf-8') as f:
        return json.load(f)


def save_preset(name: str, data: dict):
    """Save a preset with the given name."""
    path = _presets_dir() / f"{name}.json"
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def delete_preset(name: str):
    """Delete a preset by name."""
    path = _presets_dir() / f"{name}.json"
    if path.exists():
        path.unlink()