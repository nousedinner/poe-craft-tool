using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShiKe.Services;
using ShiKe.Tools.KeyLoop;
using ShiKe.Tools.Settings;

internal static class UiAuditFixChecks
{
    public static void DelayParsingRejectsNonFinite()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "zh-CN", "en-US", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var text in new[] { "NaN", "Infinity", "-Infinity", "1e999", "-1e999", "", "abc",
                             double.NaN.ToString(CultureInfo.CurrentCulture),
                             double.PositiveInfinity.ToString(CultureInfo.CurrentCulture) })
                    Require(!KeyLoopPage.TryParseDelay(text, out _), $"{culture}不应接受间隔{text}");
                Require(KeyLoopPage.TryParseDelay("1.5", out var invariant) && invariant == 1.5,
                    "既有点号小数必须保持支持");
                Require(KeyLoopPage.TryParseDelay(1.5.ToString(CultureInfo.CurrentCulture), out var local) && local == 1.5,
                    "本地小数格式必须保持支持");
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    public static void StorageRejectsNonFiniteWithoutChangingFile()
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = Path.Combine(root, "audit-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new StorageService(directory);
            storage.SaveSettings(new JsonObject { ["keyloop"] = new JsonObject { ["slots"] = new JsonArray() } });
            var path = Path.Combine(directory, "settings.json");
            var original = File.ReadAllBytes(path);
            JsonNode?[] invalid = [JsonValue.Create(double.NaN), JsonValue.Create(double.PositiveInfinity),
                JsonValue.Create(double.NegativeInfinity), JsonValue.Create(float.NaN),
                JsonValue.Create(float.PositiveInfinity), JsonValue.Create("NaN"), JsonNode.Parse("1e999"), null];
            foreach (var value in invalid)
            {
                var rejected = false;
                try { storage.UpdateSettings(settings => settings["keyloop"] = Section(value)); }
                catch (StorageException) { rejected = true; }
                Require(rejected, "非法间隔必须作为可处理的配置错误拒绝");
                Require(File.ReadAllBytes(path).SequenceEqual(original), "拒绝保存不能改变原配置");
            }
            foreach (var value in new JsonNode[] { JsonValue.Create(2)!, JsonValue.Create(2.5)!,
                         JsonValue.Create(2.5m)!, JsonValue.Create(2.5f)!, JsonNode.Parse("2.5")! })
            {
                storage.UpdateSettings(settings => settings["keyloop"] = Section(value));
                Require(storage.LoadSettings()["keyloop"]!["slots"]![0]!["delay_s"] is not null,
                    "有限数值保存后必须可以正常加载");
            }
        }
        finally
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试目录越界");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static JsonObject Section(JsonNode? delay) => new()
    {
        ["slots"] = new JsonArray(new JsonObject { ["enabled"] = false, ["key"] = "a", ["delay_s"] = delay }),
    };

    public static void NumPadCaptureAndSettingsKeepIdentity()
    {
        for (var digit = 0; digit < 10; digit++)
        {
            var key = (Key)((int)Key.NumPad0 + digit);
            var expected = (ushort)(0x60 + digit);
            var settingName = SettingsPage.KeyName(key)!;
            var slotName = KeyLoopPage.KeyToName(key)!;
            Require(settingName == $"NumPad{digit}" && slotName == settingName, "两处捕获必须保留小键盘身份");
            Require(KeyCode.Parse(slotName) == expected, "发送键值必须为小键盘数字");
            Require(HotkeyParser.Parse("Ctrl+" + settingName)?.Vk == expected &&
                    HotkeyParser.ParseHold(settingName)?.Vk == expected, "普通与按住热键均需保留小键盘键值");
            Require(KeyCode.Parse(digit.ToString(CultureInfo.InvariantCulture)) == 0x30 + digit,
                "历史顶排数字的含义不能改变");
            var original = new KeyLoopTool();
            original.Slots[0].Key = slotName;
            original.Slots[0].Enabled = true;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) original.SaveSettings(writer);
            using var doc = JsonDocument.Parse(stream.ToArray());
            var restored = new KeyLoopTool();
            restored.LoadSettings(doc.RootElement);
            Require(restored.Slots[0].Key == slotName && KeyCode.Parse(restored.Slots[0].Key) == expected,
                "保存与重新加载后不能变成顶排数字");
        }
        Require(KeyCode.Parse("NumPad10") == 0 && KeyCode.Parse("NumPad-1") == 0, "范围外键名必须拒绝");
    }

    // 在现有真实WPF页面验证中调用，复用已隔离的宿主与测试窗口，不另开应用。
    public static void InvalidDelayEditKeepsPrevious(KeyLoopPage page, KeyLoopTool tool, StorageService storage)
    {
        var panel = (StackPanel)page.FindName("SlotPanel");
        var row = (Grid)((Border)panel.Children[1]).Child;
        var input = row.Children.OfType<TextBox>().Single();
        var previous = tool.Slots[1].DelaySeconds;
        foreach (var text in new[] { "NaN", "1e999", "-Infinity", "abc" })
        {
            input.Text = text;
            page.CollectSettingsFromUi();
            Require(tool.Slots[1].DelaySeconds == previous, "收集非法输入不能污染原槽位");
            input.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Require(tool.Slots[1].DelaySeconds == previous &&
                    KeyLoopPage.TryParseDelay(input.Text, out var shown) && shown == previous,
                "失焦提交应恢复上次有效值");
            var saved = storage.LoadSettings()["keyloop"]!["slots"]![1]!["delay_s"]!.GetValue<double>();
            Require(saved == previous, "未启用槽位的非法输入也不能覆盖有效配置");
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
