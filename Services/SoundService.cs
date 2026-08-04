using System.Windows.Media;

namespace ShiKe.Services;

/// <summary>
/// 音效服务（对齐 Python 版 auto_operator.py _play_sound）。
/// 查找顺序：原名精确 → stem 同名不同后缀（.wav/.mp3/.ogg/.flac）→ 放弃。
/// 注意：selected_sound 默认存 "default_ding.wav"，但 sounds/ 实际是 default_ding.mp3 → stem 回退兜底。
/// 播放用 MediaPlayer（wav/mp3/wma 原生；ogg/flac 后期按需加 NAudio）。
/// </summary>
public sealed class SoundService
{
    private readonly MediaPlayer _player = new();
    private readonly string _soundDir;

    private static readonly string[] AudioExts = [".wav", ".mp3", ".ogg", ".flac"];

    public SoundService()
    {
        _soundDir = Path.Combine(AppContext.BaseDirectory, "sounds");
    }

    /// <summary>播放音效文件（找不到静默放弃，对齐 Python）。</summary>
    public void Play(string? soundFileName)
    {
        if (string.IsNullOrWhiteSpace(soundFileName))
            return;
        var file = FindSoundFile(soundFileName);
        if (file is null)
            return;
        try
        {
            _player.Open(new Uri(file));
            _player.Play();
        }
        catch (Exception)
        {
            // 播放失败静默（对齐 Python）
        }
    }

    /// <summary>扫描 sounds/ 目录音频文件（设置页下拉用，返回文件名）。</summary>
    public List<string> ScanSounds()
    {
        if (!Directory.Exists(_soundDir))
            return [];
        return Directory.GetFiles(_soundDir)
                        .Where(f => AudioExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                        .Select(f => Path.GetFileName(f)!)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList();
    }

    private string? FindSoundFile(string name)
    {
        if (!Directory.Exists(_soundDir))
            return null;
        // 第一步：原名精确查找
        var exact = Path.Combine(_soundDir, name);
        if (File.Exists(exact))
            return exact;
        // 第二步：去掉后缀按 stem 匹配任意音频后缀（Python:792-802 逻辑）
        var stem = Path.GetFileNameWithoutExtension(name);
        foreach (var ext in AudioExts)
        {
            var candidate = Path.Combine(_soundDir, stem + ext);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
