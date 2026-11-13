using ShiKe.Host;

namespace ShiKe.Services;

/// <summary>
/// 热键与按键循环槽位键的冲突判定，与 HotkeyParser 同层。
/// 放在 Services 而不是 Tools.Settings：SettingsTool 注入 KeyLoopTool，
/// 若 KeyLoopTool 再回头读 Tools.Settings 的校验规则，两个工具就互相依赖了。
/// </summary>
public static class HotkeyConflictRules
{
    /// <summary>
    /// 按键循环槽位键与全局热键、以及其他槽位之间的冲突检测。
    /// 槽位键是“替玩家按下的键”，与热键共用同一物理按键时会互相打脸：
    /// 按下去既触发热键又发送按键，所以两个方向都必须判定。
    /// </summary>
    public static IReadOnlyList<string> ValidateSlotKeyConflicts(
        IReadOnlyList<HotkeyEntry> hotkeys, IReadOnlyList<(string Name, string Key)> slots)
    {
        var errors = new List<string>();
        var parsedSlots = new List<(string Name, string Key, ParsedHotkey Trigger)>();
        foreach (var slot in slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Key)) continue;
            var trigger = HotkeyParser.ParseHold(slot.Key);
            if (trigger is null)
            {
                errors.Add($"按键循环{slot.Name}的按键“{slot.Key}”无法识别");
                continue;
            }
            parsedSlots.Add((slot.Name, slot.Key, trigger.Value));
        }

        foreach (var slot in parsedSlots)
        {
            foreach (var hotkey in hotkeys)
            {
                if (string.IsNullOrWhiteSpace(hotkey.Key)) continue;
                var trigger = hotkey.Mode == HotkeyMode.Hold
                    ? HotkeyParser.ParseHold(hotkey.Key)
                    : HotkeyParser.Parse(hotkey.Key);
                if (trigger is null || trigger.Value != slot.Trigger) continue;
                errors.Add($"按键循环{slot.Name}的按键“{slot.Key}”与「{hotkey.Name}」热键冲突");
            }
        }

        foreach (var group in parsedSlots.GroupBy(slot => slot.Trigger).Where(group => group.Count() > 1))
        {
            var names = string.Join("、", group.Select(slot => slot.Name));
            errors.Add($"按键循环{names}使用了相同的按键“{group.First().Key}”");
        }

        return errors;
    }
}
