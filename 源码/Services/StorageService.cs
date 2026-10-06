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
    private readonly object _settingsGate = new();

    public string DataDir { get; }

    private string SettingsPath => Path.Combine(DataDir, "settings.json");
    private string CoordinatesPath => Path.Combine(DataDir, "coordinates.json");
    private string RulesPath => Path.Combine(DataDir, "rules.json");
    private string PresetsDir => Path.Combine(DataDir, "presets");

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public StorageService(string? dataDir = null)
    {
        DataDir = dataDir ?? Path.Combine(AppContext.BaseDirectory, "data");
        try { Directory.CreateDirectory(DataDir); }
        catch (Exception ex) when (IsFileError(ex)) { throw new StorageException("创建数据目录", DataDir, ex); }
    }

    // ── settings.json：分节加载 + 旧版迁移 ──

    /// <summary>
    /// 加载 settings.json。若为旧版平铺格式（顶层无分节键）则自动迁移：
    /// 平铺键 → 分节 → 写回新格式 → 旧文件改名 settings.json.bak。
    /// 文件不存在时返回空分节对象（抽屉各自用 SettingsDefaults 兜底）。
    /// </summary>
    public JsonObject LoadSettings()
    {
        lock (_settingsGate)
            return LoadSettingsCore();
    }

    private JsonObject LoadSettingsCore()
    {
        try
        {
            var root = ReadJsonObject(SettingsPath, ValidateSettings);
            if (root is null) return [];
            if (IsSectionedSettings(root))
                return root; // 已是分节格式

            // 旧版平铺 → 先备份原始文件，再原子写入迁移结果。
            // 不可先覆盖再 Move，否则被移走的是新文件而不是旧文件。
            var migrated = MigrateLegacySettings(root);
            ValidateSettings(migrated);
            File.Copy(SettingsPath, SettingsPath + ".bak", overwrite: true);
            WriteJsonAtomic(SettingsPath, migrated);
            return migrated;
        }
        catch (StorageException) { throw; }
        catch (Exception ex) when (IsFileError(ex))
        {
            throw new StorageException("加载或迁移设置", SettingsPath, ex);
        }
    }

    public void SaveSettings(JsonObject root)
    {
        lock (_settingsGate)
            WriteJsonProtected(SettingsPath, root, ValidateSettings);
    }

    /// <summary>在同一锁内读取、修改并原子保存 settings，避免不同设置入口互相覆盖。</summary>
    public void UpdateSettings(Action<JsonObject> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_settingsGate)
        {
            var root = LoadSettingsCore();
            update(root);
            WriteJsonProtected(SettingsPath, root, ValidateSettings);
        }
    }

    private static bool IsSectionedSettings(JsonObject root)
        => root.ContainsKey("craft") || root.ContainsKey("clicker") || root.ContainsKey("keyloop") ||
           root.ContainsKey("hideout") || root.ContainsKey("host") || root.ContainsKey("schema_version");

    private static bool IsFileError(Exception ex)
        => ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException;

    private static JsonObject? ReadJsonObject(string path, Action<JsonObject>? validate = null)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("JSON 顶层必须是对象，不能是数组、空值或其他类型");
            validate?.Invoke(root);
            return root;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (IsFileError(ex)) { throw new StorageException("读取文件", path, ex); }
    }

    private static void WriteJsonProtected(string path, JsonObject root, Action<JsonObject>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        try
        {
            validate?.Invoke(root);
            // 保存也重新读取现有文件，防止启动后文件损坏或被占用时覆盖成默认配置。
            ReadJsonObject(path, validate);
            WriteJsonAtomic(path, root);
        }
        catch (StorageException ex) { throw new StorageException("保存文件", path, ex.InnerException ?? ex); }
        catch (Exception ex) when (IsFileError(ex)) { throw new StorageException("保存文件", path, ex); }
    }

    private static void ValidateSettings(JsonObject root)
    {
        if (!IsSectionedSettings(root))
        {
            ValidateIntFields(root, "delay_ms", "clipboard_unchanged_threshold", "clicker_interval_ms");
            ValidateBoolFields(root, "sound_enabled", "popup_enabled", "auto_detect_poe", "hideout_enabled");
            ValidateStringFields(root, "hotkey_start", "hotkey_stop", "hotkey_set_coord", "target_process",
                "selected_sound", "clicker_hotkey", "clicker_hold_hotkey", "clicker_button", "clicker_mode",
                "key_loop_hotkey", "hideout_hotkey");
            ValidateSlots(root, "key_loop_slots");
            // 直接保存也须检查旧平铺字段；只映射到内存，不触发备份或写入。
            ValidateSettings(MigrateLegacySettings(root));
            return;
        }
        foreach (var key in new[] { "craft", "clicker", "keyloop", "hideout", "host" })
            if (root.TryGetPropertyValue(key, out var section) && section is not JsonObject)
                throw new InvalidDataException($"设置分节“{key}”必须是对象");
        ValidateIntFields(root, "schema_version");
        if (root["host"] is JsonObject host)
        {
            ValidateStringFields(host, "target_process");
            ValidateBoolFields(host, "auto_detect_poe");
            if (host.TryGetPropertyValue("hotkeys", out var hotkeys))
            {
                if (hotkeys is not JsonObject keys) throw new InvalidDataException("host.hotkeys 必须是对象");
                ValidateStringFields(keys, "start", "stop", "coordinate");
            }
        }
        if (root["craft"] is JsonObject craft)
        {
            ValidateIntFields(craft, "delay_ms", "clipboard_unchanged_threshold");
            ValidateBoolFields(craft, "enabled", "sound_enabled", "popup_enabled", "mode2_scour_alch", "use_exalt");
            ValidateStringFields(craft, "selected_sound");
        }
        if (root["clicker"] is JsonObject clicker)
        {
            ValidateIntFields(clicker, "interval_ms");
            ValidateBoolFields(clicker, "enabled", "notifications_enabled");
            ValidateStringFields(clicker, "hotkey", "hold_hotkey", "button", "mode");
        }
        if (root["hideout"] is JsonObject hideout)
        {
            ValidateBoolFields(hideout, "enabled");
            ValidateStringFields(hideout, "hotkey", "command");
        }
        if (root["keyloop"] is JsonObject keyLoop)
        {
            ValidateBoolFields(keyLoop, "enabled", "notifications_enabled");
            ValidateStringFields(keyLoop, "hotkey");
            ValidateSlots(keyLoop, "slots");
        }
    }

    private static void ValidateSlots(JsonObject root, string propertyName)
    {
        if (!root.TryGetPropertyValue(propertyName, out var slotValue)) return;
        if (slotValue is not JsonArray slots) throw new InvalidDataException($"“{propertyName}”必须是槽位数组");
        foreach (var slotValueItem in slots)
        {
            if (slotValueItem is not JsonObject slot) throw new InvalidDataException("按键循环槽位必须是对象");
            ValidateBoolFields(slot, "enabled");
            ValidateStringFields(slot, "key");
            foreach (var key in new[] { "delay_s", "delay" })
            {
                if (slot.TryGetPropertyValue(key, out var value) &&
                    (value is not JsonValue || value.GetValueKind() != JsonValueKind.Number ||
                     !JsonSerializer.SerializeToElement(value).TryGetDouble(out var delay) || !double.IsFinite(delay)))
                    throw new InvalidDataException($"按键循环槽位的“{key}”必须是有限数值");
            }
        }
    }

    private static void ValidateIntFields(JsonObject root, params string[] keys)
    {
        foreach (var key in keys)
            if (root.TryGetPropertyValue(key, out var value) && (value is not JsonValue v || !v.TryGetValue<int>(out _)))
                throw new InvalidDataException($"“{key}”必须是整数");
    }

    private static void ValidateBoolFields(JsonObject root, params string[] keys)
    {
        foreach (var key in keys)
            if (root.TryGetPropertyValue(key, out var value) && (value is not JsonValue v || !v.TryGetValue<bool>(out _)))
                throw new InvalidDataException($"“{key}”必须是布尔值");
    }

    private static void ValidateStringFields(JsonObject root, params string[] keys)
    {
        foreach (var key in keys)
            if (root.TryGetPropertyValue(key, out var value) && (value is not JsonValue v || !v.TryGetValue<string>(out _)))
                throw new InvalidDataException($"“{key}”必须是字符串");
    }

    private static void ValidateRules(JsonObject root)
    {
        ValidateIntFields(root, "primary_hit_count", "secondary_hit_count");
        foreach (var key in new[] { "primary_hit_count", "secondary_hit_count" })
            if (root[key] is JsonValue hits && hits.TryGetValue<int>(out var count) && count < 0)
                throw new InvalidDataException($"“{key}”不能为负数");
        if (root.TryGetPropertyValue("single_currency", out var currency) &&
            (currency is not JsonValue cv || !cv.TryGetValue<string>(out _)))
            throw new InvalidDataException("single_currency 必须是通货名称字符串");
        if (root.TryGetPropertyValue("mode", out var mode) &&
            (mode is not JsonValue mv ||
             !(mv.TryGetValue<int>(out var modeNumber) && modeNumber is >= 1 and <= 3 ||
               mv.TryGetValue<string>(out var modeName) && modeName is "single" or "alt_aug" or "alt_aug_regal")))
            throw new InvalidDataException("mode 必须是 1～3，或 single / alt_aug / alt_aug_regal");
        foreach (var key in new[] { "primary_affixes", "secondary_affixes", "exclude_affixes" })
        {
            if (!root.ContainsKey(key)) continue;
            if (root[key] is not JsonArray entries)
                throw new InvalidDataException($"“{key}”必须是词缀数组");
            foreach (var entry in entries)
            {
                if (entry is JsonValue text && text.TryGetValue<string>(out _)) continue;
                if (entry is JsonObject obj && obj["text"] is JsonValue tv && tv.TryGetValue<string>(out _)) continue;
                throw new InvalidDataException($"“{key}”中的词缀必须是字符串或含 text 字符串的对象");
            }
        }
    }

    private static Point ReadCoordinate(string key, JsonNode? value)
    {
        if (value is JsonArray arr && arr.Count >= 2 &&
            arr[0] is JsonValue xv && xv.TryGetValue<int>(out var x) &&
            arr[1] is JsonValue yv && yv.TryGetValue<int>(out var y))
            return new Point(x, y);
        throw new InvalidDataException($"坐标“{key}”必须是至少包含两个整数的数组");
    }

    private static void ValidateCoordinates(JsonObject root)
    {
        foreach (var (key, value) in root) ReadCoordinate(key, value);
    }

    private static void WriteJsonAtomic(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("无法确定存储目录");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, root.ToJsonString(Indented));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception ex) when (IsFileError(ex))
            {
                Diag.Log($"[存储] 临时文件清理失败: {tempPath}, {ex.Message}");
            }
        }
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
        MoveTo(host, "auto_detect_poe", "auto_detect_poe");
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

    /// <summary>加载坐标：{slotId: Point}。缺失返回空字典；损坏或无法读取必须报告，不能返回部分坐标。</summary>
    public Dictionary<string, Point> LoadCoordinates()
    {
        var result = new Dictionary<string, Point>();
        var root = ReadJsonObject(CoordinatesPath, ValidateCoordinates);
        if (root is not null)
            foreach (var (key, value) in root) result[key] = ReadCoordinate(key, value);
        return result;
    }

    public void SaveCoordinates(Dictionary<string, Point> coords)
    {
        var obj = new JsonObject();
        foreach (var (key, pt) in coords)
            obj[key] = new JsonArray((int)pt.X, (int)pt.Y);
        WriteJsonProtected(CoordinatesPath, obj, ValidateCoordinates);
    }

    // ── rules.json（兼容旧版，阶段3 使用）──

    public JsonObject? LoadRules()
    {
        return ReadJsonObject(RulesPath, ValidateRules);
    }

    public void SaveRules(JsonObject rules)
    {
        WriteJsonProtected(RulesPath, rules, ValidateRules);
    }

    // ── presets ──

    public List<string> ListPresets()
    {
        try
        {
            Directory.CreateDirectory(PresetsDir);
            return Directory.GetFiles(PresetsDir, "*.json")
                            .Select(Path.GetFileNameWithoutExtension)
                            .Where(n => n is not null)
                            .Select(n => n!)
                            .OrderBy(n => n, StringComparer.Ordinal)
                            .ToList();
        }
        catch (Exception ex) when (IsFileError(ex)) { throw new StorageException("读取预设目录", PresetsDir, ex); }
    }

    public JsonObject? LoadPreset(string name)
    {
        var path = GetPresetPath(name);
        return ReadJsonObject(path, ValidateRules);
    }

    public void SavePreset(string name, JsonObject preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        WriteJsonProtected(GetPresetPath(name), preset, ValidateRules);
    }

    public void DeletePreset(string name)
    {
        var path = GetPresetPath(name);
        try { File.Delete(path); }
        catch (Exception ex) when (IsFileError(ex)) { throw new StorageException("删除预设", path, ex); }
    }

    /// <summary>验证用户可见的预设名称；服务入口仍会再次验证，不能只依赖 UI。</summary>
    public static bool TryValidatePresetName(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "预设名称不能为空";
            return false;
        }
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal))
        {
            error = "预设名称不能以空格开头或结尾";
            return false;
        }
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            error = "预设名称不能包含路径或文件名非法字符";
            return false;
        }
        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            error = "预设名称不能以点或空格结尾";
            return false;
        }

        var deviceStem = name.Split('.', 2)[0];
        if (IsReservedWindowsDeviceName(deviceStem))
        {
            error = $"“{name}”是 Windows 保留名称，请换一个名称";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private string GetPresetPath(string name)
    {
        if (!TryValidatePresetName(name, out var error))
            throw new ArgumentException(error, nameof(name));

        var root = Path.GetFullPath(PresetsDir);
        var path = Path.GetFullPath(Path.Combine(root, name + ".json"));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("预设路径超出预设目录", nameof(name));
        return path;
    }

    private static bool IsReservedWindowsDeviceName(string stem)
    {
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        if (stem.Length != 4) return false;
        var prefix = stem[..3];
        return (prefix.Equals("COM", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }
}
