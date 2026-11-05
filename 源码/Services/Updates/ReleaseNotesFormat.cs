using System.Text.RegularExpressions;

namespace ShiKe.Services.Updates;

/// <summary>运行时与发布准备共用的版本说明格式；版本头不进入用户看到的正文。</summary>
internal static class ReleaseNotesFormat
{
    internal const int MaximumBodyCharacters = 12000;
    internal const int MaximumDocumentCharacters = MaximumBodyCharacters + 128;
    private static readonly Regex VersionPattern = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z");

    internal static string? Parse(string? text, string expectedVersion)
    {
        if (text is null || text.Length > MaximumDocumentCharacters || !VersionPattern.IsMatch(expectedVersion)) return null;
        var normalized = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var newline = normalized.IndexOf('\n');
        if (newline < 0 || normalized[..newline] != "Version: " + expectedVersion) return null;
        var body = normalized[(newline + 1)..].Trim();
        if (body.Length == 0 || body.Length > MaximumBodyCharacters) return null;
        for (var index = 0; index < body.Length; index++)
        {
            var character = body[index];
            if (char.IsControl(character) && character is not ('\n' or '\t')) return null;
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= body.Length || !char.IsLowSurrogate(body[++index])) return null;
            }
            else if (char.IsLowSurrogate(character)) return null;
        }
        return body;
    }
}
