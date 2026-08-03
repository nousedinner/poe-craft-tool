"""Data models for PoE Craft Tool."""
from dataclasses import dataclass, field
from enum import IntEnum
from typing import Optional


class Mode(IntEnum):
    SINGLE = 1       # 单通货
    ALT_AUG = 2      # 改造+增幅
    ALT_AUG_REGAL = 3  # 改造+增幅+富豪


class CurrencyType:
    ALTERATION = "alteration"
    AUGMENTATION = "augmentation"
    ALCHEMY = "alchemy"
    CHAOS = "chaos"
    EXALTED = "exalted"
    DIVINE = "divine"
    REGAL = "regal"
    SCOURING = "scouring"
    TRANSMUTATION = "transmutation"
    CUSTOM = "custom"

    LABELS = {
        ALTERATION: "改造石",
        AUGMENTATION: "增幅石",
        ALCHEMY: "点金石",
        CHAOS: "混沌石",
        EXALTED: "崇高石",
        DIVINE: "神圣石",
        REGAL: "富豪石",
        SCOURING: "重铸石",
        TRANSMUTATION: "蜕变石",
        CUSTOM: "自定义通货",
    }

    # Currencies used in each mode
    MODE_CURRENCIES = {
        Mode.SINGLE: [ALTERATION, CHAOS, CUSTOM],
        Mode.ALT_AUG: [ALTERATION, AUGMENTATION, ALCHEMY, SCOURING],
        Mode.ALT_AUG_REGAL: [ALTERATION, AUGMENTATION, REGAL, SCOURING, TRANSMUTATION, EXALTED],
    }

    # Currency action type: "right_click_only" or "right_click_item"
    # Most currencies: right-click then left-click item
    # Augmentation in Mode 2/3: after releasing shift, re-right-click
    ACTION_RIGHT_CLICK = "right_click"
    ACTION_RIGHT_CLICK_ITEM = "right_click_item"


@dataclass
class AffixRule:
    """A single affix rule - text substring match."""
    text: str  # The affix text pattern (e.g., "急冻的" or "冰霜伤害")

    def matches(self, affix_name: str, affix_desc: str) -> bool:
        """Check if this rule matches an item's affix name or description."""
        return self.text in affix_name or self.text in affix_desc


@dataclass
class CraftRules:
    """Complete crafting rule set."""
    mode: Mode = Mode.SINGLE
    single_currency: str = CurrencyType.ALTERATION  # For Mode 1

    primary_affixes: list[AffixRule] = field(default_factory=list)
    primary_hit_count: int = 1  # How many primary must match

    secondary_affixes: list[AffixRule] = field(default_factory=list)
    secondary_hit_count: int = 0  # How many secondary must match

    exclude_affixes: list[AffixRule] = field(default_factory=list)

    def validate(self) -> tuple[bool, str]:
        """Validate rule constraints."""
        # Check hit count vs pool size
        if self.primary_hit_count > 0 and len(self.primary_affixes) == 0:
            return False, f"主词缀命中数要求 {self.primary_hit_count}，但主词缀池为空"
        if self.primary_hit_count > len(self.primary_affixes):
            return False, f"主词缀命中数 {self.primary_hit_count} 超过主词缀池数量 {len(self.primary_affixes)}"
        if self.secondary_hit_count > 0 and len(self.secondary_affixes) == 0:
            return False, f"次级词缀命中数要求 {self.secondary_hit_count}，但次级词缀池为空"
        if self.secondary_hit_count > len(self.secondary_affixes):
            return False, f"次级词缀命中数 {self.secondary_hit_count} 超过次级词缀池数量 {len(self.secondary_affixes)}"

        total = self.primary_hit_count + self.secondary_hit_count
        if self.mode == Mode.SINGLE:
            pass  # No limit
        elif self.mode == Mode.ALT_AUG:
            if total > 2:
                return False, "Mode 2 总词缀命中数不能超过 2"
        elif self.mode == Mode.ALT_AUG_REGAL:
            if total > 3:
                return False, "Mode 3 总词缀命中数不能超过 3"
        return True, ""


@dataclass
class AffixCheckResult:
    """Result of checking affixes against rules."""
    primary_hits: int = 0
    secondary_hits: int = 0
    has_exclude: bool = False
    matched_primary: list[str] = field(default_factory=list)
    matched_secondary: list[str] = field(default_factory=list)
    matched_exclude: list[str] = field(default_factory=list)

    @property
    def total_hits(self) -> int:
        return self.primary_hits + self.secondary_hits

    def meets_final_rules(self, rules: CraftRules) -> bool:
        """Check if this result meets the final success criteria."""
        if self.has_exclude:
            return False
        if self.primary_hits < rules.primary_hit_count:
            return False
        if self.secondary_hits < rules.secondary_hit_count:
            return False
        return True

    def has_any_primary_or_secondary(self) -> bool:
        """Has at least one primary or secondary hit."""
        return self.primary_hits > 0 or self.secondary_hits > 0

    def has_primary(self) -> bool:
        return self.primary_hits > 0
