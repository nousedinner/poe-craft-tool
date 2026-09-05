using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Settings;

public sealed record HotkeySettings(
    string CraftStart,
    string CraftStop,
    string Coordinate,
    string ClickerToggle,
    string ClickerHold,
    string KeyLoop,
    string Hideout)
{
    public HotkeySettings Normalize() => this with
    {
        CraftStart = CraftStart.Trim(),
        CraftStop = CraftStop.Trim(),
        Coordinate = Coordinate.Trim(),
        ClickerToggle = ClickerToggle.Trim(),
        ClickerHold = ClickerHold.Trim(),
        KeyLoop = KeyLoop.Trim(),
        Hideout = Hideout.Trim(),
    };
}

public sealed record SettingsDraft(
    HotkeySettings Hotkeys,
    string TargetProcess,
    bool AutoDetectPoe,
    bool CraftSoundEnabled,
    bool CraftPopupEnabled,
    string SelectedSound,
    bool ClickerNotificationsEnabled,
    bool KeyLoopNotificationsEnabled,
    bool HideoutEnabled,
    string HideoutCommand)
{
    public SettingsDraft Normalize() => this with
    {
        Hotkeys = Hotkeys.Normalize(),
        TargetProcess = TargetProcess.Trim(),
        SelectedSound = string.IsNullOrWhiteSpace(SelectedSound)
            ? SettingsDefaults.SelectedSound
            : SelectedSound.Trim(),
        HideoutCommand = string.IsNullOrWhiteSpace(HideoutCommand)
            ? SettingsDefaults.HideoutCommand
            : HideoutCommand.Trim(),
    };
}

public sealed record SettingsApplyResult(bool Success, string Message)
{
    public static SettingsApplyResult Ok(string message = "设置已保存并生效") => new(true, message);
    public static SettingsApplyResult Fail(string message) => new(false, message);
}

/// <summary>不触发 Win32 注册的纯设置校验，供页面和内部测试共用。</summary>
public static class SettingsValidation
{
    public static IReadOnlyList<string> Validate(SettingsDraft draft)
    {
        var errors = ValidateHotkeys(draft.Hotkeys).ToList();

        if (!IsSingleFileName(draft.TargetProcess))
            errors.Add("目标进程只能填写进程文件名，不能包含路径");
        if (!IsSingleFileName(draft.SelectedSound))
            errors.Add("音效只能选择 sounds 目录中的文件名");
        if (draft.HideoutCommand.Any(char.IsControl))
            errors.Add("回城命令不能包含换行或其他控制字符");
        if (draft.HideoutCommand.Length > 200)
            errors.Add("回城命令不能超过 200 个字符");

        return errors;
    }

    public static IReadOnlyList<string> ValidateHotkeys(HotkeySettings hotkeys)
    {
        var entries = new (string Name, string Key, HotkeyMode Mode)[]
        {
            ("启动洗装", hotkeys.CraftStart, HotkeyMode.Toggle),
            ("停止洗装", hotkeys.CraftStop, HotkeyMode.Toggle),
            ("坐标录制", hotkeys.Coordinate, HotkeyMode.Toggle),
            ("连点器切换", hotkeys.ClickerToggle, HotkeyMode.Toggle),
            ("连点器按住", hotkeys.ClickerHold, HotkeyMode.Hold),
            ("按键循环", hotkeys.KeyLoop, HotkeyMode.Toggle),
            ("一键回城", hotkeys.Hideout, HotkeyMode.Toggle),
        };

        var parsed = new List<(string Name, string Key, ParsedHotkey Trigger)>();
        var errors = new List<string>();
        foreach (var entry in entries)
        {
            var trigger = entry.Mode == HotkeyMode.Hold
                ? HotkeyParser.ParseHold(entry.Key)
                : HotkeyParser.Parse(entry.Key);
            if (trigger is null)
            {
                errors.Add($"{entry.Name}热键“{entry.Key}”无法识别");
                continue;
            }
            parsed.Add((entry.Name, entry.Key, trigger.Value));
        }

        foreach (var group in parsed.GroupBy(entry => entry.Trigger).Where(group => group.Count() > 1))
        {
            var names = string.Join("、", group.Select(entry => entry.Name));
            errors.Add($"热键“{group.First().Key}”发生冲突：{names}");
        }
        return errors;
    }

    private static bool IsSingleFileName(string value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        return value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
    }
}

/// <summary>
/// 热键重注册的可回滚事务。候选注册失败时立即恢复旧字段并重新注册旧集合，
/// 避免设置页一次错误输入让全部热键失效。
/// </summary>
internal static class HotkeySettingsTransaction
{
    public static SettingsApplyResult TryApply(
        HotkeySettings previous,
        HotkeySettings candidate,
        Action<HotkeySettings> apply,
        Func<IReadOnlyList<string>> registerCurrent)
    {
        apply(candidate);
        IReadOnlyList<string> issues;
        try
        {
            issues = registerCurrent();
        }
        catch (Exception ex)
        {
            issues = [$"重新注册发生异常：{ex.Message}"];
        }

        if (issues.Count == 0)
            return SettingsApplyResult.Ok("热键已重新注册");

        apply(previous);
        IReadOnlyList<string> rollbackIssues;
        try
        {
            rollbackIssues = registerCurrent();
        }
        catch (Exception ex)
        {
            rollbackIssues = [$"恢复旧热键时发生异常：{ex.Message}"];
        }

        var message = "新热键未生效，已恢复修改前配置：\n" + string.Join("\n", issues);
        if (rollbackIssues.Count > 0)
            message += "\n\n警告：旧热键恢复也遇到问题：\n" + string.Join("\n", rollbackIssues);
        return SettingsApplyResult.Fail(message);
    }
}
