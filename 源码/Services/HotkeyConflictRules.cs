using ShiKe.Host;

namespace ShiKe.Services;

/// <summary>
/// 热键与自动化输入槽位键的冲突判定，与 HotkeyParser 同层，供各工具复用。
/// </summary>
public static class HotkeyConflictRules
{
    /// <summary>
    /// 输入槽位键与全局热键、其他槽位之间的冲突检测。
    /// 键盘槽位发送的合成按键仍可能触发 RegisterHotKey Toggle，所以同一虚拟键要忽略修饰键比较；
    /// 鼠标侧键则由低级钩子过滤合成事件，可按完整组合键区分。
    /// </summary>
    public static IReadOnlyList<string> ValidateSlotKeyConflicts(
        IReadOnlyList<(string Name, string Key, HotkeyMode Mode)> hotkeys,
        IReadOnlyList<(string Name, string Key)> slots)
    {
        var errors = new List<string>();
        var parsedSlots = new List<(string Name, string Key, ParsedHotkey Trigger)>();
        foreach (var slot in slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Key)) continue;
            var trigger = HotkeyParser.ParseHold(slot.Key);
            if (trigger is null)
            {
                errors.Add($"输入槽位“{slot.Name}”的按键“{slot.Key}”无法识别");
                continue;
            }
            parsedSlots.Add((slot.Name, slot.Key, trigger.Value));
        }

        foreach (var slot in parsedSlots)
        {
            foreach (var hotkey in hotkeys)
            {
                if (string.IsNullOrWhiteSpace(hotkey.Key)) continue;
                var trigger = HotkeyParser.ParseForMode(hotkey.Key, hotkey.Mode);
                if (trigger is not ParsedHotkey hotkeyTrigger) continue;
                var sameTrigger = hotkeyTrigger == slot.Trigger;
                var injectedKeyboardCanTriggerToggle = !slot.Trigger.IsMouseButton &&
                    hotkey.Mode == HotkeyMode.Toggle && hotkeyTrigger.Vk == slot.Trigger.Vk;
                if (!sameTrigger && !injectedKeyboardCanTriggerToggle) continue;
                errors.Add($"输入槽位“{slot.Name}”的按键“{slot.Key}”与「{hotkey.Name}」热键冲突");
            }
        }

        foreach (var group in parsedSlots.GroupBy(slot => slot.Trigger).Where(group => group.Count() > 1))
        {
            var names = string.Join("、", group.Select(slot => slot.Name));
            errors.Add($"输入槽位{names}使用了相同的按键“{group.First().Key}”");
        }

        return errors;
    }
}
