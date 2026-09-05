using System.Windows;
using System.Windows.Media;

namespace ShiKe.Services;

/// <summary>
/// 音效服务（对齐 Python 版 auto_operator.py _play_sound）。
/// 查找顺序：原名精确 → stem 同名不同后缀（.wav/.mp3/.ogg/.flac）→ 放弃。
/// MediaPlayer 必须在 UI 线程创建和调用。通过 Dispatcher.Invoke 同步执行。
/// </summary>
public sealed class SoundService
{
    private readonly string _soundDir;
    private MediaPlayer? _player; // 延迟到 UI 线程创建

    private static readonly string[] AudioExts = [".wav", ".mp3", ".ogg", ".flac"];

    public SoundService()
    {
        _soundDir = Path.Combine(AppContext.BaseDirectory, "sounds");
    }

    public string SoundDirectory => _soundDir;

    /// <summary>播放音效文件（任意线程可调，通过 Dispatcher.Invoke 同步在 UI 线程执行）。</summary>
    public void Play(string? soundFileName)
    {
        if (string.IsNullOrWhiteSpace(soundFileName))
            return;
        var file = FindSoundFile(soundFileName);
        if (file is null)
        {
            Diag.Log($"[音效] 文件未找到: {soundFileName}");
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            Diag.Log("[音效] Dispatcher 不可用");
            return;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                PlayCore(file);
            }
            else
            {
                // 同步调用：阻塞引擎线程直到 UI 线程完成播放启动
                dispatcher.Invoke(() => PlayCore(file));
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"[音效] 异常: {ex.Message}");
        }
    }

    private void PlayCore(string file)
    {
        try
        {
            _player ??= new MediaPlayer();
            _player.Open(new Uri(file, UriKind.Absolute));
            _player.Play();
            Diag.Log($"[音效] 播放成功: {Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            Diag.Log($"[音效] PlayCore 异常: {ex.Message}");
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
        {
            Diag.Log($"[音效] 目录不存在: {_soundDir}");
            return null;
        }
        var exact = Path.Combine(_soundDir, name);
        if (File.Exists(exact))
            return exact;
        var stem = Path.GetFileNameWithoutExtension(name);
        foreach (var ext in AudioExts)
        {
            var candidate = Path.Combine(_soundDir, stem + ext);
            if (File.Exists(candidate))
                return candidate;
        }
        Diag.Log($"[音效] 未找到匹配文件: {name} (目录: {_soundDir})");
        return null;
    }
}
