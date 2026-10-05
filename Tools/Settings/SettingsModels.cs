using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Tools.Hideout;

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

    internal IReadOnlyList<(string Id, string Name, string Key, HotkeyMode Mode)> GetEntries() =>
    [
        ("craft_start", "启动洗装", CraftStart, HotkeyMode.Toggle),
        ("craft_stop", "停止洗装", CraftStop, HotkeyMode.Toggle),
        ("coordinate", "坐标录制", Coordinate, HotkeyMode.Toggle),
        ("clicker_toggle", "连点器切换", ClickerToggle, HotkeyMode.Toggle),
        ("clicker_hold", "连点器按住", ClickerHold, HotkeyMode.Hold),
        ("keyloop", "按键循环", KeyLoop, HotkeyMode.Toggle),
        ("hideout", "一键回城", Hideout, HotkeyMode.Toggle),
    ];

    internal HotkeySettings WithKey(string id, string key) => id switch
    {
        "craft_start" => this with { CraftStart = key },
        "craft_stop" => this with { CraftStop = key },
        "coordinate" => this with { Coordinate = key },
        "clicker_toggle" => this with { ClickerToggle = key },
        "clicker_hold" => this with { ClickerHold = key },
        "keyloop" => this with { KeyLoop = key },
        "hideout" => this with { Hideout = key },
        _ => throw new ArgumentException("未知的热键项", nameof(id)),
    };
}

/// <summary>当前修改项优先，按真实触发器清空其他冲突绑定，包括尚未启用的工具。</summary>
internal static class HotkeyConflictResolver
{
    public static (HotkeySettings Hotkeys, IReadOnlyList<string> ClearedNames) Resolve(
        HotkeySettings requested, string preferredId)
    {
        var candidate = requested.Normalize();
        var entries = candidate.GetEntries();
        var preferred = entries.Single(entry => entry.Id == preferredId);
        var trigger = preferred.Mode == HotkeyMode.Hold
            ? HotkeyParser.ParseHold(preferred.Key)
            : HotkeyParser.Parse(preferred.Key);
        var clearedNames = new List<string>();
        if (trigger is null) return (candidate, clearedNames);

        foreach (var entry in entries.Where(entry => entry.Id != preferredId))
        {
            var other = entry.Mode == HotkeyMode.Hold
                ? HotkeyParser.ParseHold(entry.Key)
                : HotkeyParser.Parse(entry.Key);
            if (other != trigger) continue;
            candidate = candidate.WithKey(entry.Id, string.Empty);
            clearedNames.Add(entry.Name);
        }
        return (candidate, clearedNames);
    }
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
    string HideoutCommand,
    bool CraftEnabled,
    bool ClickerEnabled,
    bool KeyLoopEnabled)
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
        if (HideoutTool.ValidateCommand(draft.HideoutCommand) is { } commandError)
            errors.Add(commandError);
        if (draft.CraftEnabled && draft.ClickerEnabled &&
            HotkeyParser.ParseHold(draft.Hotkeys.ClickerHold) is { Modifiers: 0, Vk: KeyCode.Control })
            errors.Add("连点器按住热键为 Ctrl 时，连点器与洗词缀不能同时启用");

        return errors;
    }

    public static IReadOnlyList<string> ValidateHotkeys(HotkeySettings hotkeys)
    {
        var parsed = new List<(string Name, string Key, ParsedHotkey Trigger)>();
        var errors = new List<string>();
        foreach (var entry in hotkeys.GetEntries())
        {
            if (string.IsNullOrWhiteSpace(entry.Key)) continue;
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
        IReadOnlyList<string> issues;
        try
        {
            apply(candidate);
            issues = registerCurrent();
        }
        catch (Exception ex)
        {
            issues = [$"重新注册发生异常：{ex.Message}"];
        }

        if (issues.Count == 0)
            return SettingsApplyResult.Ok("热键已重新注册");

        IReadOnlyList<string> rollbackIssues;
        try
        {
            apply(previous);
            rollbackIssues = registerCurrent();
        }
        catch (Exception ex)
        {
            rollbackIssues = [$"恢复旧热键时发生异常：{ex.Message}"];
        }

        var message = (rollbackIssues.Count == 0
            ? "新热键未生效，已恢复修改前配置：\n"
            : "新热键未生效，已尝试恢复修改前配置：\n") + string.Join("\n", issues);
        if (rollbackIssues.Count > 0)
            message += "\n\n警告：旧热键恢复也遇到问题：\n" + string.Join("\n", rollbackIssues);
        return SettingsApplyResult.Fail(message);
    }
}
