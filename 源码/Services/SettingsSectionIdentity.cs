namespace ShiKe.Services;

/// <summary>旧平铺格式字段不得作为新工具分节，避免把旧文件误认成已分节配置。</summary>
internal static class SettingsSectionIdentity
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "schema_version", "hotkey_start", "hotkey_stop", "hotkey_set_coord", "target_process",
        "auto_detect_poe", "delay_ms", "sound_enabled", "popup_enabled", "clipboard_unchanged_threshold",
        "selected_sound", "custom_sound", "clicker_hotkey", "clicker_hold_hotkey", "clicker_interval_ms",
        "clicker_button", "clicker_mode", "key_loop_hotkey", "key_loop_slots", "hideout_hotkey", "hideout_enabled",
    };

    internal static void Validate(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id != id.Trim())
            throw new ArgumentException("工具 ID 不能为空或带首尾空格", nameof(id));
        if (Reserved.Contains(id))
            throw new ArgumentException($"工具 ID“{id}”与配置保留字段冲突", nameof(id));
    }
}
