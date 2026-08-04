using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace ShiKe.Services;

/// <summary>
/// JSON 存储服务（对齐 Python 版 storage.py）。
/// 数据目录：exe 旁 data/（Python frozen 行为一致）。
/// - settings.json：按抽屉分节（旧版平铺格式自动迁移）
/// - coordinates.json / rules.json：兼容旧版格式
/// - presets/：data/presets/*.json
/// </summary>
public sealed class StorageService
{
    public string DataDir { get; }

    private string SettingsPath => Path.Combine(DataDir, "settings.json");
    private string CoordinatesPath => Path.Combine(DataDir, "coordinates.json");
    private string RulesPath => Path.Combine(DataDir, "rules.json");
    private string PresetsDir => Path.Combine(DataDir, "presets");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public StorageService()
    {
        DataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(DataDir);
    }

    // ── settings.json：分节加载 + 旧版迁移 ──

    /// <summary>
    /// 加载 settings.json。若为旧版平铺格式（顶层无分节键）则自动迁移：
    /// 平铺键 → 分节 → 写回新格式 → 旧文件改名 settings.json.bak。
    /// 文件不存在时返回空分节对象（抽屉各自用 SettingsDefaults 兜底）。
    /// </summary>
    public JsonObject LoadSettings()
    {
        if (!File.Exists(SettingsPath))
            return [];

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? [];
            if (root.ContainsKey("craft") || root.ContainsKey("clicker") || root.ContainsKey("host"))
                return root; // 已是分节格式

            // 旧版平铺 → 迁移
            var migrated = MigrateLegacySettings(root);
            SaveSettings(migrated);
            File.Move(SettingsPath, SettingsPath + ".bak", overwrite: true);
            return migrated;
        }
        catch (JsonException)
        {
            return []; // 损坏文件：不覆盖，返回空
        }
    }

    public void SaveSettings(JsonObject root)
    {
        File.WriteAllText(SettingsPath, root.ToJsonString(Indented));
    }

    /// <summary>旧版平铺 settings 迁移映射（ARCHITECTURE §3.5）。</summary>
    private static JsonObject MigrateLegacySettings(JsonObject flat)
    {
        var host = new JsonObject();
        var hostHotkeys = new JsonObject();
        var craft = new JsonObject();
        var clicker = new JsonObject();
        var keyloop = new JsonObject();
        var hideout = new JsonObject();

        void MoveTo(JsonObject target, string oldKey, string newKey)
        {
            if (flat.TryGetPropertyValue(oldKey, out var v) && v is not null)
                target[newKey] = v.DeepClone();
        }

        MoveTo(hostHotkeys, "hotkey_start", "start");
        MoveTo(hostHotkeys, "hotkey_stop", "stop");
        MoveTo(hostHotkeys, "hotkey_set_coord", "coordinate");
        MoveTo(host, "target_process", "target_process");
        host["hotkeys"] = hostHotkeys;

        MoveTo(craft, "delay_ms", "delay_ms");
        MoveTo(craft, "sound_enabled", "sound_enabled");
        MoveTo(craft, "popup_enabled", "popup_enabled");
        MoveTo(craft, "clipboard_unchanged_threshold", "clipboard_unchanged_threshold");
        MoveTo(craft, "selected_sound", "selected_sound");
        MoveTo(craft, "custom_sound", "custom_sound");

        MoveTo(clicker, "clicker_hotkey", "hotkey");
        MoveTo(clicker, "clicker_hold_hotkey", "hold_hotkey");
        MoveTo(clicker, "clicker_interval_ms", "interval_ms");
        MoveTo(clicker, "clicker_button", "button");
        MoveTo(clicker, "clicker_mode", "mode");

        MoveTo(keyloop, "key_loop_hotkey", "hotkey");
        MoveTo(keyloop, "key_loop_slots", "slots");

        MoveTo(hideout, "hideout_hotkey", "hotkey");
        MoveTo(hideout, "hideout_enabled", "enabled");

        return new JsonObject
        {
            ["craft"] = craft,
            ["clicker"] = clicker,
            ["keyloop"] = keyloop,
            ["hideout"] = hideout,
            ["host"] = host,
        };
    }

    // ── coordinates.json（兼容旧版）──

    /// <summary>加载坐标：{slotId: Point}。文件缺失/损坏返回空字典（抽屉自行兜底 null）。</summary>
    public Dictionary<string, Point> LoadCoordinates()
    {
        var result = new Dictionary<string, Point>();
        if (!File.Exists(CoordinatesPath)) return result;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(CoordinatesPath)) as JsonObject;
            if (root is null) return result;
            foreach (var (key, val) in root)
            {
                if (val is JsonArray arr && arr.Count >= 2 &&
                    arr[0] is JsonValue xv && arr[1] is JsonValue yv)
                {
                    var x = xv.GetValue<int>();
                    var y = yv.GetValue<int>();
                    result[key] = new Point(x, y);
                }
            }
        }
        catch (JsonException) { }
        return result;
    }

    public void SaveCoordinates(Dictionary<string, Point> coords)
    {
        var obj = new JsonObject();
        foreach (var (key, pt) in coords)
            obj[key] = new JsonArray((int)pt.X, (int)pt.Y);
        File.WriteAllText(CoordinatesPath, obj.ToJsonString(Indented));
    }

    // ── rules.json（兼容旧版，阶段3 使用）──

    public JsonObject? LoadRules()
    {
        if (!File.Exists(RulesPath)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(RulesPath)) as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    public void SaveRules(JsonObject rules)
    {
        File.WriteAllText(RulesPath, rules.ToJsonString(Indented));
    }

    // ── presets ──

    public List<string> ListPresets()
    {
        Directory.CreateDirectory(PresetsDir);
        return Directory.GetFiles(PresetsDir, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(n => n is not null)
                        .Select(n => n!)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList();
    }

    public JsonObject? LoadPreset(string name)
    {
        var path = Path.Combine(PresetsDir, name + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public void SavePreset(string name, JsonObject preset)
    {
        Directory.CreateDirectory(PresetsDir);
        File.WriteAllText(Path.Combine(PresetsDir, name + ".json"), preset.ToJsonString(Indented));
    }

    public void DeletePreset(string name)
    {
        var path = Path.Combine(PresetsDir, name + ".json");
        if (File.Exists(path)) File.Delete(path);
    }
}
