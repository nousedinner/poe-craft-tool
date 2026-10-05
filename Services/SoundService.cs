using System.Windows;
using System.Windows.Media;

namespace ShiKe.Services;

public sealed record SoundPlayRequestResult(bool Accepted, string Message);

public sealed record SoundPlaybackStatus(bool Success, string Message);

/// <summary>
/// 音效服务（对齐 Python 版 auto_operator.py _play_sound）。
/// 查找顺序：原名精确 → stem 同名不同后缀（.wav/.mp3/.ogg/.flac）→ 放弃。
/// MediaPlayer 必须在 UI 线程创建和调用；后台只排队提交，不等待 UI 线程。
/// </summary>
public sealed class SoundService : IDisposable
{
    private readonly string _soundDir;
    private MediaPlayer? _player; // 延迟到 UI 线程创建
    private string? _pendingFile;
    private volatile bool _disposed;

    private static readonly string[] AudioExts = [".wav", ".mp3", ".ogg", ".flac"];

    public SoundService(string? soundDirectory = null)
    {
        _soundDir = Path.GetFullPath(soundDirectory ?? Path.Combine(AppContext.BaseDirectory, "sounds"));
    }

    public string SoundDirectory => _soundDir;
    public event Action<SoundPlaybackStatus>? PlaybackStatusChanged;

    /// <summary>播放音效文件（后台调用排队交给 UI，不阻塞工具停止或退出）。</summary>
    public void Play(string? soundFileName)
    {
        _ = TryPlay(soundFileName);
    }

    /// <summary>
    /// 校验并提交播放请求。Accepted 只表示请求已被接受或排队；真实打开成功或失败由
    /// PlaybackStatusChanged 反馈，不能在 Open() 返回时提前记录“播放成功”。
    /// </summary>
    public SoundPlayRequestResult TryPlay(string? soundFileName)
    {
        if (_disposed)
            return new SoundPlayRequestResult(false, "音效服务已关闭");
        if (!TryResolveSoundFile(soundFileName, out var file, out var resolveError))
        {
            Diag.Log($"[音效] {resolveError}");
            return new SoundPlayRequestResult(false, resolveError);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            const string message = "音效播放环境尚未就绪";
            Diag.Log($"[音效] {message}");
            return new SoundPlayRequestResult(false, message);
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                return PlayCore(file);
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_disposed && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                        _ = PlayCore(file);
                }));
            }
            return new SoundPlayRequestResult(true, $"已提交音效：{Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            Diag.Log($"[音效] 异常: {ex.Message}");
            return new SoundPlayRequestResult(false, $"无法提交音效播放：{ex.Message}");
        }
    }

    private SoundPlayRequestResult PlayCore(string file)
    {
        if (_disposed)
            return new SoundPlayRequestResult(false, "音效服务已关闭");
        try
        {
            if (_player is null)
            {
                _player = new MediaPlayer();
                _player.MediaOpened += Player_MediaOpened;
                _player.MediaFailed += Player_MediaFailed;
            }
            _player.Stop();
            _player.Close();
            _pendingFile = file;
            _player.Open(new Uri(file, UriKind.Absolute));
            return new SoundPlayRequestResult(true, $"正在打开音效：{Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            Diag.Log($"[音效] PlayCore 异常: {ex.Message}");
            var message = $"音效打开失败：{ex.Message}";
            PublishPlaybackStatus(false, message);
            return new SoundPlayRequestResult(false, message);
        }
    }

    private void Player_MediaOpened(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var name = Path.GetFileName(_pendingFile ?? string.Empty);
        try
        {
            _player?.Play();
            Diag.Log($"[音效] 播放成功: {name}");
            PublishPlaybackStatus(true, $"正在播放：{name}");
        }
        catch (Exception ex)
        {
            Diag.Log($"[音效] 播放启动失败: {ex.Message}");
            PublishPlaybackStatus(false, $"音效播放失败：{ex.Message}");
        }
    }

    private void Player_MediaFailed(object? sender, ExceptionEventArgs e)
    {
        if (_disposed) return;
        var name = Path.GetFileName(_pendingFile ?? string.Empty);
        var detail = e.ErrorException?.Message ?? "系统无法解码该音频";
        Diag.Log($"[音效] MediaFailed: {name}: {detail}");
        PublishPlaybackStatus(false, $"无法播放 {name}：{detail}");
    }

    private void PublishPlaybackStatus(bool success, string message)
    {
        try { PlaybackStatusChanged?.Invoke(new SoundPlaybackStatus(success, message)); }
        catch (Exception ex) { Diag.Log($"[音效] 状态回调失败: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            DisposeCore();
        else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            dispatcher.BeginInvoke(new Action(DisposeCore));
    }

    private void DisposeCore()
    {
        var player = _player;
        _player = null;
        _pendingFile = null;
        PlaybackStatusChanged = null;
        if (player is null) return;
        player.MediaOpened -= Player_MediaOpened;
        player.MediaFailed -= Player_MediaFailed;
        try { player.Stop(); }
        finally { player.Close(); }
    }

    /// <summary>扫描 sounds/ 目录音频文件（设置页下拉用，返回文件名）。</summary>
    public List<string> ScanSounds()
    {
        if (!Directory.Exists(_soundDir))
            return [];
        try
        {
            return Directory.GetFiles(_soundDir)
                            .Where(f => AudioExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                            .Select(f => Path.GetFileName(f)!)
                            .OrderBy(n => n, StringComparer.Ordinal)
                            .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Log($"[音效] 扫描目录失败: {ex.Message}");
            return [];
        }
    }

    internal bool TryResolveSoundFile(string? name, out string file, out string error)
    {
        file = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "尚未选择音效文件";
            return false;
        }
        name = name.Trim();
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "音效名称不能包含路径或非法文件名字符";
            return false;
        }
        if (!AudioExts.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
        {
            error = $"不支持的音效扩展名：{Path.GetExtension(name)}";
            return false;
        }
        if (!Directory.Exists(_soundDir))
        {
            error = $"音效目录不存在：{_soundDir}";
            return false;
        }

        var exact = Path.GetFullPath(Path.Combine(_soundDir, name));
        if (!IsInsideSoundDirectory(exact))
        {
            error = "音效路径超出 sounds 目录";
            return false;
        }
        if (File.Exists(exact))
        {
            file = exact;
            error = string.Empty;
            return true;
        }
        var stem = Path.GetFileNameWithoutExtension(name);
        foreach (var ext in AudioExts)
        {
            var candidate = Path.GetFullPath(Path.Combine(_soundDir, stem + ext));
            if (File.Exists(candidate))
            {
                file = candidate;
                error = string.Empty;
                return true;
            }
        }
        error = $"未找到音效文件：{name}";
        return false;
    }

    private bool IsInsideSoundDirectory(string path)
    {
        var rootPrefix = _soundDir.EndsWith(Path.DirectorySeparatorChar)
            ? _soundDir
            : _soundDir + Path.DirectorySeparatorChar;
        return path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
