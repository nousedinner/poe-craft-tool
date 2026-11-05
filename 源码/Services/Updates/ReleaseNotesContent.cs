using System.Text;

namespace ShiKe.Services.Updates;

/// <summary>只在自动更新提交成功后按需读取内置纯文本，不依赖网络或用户配置。</summary>
internal static class ReleaseNotesContent
{
    internal const string ResourceName = "ShiKe.ReleaseNotes.txt";
    internal const int MaximumCharacters = 12000;

    internal static string CurrentVersion => AppIdentity.Version.ToString(3);

    internal static string? Read()
        => Read(() => typeof(Bootstrapper).Assembly.GetManifestResourceStream(ResourceName));

    // 读取失败只影响可选说明，不得使已经提交的更新退出或回滚。
    internal static string? Read(Func<Stream?> openResource, Action<string>? reportFailure = null)
    {
        var report = reportFailure ?? Diag.Log;
        try
        {
            using var stream = openResource();
            if (stream is null)
            {
                Report(report, "[版本说明] 内置资源缺失，已跳过显示。");
                return null;
            }

            using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var buffer = new char[MaximumCharacters + 1];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = Parse(new string(buffer, 0, length));
            if (text is null) Report(report, "[版本说明] 内容为空、格式无效或超过长度限制，已跳过显示。");
            return text;
        }
        catch (Exception error)
        {
            Report(report, $"[版本说明] 读取失败，已跳过显示：{error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    internal static string? Parse(string? text)
    {
        if (text is null || text.Length > MaximumCharacters) return null;
        var normalized = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Trim();
        if (normalized.Length == 0) return null;
        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (char.IsControl(character) && character is not ('\n' or '\t')) return null;
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= normalized.Length || !char.IsLowSurrogate(normalized[++index])) return null;
            }
            else if (char.IsLowSurrogate(character)) return null;
        }
        return normalized;
    }

    internal static bool ShouldShow(bool isAutomaticUpdate, bool healthConfirmed, bool appClosing,
        bool alreadyShown, string? text)
        => isAutomaticUpdate && healthConfirmed && !appClosing && !alreadyShown &&
           !string.IsNullOrWhiteSpace(text);

    private static void Report(Action<string> report, string message)
    {
        try { report(message); }
        catch { /* 可选说明的诊断输出同样不能破坏已提交更新。 */ }
    }
}

/// <summary>仅本次进程的一次展示状态；会话提交负责跨启动约束，不写额外 seen 文件。</summary>
internal sealed class ReleaseNotesDisplayState
{
    private readonly object _gate = new();
    private bool _showing;
    private bool _shown;

    internal bool AlreadyShown { get { lock (_gate) return _shown; } }

    internal bool TryBegin(bool isAutomaticUpdate, bool healthConfirmed, bool appClosing, string? text)
    {
        lock (_gate)
        {
            if (_showing || !ReleaseNotesContent.ShouldShow(isAutomaticUpdate, healthConfirmed,
                    appClosing, _shown, text)) return false;
            _showing = true;
            return true;
        }
    }

    internal void Complete(bool renderedOnce)
    {
        lock (_gate)
        {
            if (!_showing) return;
            _shown |= renderedOnce;
            _showing = false;
        }
    }
}
