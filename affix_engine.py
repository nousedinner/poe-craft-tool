# -*- coding: utf-8 -*-
"""Affix parsing and rule matching engine for Chinese PoE client."""
import re
from models import AffixRule, AffixCheckResult, CraftRules


def parse_item_text(clipboard_text: str) -> list[dict]:
    """Parse PoE Ctrl+Alt+C item text from Chinese client into individual affixes.

    Chinese PoE clipboard format uses { 前缀属性/后缀属性 } markers:
    { 前缀属性 "急冻的" (等阶：6) — 伤害, 元素, 冰霜, 攻击 }
    该装备附加 52(41-55) - 81(81-95) 基础冰霜伤害

    Returns list of dicts with: name, type (前缀/后缀), tier, description
    """
    lines = clipboard_text.strip().split('\n')
    affixes = []
    i = 0

    while i < len(lines):
        line = lines[i].strip()
        # Check if this is an affix header: starts with { and contains 属性
        if line.startswith('{') and '属性' in line:
            # Extract affix name from quotes
            name_match = re.search(r'"([^"]+)"', line)
            name = name_match.group(1) if name_match else ""

            # Extract affix type (前缀 or 后缀)
            type_match = re.search(r'(前缀|后缀)', line)
            affix_type = type_match.group(1) if type_match else ""

            # Extract tier (等阶)
            tier_match = re.search(r'等阶[\uff1a:]\s*(\d+)', line)
            tier = int(tier_match.group(1)) if tier_match else 0

            # Next line(s) are the affix description
            desc_lines = []
            j = i + 1
            while j < len(lines):
                next_line = lines[j].strip()
                # Stop at next affix header, separator, or empty line
                if next_line.startswith('{') or next_line.startswith('--------') or not next_line:
                    break
                # Skip parenthetical notes (like (吸取的魔力会随时间逐渐回复...))
                if next_line.startswith('(') and next_line.endswith(')'):
                    j += 1
                    continue
                desc_lines.append(next_line)
                j += 1

            description = ' '.join(desc_lines) if desc_lines else ""
            affixes.append({
                'name': name,
                'type': affix_type,
                'tier': tier,
                'description': description,
            })
            i = j
        else:
            i += 1

    return affixes


def check_affixes(item_affixes: list[dict], rules: CraftRules) -> AffixCheckResult:
    """Check item affixes against craft rules using text substring matching."""
    result = AffixCheckResult()

    # Check exclude first - early exit on any match
    for exclude_rule in rules.exclude_affixes:
        for affix in item_affixes:
            if exclude_rule.matches(affix['name'], affix['description']):
                result.has_exclude = True
                result.matched_exclude.append(affix['name'])
                return result

    # Check primary
    for primary_rule in rules.primary_affixes:
        for affix in item_affixes:
            if primary_rule.matches(affix['name'], affix['description']):
                if affix['name'] not in result.matched_primary:
                    result.matched_primary.append(affix['name'])
                    result.primary_hits += 1
                break  # Each rule matches at most once

    # Check secondary
    for secondary_rule in rules.secondary_affixes:
        for affix in item_affixes:
            if secondary_rule.matches(affix['name'], affix['description']):
                if affix['name'] not in result.matched_secondary:
                    result.matched_secondary.append(affix['name'])
                    result.secondary_hits += 1
                break

    return result


def get_item_affix_count(item_text: str) -> int:
    """Count the number of affixes on an item from clipboard text."""
    affixes = parse_item_text(item_text)
    return len(affixes)
